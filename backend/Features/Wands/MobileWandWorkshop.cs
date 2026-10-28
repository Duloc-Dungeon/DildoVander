using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Customers;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Wands;

internal sealed record MobileWandWorkshopAnswer(string QuestionId, int QuestionRevision,
    string Answer, string SourceMode = "text");
internal sealed record MobileWandWorkshopSkip(string QuestionId, int QuestionRevision);
internal sealed record MobileWandWorkshopCorrection(int AnswerRevision, string Answer,
    string SourceMode = "text");
internal sealed record MobileWandWorkshopStep(string QuestionId, int QuestionRevision, int Number,
    string Question, string[] BasedOnFactIds, string? AnswerId = null, int AnswerRevision = 0,
    string? Answer = null, bool Skipped = false, string? SourceMode = null, int Total = 3,
    string AuthoringStatus = "ready", bool UsedFallback = false);
internal sealed record MobileWandPublicResult(string WandId, string Name, string Material, string Core,
    string Temperament, string Lore, string DesignDescription, string Affinity, string Weakness,
    WandSpell[] Spells, string CombatArchetype, string CombatRulesVersion);
internal sealed record MobileWandWorkshopState(string Id, string ProfileId, long ProfileRevision, int Tier,
    string Status, MobileWandWorkshopStep? Question, MobileWandWorkshopStep[] Steps,
    MobileWandPublicResult? Wand, string? KeepsakeId = null,
    Kufr.Backend.Features.Keepsakes.OwnedWandArtworkReference? Artwork = null,
    string? ErrorCode = null, string? Verdict = null, bool CanRetry = false,
    string? RecoveryKind = null);
internal sealed record MobileWandWorkshopJob(string AccountId, string OperationId, int Tier,
    CharacterConversationSnapshot Profile, MobileWandWorkshopStep[] Steps, bool RetryRequested,
    string LaunchGroupId, long LaunchAccessRevision);
internal sealed record MobileWandQuestionJob(string AccountId, string OperationId, int Tier,
    CharacterConversationSnapshot Profile, MobileWandWorkshopStep Step, MobileWandWorkshopStep[] Steps,
    string LeaseId, string LaunchGroupId, long LaunchAccessRevision);
internal sealed record MobileWandWorkshopStart(string OperationId, bool Upgrade = false);

internal sealed partial class WandGenerationStore
{
    private sealed record WorkshopRow(string AccountId, string OperationId, string ProfileId,
        long ProfileRevision, int Tier, string Status, string ProfileJson, string StepsJson,
        string? KeepsakeId, string? ErrorCode, string? ArtworkError, bool RetryRequested,
        string? QuestionLease, long QuestionLeaseUntil, string LaunchGroupId, long LaunchAccessRevision);

    private void EnsureWorkshop(SqliteConnection connection)
    {
        Execute(connection, null, """
            CREATE TABLE IF NOT EXISTS mobile_wand_workshops(
                account_id TEXT NOT NULL, operation_id TEXT NOT NULL, profile_id TEXT NOT NULL,
                profile_revision INTEGER NOT NULL, tier INTEGER NOT NULL, status TEXT NOT NULL,
                launch_group_id TEXT NOT NULL DEFAULT '', launch_access_revision INTEGER NOT NULL DEFAULT 0,
                profile_json TEXT NOT NULL, steps_json TEXT NOT NULL, keepsake_id TEXT NULL,
                error_code TEXT NULL, artwork_error TEXT NULL, retry_requested INTEGER NOT NULL DEFAULT 0,
                question_lease TEXT NULL, question_lease_until INTEGER NOT NULL DEFAULT 0,
                updated_at INTEGER NOT NULL,
                PRIMARY KEY(account_id, operation_id),
                UNIQUE(account_id, profile_id, profile_revision, tier));
            """);
        using var retry = Command(connection, null,
            "SELECT COUNT(*) FROM pragma_table_info('mobile_wand_workshops') WHERE name='retry_requested'");
        if (Convert.ToInt32(retry.ExecuteScalar()) == 0)
            Execute(connection, null, "ALTER TABLE mobile_wand_workshops ADD COLUMN retry_requested INTEGER NOT NULL DEFAULT 0");
        using var artwork = Command(connection, null,
            "SELECT COUNT(*) FROM pragma_table_info('mobile_wand_workshops') WHERE name='artwork_error'");
        if (Convert.ToInt32(artwork.ExecuteScalar()) == 0)
            Execute(connection, null, "ALTER TABLE mobile_wand_workshops ADD COLUMN artwork_error TEXT NULL");
        using var questionLease = Command(connection, null,
            "SELECT COUNT(*) FROM pragma_table_info('mobile_wand_workshops') WHERE name='question_lease'");
        if (Convert.ToInt32(questionLease.ExecuteScalar()) == 0)
        {
            Execute(connection, null, "ALTER TABLE mobile_wand_workshops ADD COLUMN question_lease TEXT NULL");
            Execute(connection, null, "ALTER TABLE mobile_wand_workshops ADD COLUMN question_lease_until INTEGER NOT NULL DEFAULT 0");
        }
        using var groupColumn = Command(connection, null,
            "SELECT COUNT(*) FROM pragma_table_info('mobile_wand_workshops') WHERE name='launch_group_id'");
        if (Convert.ToInt32(groupColumn.ExecuteScalar()) == 0)
        {
            Execute(connection, null, "ALTER TABLE mobile_wand_workshops ADD COLUMN launch_group_id TEXT NOT NULL DEFAULT ''");
            Execute(connection, null, "ALTER TABLE mobile_wand_workshops ADD COLUMN launch_access_revision INTEGER NOT NULL DEFAULT 0");
        }
    }

    internal string? FindWorkshop(string accountId, string profileId, long revision, int tier)
    {
        using var connection = Open(); EnsureWorkshop(connection);
        using var command = Command(connection, null, """
            SELECT operation_id FROM mobile_wand_workshops
            WHERE account_id=$a AND profile_id=$p AND profile_revision=$r AND tier=$t
            """, ("$a", accountId), ("$p", profileId), ("$r", revision), ("$t", tier));
        return command.ExecuteScalar() as string;
    }

    internal bool WorkshopMatchesLaunchSnapshot(string accountId, string operationId,
        string groupId, long accessRevision)
    {
        using var connection = Open(); EnsureWorkshop(connection);
        using var command = Command(connection, null, """
            SELECT 1 FROM mobile_wand_workshops WHERE account_id=$a AND operation_id=$o
                AND launch_group_id=$g AND launch_access_revision=$r
            """, ("$a", accountId), ("$o", operationId), ("$g", groupId), ("$r", accessRevision));
        return command.ExecuteScalar() is not null;
    }

    internal string BeginWorkshop(string accountId, string operationId,
        CharacterConversationSnapshot profile, int tier, string launchGroupId, long launchAccessRevision)
    {
        using var connection = Open(); EnsureWorkshop(connection);
        using var transaction = connection.BeginTransaction(deferred: false);
        var existing = ReadWorkshop(connection, transaction, accountId, operationId);
        if (existing is not null)
        {
            if (existing.ProfileId != profile.ProfileId || existing.ProfileRevision != profile.Revision || existing.Tier != tier
                || existing.LaunchGroupId != launchGroupId || existing.LaunchAccessRevision != launchAccessRevision)
                throw new WandGenerationException("wand_operation_conflict");
            transaction.Commit();
            return operationId;
        }

        var steps = Enumerable.Range(1, 3).Select(number => new MobileWandWorkshopStep(
            Guid.NewGuid().ToString("N"), 1, number, "", [], AuthoringStatus: "pending")).ToArray();
        try
        {
            Execute(connection, transaction, """
                INSERT INTO mobile_wand_workshops(account_id,operation_id,profile_id,profile_revision,tier,
                    status,launch_group_id,launch_access_revision,profile_json,steps_json,updated_at)
                VALUES($a,$o,$p,$r,$t,'authoring',$group,$accessRevision,$profile,$steps,$now)
                """, ("$a", accountId), ("$o", operationId), ("$p", profile.ProfileId),
                ("$r", profile.Revision), ("$t", tier), ("$group", launchGroupId),
                ("$accessRevision", launchAccessRevision), ("$profile", Serialize(profile)),
                ("$steps", Serialize(steps)), ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19)
        {
            throw new WandGenerationException("wand_operation_conflict");
        }
        transaction.Commit();
        return operationId;
    }

    internal MobileWandWorkshopState ReadWorkshopState(string accountId, string operationId)
    {
        using var connection = Open(); EnsureWorkshop(connection);
        var row = ReadWorkshop(connection, null, accountId, operationId)
            ?? throw new WandGenerationException("wand_session_unavailable", 404);
        var steps = Deserialize<MobileWandWorkshopStep[]>(row.StepsJson);
        var generation = Read(accountId, operationId);
        var wand = generation?.SelectedWand;
        var status = row.Status;
        string? error = row.ErrorCode;
        if (status == "generating" && generation is not null)
        {
            if (generation.Status == "selected") status = "complete";
            else if (generation.Status == "unavailable") { status = "failed"; error = generation.ErrorCode; }
        }
        var active = status == "asking" ? steps.FirstOrDefault(step => step.Answer is null && !step.Skipped
            && step.AuthoringStatus == "ready") : null;
        var publicWand = wand is null ? null : new MobileWandPublicResult(wand.WandId, wand.Name,
            wand.Material, wand.Core, wand.Temperament, wand.Lore, wand.DesignDescription,
            wand.Affinity, wand.Weakness, wand.Spells, wand.CombatArchetype, wand.CombatRulesVersion);
        var state = new MobileWandWorkshopState(operationId, row.ProfileId, row.ProfileRevision, row.Tier,
            status, active, steps, publicWand, row.KeepsakeId,
            row.ArtworkError is null ? null : new Kufr.Backend.Features.Keepsakes.OwnedWandArtworkReference(
                "", null, "failed", true, row.ArtworkError), error);
        return state with
        {
            Verdict = status == "complete" ? state.VerdictText() : null,
            CanRetry = status == "failed" && (CanRetryGeneration(accountId, operationId)
                || CanRecoverWorkshopQuestion(accountId, operationId)),
            RecoveryKind = status == "failed" && CanRecoverWorkshopQuestion(accountId, operationId)
                ? "question_fallback" : status == "failed" && CanRetryGeneration(accountId, operationId)
                    ? "generation" : null
        };
    }

    internal MobileWandWorkshopJob[] PendingWorkshops()
    {
        using var connection = Open(); EnsureWorkshop(connection);
        using var command = Command(connection, null, """
            SELECT account_id,operation_id,tier,profile_json,steps_json,retry_requested,launch_group_id,launch_access_revision
            FROM mobile_wand_workshops WHERE status='generating' ORDER BY updated_at LIMIT 8
            """);
        using var reader = command.ExecuteReader();
        var jobs = new List<MobileWandWorkshopJob>();
        while (reader.Read()) jobs.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
            Deserialize<CharacterConversationSnapshot>(reader.GetString(3)),
            Deserialize<MobileWandWorkshopStep[]>(reader.GetString(4)), reader.GetInt32(5) != 0,
            reader.GetString(6), reader.GetInt64(7)));
        return jobs.ToArray();
    }

    internal void DeferWorkshop(string accountId, string operationId)
    {
        using var connection = Open(); EnsureWorkshop(connection);
        Execute(connection, null, """
            UPDATE mobile_wand_workshops SET updated_at=(
                SELECT MAX(updated_at)+1 FROM mobile_wand_workshops WHERE status='generating'
            ) WHERE account_id=$a AND operation_id=$o AND status='generating'
            """,
            ("$a", accountId), ("$o", operationId));
    }

    internal MobileWandQuestionJob? ClaimWorkshopQuestion()
    {
        using var connection = Open(); EnsureWorkshop(connection);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var query = Command(connection, transaction,
            "SELECT account_id,operation_id FROM mobile_wand_workshops WHERE status='authoring' ORDER BY updated_at LIMIT 16");
        using var reader = query.ExecuteReader();
        var candidates = new List<(string Account, string Operation)>();
        while (reader.Read()) candidates.Add((reader.GetString(0), reader.GetString(1)));
        reader.Close();
        foreach (var (account, operation) in candidates)
        {
            var row = ReadWorkshop(connection, transaction, account, operation);
            if (row is null) continue;
            if (row.QuestionLease is not null && row.QuestionLeaseUntil > DateTimeOffset.UtcNow.ToUnixTimeSeconds()) continue;
            if (row.QuestionLease is not null)
            {
                Execute(connection, transaction, "UPDATE mobile_wand_workshops SET status='failed',error_code='question_authoring_uncertain',question_lease=NULL,question_lease_until=0 WHERE account_id=$a AND operation_id=$o AND status='authoring'",
                    ("$a", account), ("$o", operation));
                continue;
            }
            var steps = Deserialize<MobileWandWorkshopStep[]>(row.StepsJson);
            var index = Array.FindIndex(steps, step => step.AuthoringStatus == "pending"
                && (step.Number == 1 || steps[step.Number - 2].Answer is not null || steps[step.Number - 2].Skipped));
            if (index < 0) continue;
            var lease = Guid.NewGuid().ToString("N");
            steps[index] = steps[index] with { AuthoringStatus = "authoring" };
            Execute(connection, transaction, "UPDATE mobile_wand_workshops SET steps_json=$steps,question_lease=$lease,question_lease_until=$until,updated_at=$now WHERE account_id=$a AND operation_id=$o AND status='authoring'",
                ("$steps", Serialize(steps)), ("$lease", lease), ("$until", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120),
                ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("$a", account), ("$o", operation));
            transaction.Commit();
            return new(account, operation, row.Tier,
                Deserialize<CharacterConversationSnapshot>(row.ProfileJson), steps[index], steps, lease,
                row.LaunchGroupId, row.LaunchAccessRevision);
        }
        transaction.Commit();
        return null;
    }

    internal void CompleteWorkshopQuestion(MobileWandQuestionJob job, string question,
        string[] basedOnFactIds, bool usedFallback)
    {
        if (!WandGenerationValidation.Text(question, 400) || question.Count(character => character == '?') != 1
            || question.Contains('\n') || basedOnFactIds.Length > 16)
            throw new WandGenerationException("wand_question_invalid", 502);
        using var connection = Open(); EnsureWorkshop(connection);
        using var transaction = connection.BeginTransaction(deferred: false);
        var row = ReadWorkshop(connection, transaction, job.AccountId, job.OperationId)
            ?? throw new WandGenerationException("wand_session_unavailable", 404);
        if (row.Status != "authoring" || row.QuestionLease != job.LeaseId)
            throw new WandGenerationException("wand_question_authoring_stale");
        var steps = Deserialize<MobileWandWorkshopStep[]>(row.StepsJson);
        var index = Array.FindIndex(steps, step => step.QuestionId == job.Step.QuestionId);
        if (index < 0 || steps[index].AuthoringStatus != "authoring")
            throw new WandGenerationException("wand_question_authoring_stale");
        steps[index] = steps[index] with { Question = question.Trim(), BasedOnFactIds = basedOnFactIds,
            AuthoringStatus = "ready", UsedFallback = usedFallback };
        Execute(connection, transaction, "UPDATE mobile_wand_workshops SET status='asking',steps_json=$steps,question_lease=NULL,question_lease_until=0,error_code=NULL,updated_at=$now WHERE account_id=$a AND operation_id=$o AND status='authoring' AND question_lease=$lease",
            ("$steps", Serialize(steps)), ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            ("$a", job.AccountId), ("$o", job.OperationId), ("$lease", job.LeaseId));
        transaction.Commit();
    }

    internal void FailWorkshopQuestion(MobileWandQuestionJob job, string errorCode)
    {
        using var connection = Open(); EnsureWorkshop(connection);
        Execute(connection, null, "UPDATE mobile_wand_workshops SET status='failed',error_code=$e,question_lease=NULL,question_lease_until=0,updated_at=$now WHERE account_id=$a AND operation_id=$o AND status='authoring' AND question_lease=$lease",
            ("$e", errorCode), ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            ("$a", job.AccountId), ("$o", job.OperationId), ("$lease", job.LeaseId));
    }

    private bool CanRecoverWorkshopQuestion(string accountId, string operationId)
    {
        using var connection = Open(); EnsureWorkshop(connection);
        var row = ReadWorkshop(connection, null, accountId, operationId);
        if (row is null || row.Status != "failed" || !CanRecoverQuestionError(row.ErrorCode)) return false;
        return Deserialize<MobileWandWorkshopStep[]>(row.StepsJson)
            .Any(step => step.AuthoringStatus == "authoring" && string.IsNullOrEmpty(step.Question));
    }

    internal bool RecoverWorkshopQuestionFallback(string accountId, string operationId)
    {
        using var connection = Open(); EnsureWorkshop(connection);
        using var transaction = connection.BeginTransaction(deferred: false);
        var row = ReadWorkshop(connection, transaction, accountId, operationId);
        if (row is null || row.Status != "failed" || !CanRecoverQuestionError(row.ErrorCode))
        { transaction.Commit(); return false; }
        var steps = Deserialize<MobileWandWorkshopStep[]>(row.StepsJson);
        var index = Array.FindIndex(steps, step => step.AuthoringStatus == "authoring" && string.IsNullOrEmpty(step.Question));
        if (index < 0) { transaction.Commit(); return false; }
        var step = steps[index];
        var job = new MobileWandQuestionJob(accountId, operationId, row.Tier,
            Deserialize<CharacterConversationSnapshot>(row.ProfileJson), step, steps, "recovery",
            row.LaunchGroupId, row.LaunchAccessRevision);
        steps[index] = step with { Question = OpenAiMobileWandQuestionProvider.FallbackQuestion(job),
            BasedOnFactIds = [], AuthoringStatus = "ready", UsedFallback = true };
        Execute(connection, transaction, "UPDATE mobile_wand_workshops SET status='asking',steps_json=$steps,error_code=NULL,question_lease=NULL,question_lease_until=0,updated_at=$now WHERE account_id=$a AND operation_id=$o AND status='failed'",
            ("$steps", Serialize(steps)), ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            ("$a", accountId), ("$o", operationId));
        transaction.Commit();
        return true;
    }

    private static bool CanRecoverQuestionError(string? code) => code is
        "question_authoring_uncertain" or "question_provider_uncertain" or
        "question_response_incomplete" or "question_response_invalid" or "question_context_too_large";

    internal void ReleaseWorkshopQuestion(MobileWandQuestionJob job)
    {
        using var connection = Open(); EnsureWorkshop(connection);
        using var transaction = connection.BeginTransaction(deferred: false);
        var row = ReadWorkshop(connection, transaction, job.AccountId, job.OperationId);
        if (row is null || row.Status != "authoring" || row.QuestionLease != job.LeaseId)
        { transaction.Commit(); return; }
        var steps = Deserialize<MobileWandWorkshopStep[]>(row.StepsJson);
        var index = Array.FindIndex(steps, step => step.QuestionId == job.Step.QuestionId);
        if (index >= 0 && steps[index].AuthoringStatus == "authoring")
        {
            steps[index] = steps[index] with { AuthoringStatus = "pending" };
            Execute(connection, transaction, "UPDATE mobile_wand_workshops SET steps_json=$steps,question_lease=NULL,question_lease_until=0,updated_at=$now WHERE account_id=$a AND operation_id=$o AND question_lease=$lease",
                ("$steps", Serialize(steps)), ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                ("$a", job.AccountId), ("$o", job.OperationId), ("$lease", job.LeaseId));
        }
        transaction.Commit();
    }

    internal MobileWandWorkshopStep[] CloseWorkshopQuestion(string accountId, string operationId,
        string questionId, int questionRevision, string? answer, string? sourceMode)
    {
        using var connection = Open(); EnsureWorkshop(connection);
        using var transaction = connection.BeginTransaction(deferred: false);
        var row = ReadWorkshop(connection, transaction, accountId, operationId)
            ?? throw new WandGenerationException("wand_session_unavailable", 404);
        var steps = Deserialize<MobileWandWorkshopStep[]>(row.StepsJson);
        var index = Array.FindIndex(steps, step => step.QuestionId == questionId);
        if (index < 0 || steps[index].QuestionRevision != questionRevision)
            throw new WandGenerationException("wand_question_revision_conflict");
        if (steps[index].Answer is not null || steps[index].Skipped)
        {
            transaction.Commit();
            return steps;
        }
        if (row.Status != "asking" || steps[index].AuthoringStatus != "ready"
            || steps.Take(index).Any(step => step.Answer is null && !step.Skipped)
            || steps[index].Answer is not null)
            throw new WandGenerationException("wand_question_not_active");

        if (answer is not null)
        {
            if (!WorkshopAnswerValidation.IsValid(answer)) throw new WandGenerationException("invalid_wand_answer", 400);
            if (sourceMode is not ("text" or "voice")) throw new WandGenerationException("invalid_wand_answer", 400);
            steps[index] = steps[index] with { AnswerId = Guid.NewGuid().ToString("N"), AnswerRevision = 1,
                Answer = answer.Trim(), SourceMode = sourceMode };
        }
        else
        {
            steps[index] = steps[index] with { Skipped = true };
        }

        var finished = steps.All(step => step.Answer is not null || step.Skipped);
        if (index < 2 && !finished)
        {
            steps[index + 1] = steps[index + 1] with { AuthoringStatus = "pending" };
        }
        var nextStatus = finished ? "generating" : "authoring";
        Execute(connection, transaction, """
            UPDATE mobile_wand_workshops SET status=$status,steps_json=$steps,updated_at=$now
            WHERE account_id=$a AND operation_id=$o AND status='asking'
            """, ("$status", nextStatus), ("$steps", Serialize(steps)),
            ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("$a", accountId), ("$o", operationId));
        transaction.Commit();
        return steps;
    }

    internal void CorrectWorkshopAnswer(string accountId, string operationId, string answerId,
        int expectedRevision, string answer, string sourceMode)
    {
        if (!WorkshopAnswerValidation.IsValid(answer) || sourceMode is not ("text" or "voice"))
            throw new WandGenerationException("invalid_wand_answer", 400);
        using var connection = Open(); EnsureWorkshop(connection);
        using var transaction = connection.BeginTransaction(deferred: false);
        var row = ReadWorkshop(connection, transaction, accountId, operationId)
            ?? throw new WandGenerationException("wand_session_unavailable", 404);
        if (row.Status != "asking") throw new WandGenerationException("wand_answer_locked");
        var steps = Deserialize<MobileWandWorkshopStep[]>(row.StepsJson);
        var index = Array.FindIndex(steps, step => step.AnswerId == answerId);
        if (index < 0 || steps[index].AnswerRevision != expectedRevision || steps[index].Answer is null)
            throw new WandGenerationException("wand_answer_revision_conflict");
        steps[index] = steps[index] with { Answer = answer.Trim(), AnswerRevision = expectedRevision + 1, SourceMode = sourceMode };
        Execute(connection, transaction, "UPDATE mobile_wand_workshops SET steps_json=$steps,updated_at=$now WHERE account_id=$a AND operation_id=$o",
            ("$steps", Serialize(steps)), ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("$a", accountId), ("$o", operationId));
        transaction.Commit();
    }

    internal void CompleteWorkshop(string accountId, string operationId, string keepsakeId)
    {
        using var connection = Open(); EnsureWorkshop(connection);
        Execute(connection, null, """
            UPDATE mobile_wand_workshops SET status='complete',keepsake_id=$k,error_code=NULL,artwork_error=NULL,
                updated_at=$now WHERE account_id=$a AND operation_id=$o AND status='generating'
            """, ("$k", keepsakeId), ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            ("$a", accountId), ("$o", operationId));
    }

    internal void CompleteWorkshopWithoutArtwork(string accountId, string operationId, string errorCode)
    {
        using var connection = Open(); EnsureWorkshop(connection);
        Execute(connection, null, """
            UPDATE mobile_wand_workshops SET status='complete',keepsake_id=NULL,error_code=NULL,
                artwork_error=$e,updated_at=$now WHERE account_id=$a AND operation_id=$o AND status='generating'
            """, ("$e", errorCode), ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            ("$a", accountId), ("$o", operationId));
    }

    internal void RecordWorkshopArtworkUnavailable(string accountId, string operationId, string errorCode)
    {
        using var connection = Open(); EnsureWorkshop(connection);
        Execute(connection, null, "UPDATE mobile_wand_workshops SET artwork_error=$e,updated_at=$now WHERE account_id=$a AND operation_id=$o AND status='complete' AND keepsake_id IS NULL",
            ("$e", errorCode), ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            ("$a", accountId), ("$o", operationId));
    }

    internal void AttachWorkshopKeepsake(string accountId, string operationId, string keepsakeId)
    {
        using var connection = Open(); EnsureWorkshop(connection);
        Execute(connection, null, """
            UPDATE mobile_wand_workshops SET keepsake_id=$k,artwork_error=NULL,updated_at=$now
            WHERE account_id=$a AND operation_id=$o AND status IN ('generating','complete')
            """, ("$k", keepsakeId), ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            ("$a", accountId), ("$o", operationId));
    }

    internal void FailWorkshop(string accountId, string operationId, string errorCode)
    {
        using var connection = Open(); EnsureWorkshop(connection);
        Execute(connection, null, "UPDATE mobile_wand_workshops SET status='failed',error_code=$e,retry_requested=0,updated_at=$now WHERE account_id=$a AND operation_id=$o AND status='generating'",
            ("$e", errorCode), ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("$a", accountId), ("$o", operationId));
    }

    internal void ResumeWorkshop(string accountId, string operationId)
    {
        using var connection = Open(); EnsureWorkshop(connection);
        Execute(connection, null, "UPDATE mobile_wand_workshops SET status='generating',error_code=NULL,retry_requested=1,updated_at=$now WHERE account_id=$a AND operation_id=$o AND status='failed'",
            ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("$a", accountId), ("$o", operationId));
    }

    private WorkshopRow? ReadWorkshop(SqliteConnection connection, SqliteTransaction? transaction,
        string accountId, string operationId)
    {
        using var command = Command(connection, transaction, """
            SELECT account_id,operation_id,profile_id,profile_revision,tier,status,profile_json,
                steps_json,keepsake_id,error_code,artwork_error,retry_requested,question_lease,question_lease_until,
                launch_group_id,launch_access_revision FROM mobile_wand_workshops
            WHERE account_id=$a AND operation_id=$o
            """, ("$a", accountId), ("$o", operationId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetInt64(3), reader.GetInt32(4), reader.GetString(5), reader.GetString(6),
            reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetInt32(11) != 0, reader.IsDBNull(12) ? null : reader.GetString(12), reader.GetInt64(13),
            reader.GetString(14), reader.GetInt64(15)) : null;
    }

    private static MobileWandWorkshopStep Question(CharacterConversationSnapshot profile, int number,
        string[] priorAnswers)
    {
        var facts = profile.Enrichment?.Facts ?? [];
        string question;
        string[] basedOn;
        if (number == 1)
        {
            var fact = facts.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Text));
            basedOn = fact is null ? [] : [StableFactId(profile, fact)];
            question = fact is null
                ? "Když se tvůj plán nečekaně zadrhne, co uděláš jako první?"
                : $"Poznamenal jsem si: {Clip(fact.Text, 150)}. Když se objeví nová překážka, co uděláš jako první?";
        }
        else if (number == 2)
        {
            basedOn = [];
            var approach = priorAnswers.LastOrDefault() ?? "";
            question = approach.Contains("pomoc", StringComparison.OrdinalIgnoreCase)
                ? "Když pomoc, kterou sis zvolil, selže, s čím má hůlka pomoci jako první?"
                : approach.Contains("ček", StringComparison.OrdinalIgnoreCase)
                    ? "Když čekání přestane být rozumné, s čím má hůlka pomoci jako první?"
                    : "Když tvůj první pokus selže, s čím má hůlka pomoci jako první?";
        }
        else
        {
            basedOn = [];
            var answer = priorAnswers.LastOrDefault() ?? "";
            var basis = answer.Contains("pomoc", StringComparison.OrdinalIgnoreCase) ? "pomocnou hůlku"
                : answer.Contains("ticho", StringComparison.OrdinalIgnoreCase) ? "tichou hůlku" : "hůlku";
            question = $"Jaký drobný rozmar má mít {basis}, i když ti bude zrovna překážet?";
        }
        question = question.Replace("?", "", StringComparison.Ordinal).TrimEnd() + "?";
        return new(Guid.NewGuid().ToString("N"), 1, number, question, basedOn);
    }

    private static string StableFactId(CharacterConversationSnapshot profile, CharacterFact fact)
    {
        var content = profile.ProfileId + "\n" + profile.Revision + "\n" + fact.Text
            + "\n" + string.Join("\n", fact.SourceUrls.Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    private static string Clip(string value, int maximum)
    {
        var plain = new string(value.Where(character => !char.IsControl(character) && character != '?').ToArray());
        var sanitized = string.Join(' ', plain.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return sanitized.Length <= maximum ? sanitized : sanitized[..maximum].TrimEnd() + "…";
    }

}

internal static class WorkshopAnswerValidation
{
    internal static bool IsValid(string? answer) => !string.IsNullOrWhiteSpace(answer)
        && answer.Length <= 2000
        && !answer.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t'));
}
