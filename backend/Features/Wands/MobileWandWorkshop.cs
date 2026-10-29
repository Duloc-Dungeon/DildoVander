using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Customers;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Wands;

internal sealed record MobileWandWorkshopMessage(string MessageId, int Revision, string Role,
    string Text, string SourceMode, long CreatedAt);
internal sealed record MobileWandWorkshopStep(string QuestionId, int QuestionRevision, int Number,
    string Question, string[] BasedOnFactIds, string? AnswerId = null, int AnswerRevision = 0,
    string? Answer = null, bool Skipped = false, string? SourceMode = null, int? Total = null,
    string AuthoringStatus = "ready", bool UsedFallback = false);
internal sealed record MobileWandPublicResult(string WandId, string Name, string Material, string Core,
    string Temperament, string Lore, string DesignDescription, string Affinity, string Weakness,
    WandSpell[] Spells, string CombatArchetype, string CombatRulesVersion);
internal sealed record MobileWandWorkshopState(string Id, string ProfileId, long ProfileRevision, int Tier,
    string Status, MobileWandWorkshopStep? Question, MobileWandWorkshopStep[] Steps,
    MobileWandPublicResult? Wand, string? KeepsakeId = null,
    Kufr.Backend.Features.Keepsakes.OwnedWandArtworkReference? Artwork = null,
    string? ErrorCode = null, string? Verdict = null, bool CanRetry = false,
    string? RecoveryKind = null, MobileWandWorkshopMessage[]? Messages = null);
internal sealed record MobileWandWorkshopJob(string AccountId, string OperationId, int Tier,
    CharacterConversationSnapshot Profile, MobileWandWorkshopStep[] Steps, bool RetryRequested,
    string LaunchGroupId, long LaunchAccessRevision)
{
    internal MobileWandWorkshopMessage[] Messages { get; init; } = [];
}
internal sealed record MobileWandWorkshopStart(string OperationId, bool Upgrade = false);

internal sealed partial class WandGenerationStore
{
    private sealed record WorkshopRow(string AccountId, string OperationId, string ProfileId,
        long ProfileRevision, int Tier, string Status, string? PausedFromStatus, bool FinishRequested,
        string ProfileJson, string StepsJson, string MessagesJson,
        string? KeepsakeId, string? ErrorCode, string? ArtworkError, bool RetryRequested,
        string? QuestionLease, long QuestionLeaseUntil, string LaunchGroupId, long LaunchAccessRevision);

    private void EnsureWorkshop(SqliteConnection connection)
    {
        Execute(connection, null, """
            CREATE TABLE IF NOT EXISTS mobile_wand_workshops(
                account_id TEXT NOT NULL, operation_id TEXT NOT NULL, profile_id TEXT NOT NULL,
                profile_revision INTEGER NOT NULL, tier INTEGER NOT NULL, status TEXT NOT NULL, paused_from_status TEXT NULL,
                finish_requested INTEGER NOT NULL DEFAULT 0,
                launch_group_id TEXT NOT NULL DEFAULT '', launch_access_revision INTEGER NOT NULL DEFAULT 0,
                profile_json TEXT NOT NULL, steps_json TEXT NOT NULL, messages_json TEXT NOT NULL DEFAULT '[]', keepsake_id TEXT NULL,
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
        using var messages = Command(connection, null,
            "SELECT COUNT(*) FROM pragma_table_info('mobile_wand_workshops') WHERE name='messages_json'");
        if (Convert.ToInt32(messages.ExecuteScalar()) == 0)
            Execute(connection, null, "ALTER TABLE mobile_wand_workshops ADD COLUMN messages_json TEXT NOT NULL DEFAULT '[]'");
        using var paused = Command(connection, null,
            "SELECT COUNT(*) FROM pragma_table_info('mobile_wand_workshops') WHERE name='paused_from_status'");
        if (Convert.ToInt32(paused.ExecuteScalar()) == 0)
            Execute(connection, null, "ALTER TABLE mobile_wand_workshops ADD COLUMN paused_from_status TEXT NULL");
        using var finishRequested = Command(connection, null,
            "SELECT COUNT(*) FROM pragma_table_info('mobile_wand_workshops') WHERE name='finish_requested'");
        if (Convert.ToInt32(finishRequested.ExecuteScalar()) == 0)
            Execute(connection, null, "ALTER TABLE mobile_wand_workshops ADD COLUMN finish_requested INTEGER NOT NULL DEFAULT 0");
        MigrateLegacyWorkshopSteps(connection);
    }

    private void MigrateLegacyWorkshopSteps(SqliteConnection connection)
    {
        const string candidateRows = "status IN ('authoring','asking') AND steps_json <> '[]'"
            + " OR status='failed' AND error_code IN ('question_authoring_authority_unavailable','wand_workshop_boss_captured')";
        using (var pending = Command(connection, null,
            $"SELECT EXISTS(SELECT 1 FROM mobile_wand_workshops WHERE {candidateRows})"))
        {
            if (Convert.ToInt32(pending.ExecuteScalar()) == 0) return;
        }
        using var transaction = connection.BeginTransaction(deferred: false);
        using var query = Command(connection, transaction, """
            SELECT account_id,operation_id,status,error_code,paused_from_status,steps_json,messages_json
            FROM mobile_wand_workshops WHERE status IN ('authoring','asking') AND steps_json <> '[]'
                OR status='failed' AND error_code IN ('question_authoring_authority_unavailable','wand_workshop_boss_captured')
            """);
        var rows = new List<(string Account, string Operation, string Status, string? Error,
            string? PausedFrom, string Steps, string Messages)>();
        using (var reader = query.ExecuteReader())
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5), reader.GetString(6)));
        foreach (var row in rows)
        {
            var messages = Deserialize<MobileWandWorkshopMessage[]>(row.Messages).ToList();
            var steps = Deserialize<MobileWandWorkshopStep[]>(row.Steps);
            long time = messages.Count == 0 ? 1 : messages.Max(message => message.CreatedAt) + 1;
            foreach (var step in steps)
            {
                if (!string.IsNullOrWhiteSpace(step.Question))
                {
                    var id = LegacyMessageId(row.Operation, step.QuestionId, "assistant", step.Question);
                    if (messages.All(message => message.MessageId != id))
                        messages.Add(new(LegacyMessageId(row.Operation, step.QuestionId, "assistant", step.Question), 1,
                            "assistant", step.Question, "legacy", time++));
                }
                if (step.Answer is { Length: > 0 } answer)
                {
                    var id = LegacyMessageId(row.Operation, step.QuestionId, "user", answer);
                    if (messages.All(message => message.MessageId != id))
                        messages.Add(new(LegacyMessageId(row.Operation, step.QuestionId, "user", answer),
                            Math.Max(1, step.AnswerRevision), "user", answer, "legacy", time++));
                }
                else if (step.Skipped)
                {
                    const string skippedAnswer = "Tohle přeskočím.";
                    var id = LegacyMessageId(row.Operation, step.QuestionId, "user", skippedAnswer);
                    if (messages.All(message => message.MessageId != id))
                        messages.Add(new(LegacyMessageId(row.Operation, step.QuestionId, "user", "Tohle přeskočím."),
                            1, "user", "Tohle přeskočím.", "legacy", time++));
                }
            }
            var recoverableFailure = row.Status == "failed";
            var pausedFrom = recoverableFailure
                ? row.Error == "question_authoring_authority_unavailable" ? "asking" : "generating"
                : row.PausedFrom;
            var status = recoverableFailure ? "paused" : "asking";
            var error = recoverableFailure ? "wand_workshop_boss_captured" : null;
            Execute(connection, transaction, """
                UPDATE mobile_wand_workshops SET status=$status,paused_from_status=$paused,
                    steps_json='[]',messages_json=$messages,question_lease=NULL,question_lease_until=0,error_code=$error
                WHERE account_id=$a AND operation_id=$o
                """, ("$status", status), ("$paused", pausedFrom), ("$messages", Serialize(messages.ToArray())),
                ("$error", error), ("$a", row.Account), ("$o", row.Operation));
        }
        transaction.Commit();
    }

    private static string LegacyMessageId(string operationId, string questionId, string role, string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operationId + "\n" + questionId + "\n" + role + "\n" + text)))
            .ToLowerInvariant()[..32];

    internal string? FindWorkshop(string accountId, string profileId, long revision, int tier)
    {
        using var connection = Open(); EnsureWorkshop(connection);
        using var command = Command(connection, null, """
            SELECT operation_id FROM mobile_wand_workshops
            WHERE account_id=$a AND profile_id=$p AND profile_revision=$r AND tier=$t
            """, ("$a", accountId), ("$p", profileId), ("$r", revision), ("$t", tier));
        return command.ExecuteScalar() as string;
    }

    internal (string GroupId, long AccessRevision)? ReadWorkshopLaunchSnapshot(string accountId, string operationId)
    {
        using var connection = Open();
        EnsureWorkshop(connection);
        var row = ReadWorkshop(connection, null, accountId, operationId);
        return row is null ? null : (row.LaunchGroupId, row.LaunchAccessRevision);
    }

    internal string? FindCompletedWorkshop(string accountId, CharacterProfile profile, int tier)
    {
        using var connection = Open(); EnsureWorkshop(connection);
        using var command = Command(connection, null, """
            SELECT w.operation_id,w.profile_json FROM mobile_wand_workshops w
            JOIN wand_generation_sessions s ON s.account_id=w.account_id AND s.operation_id=w.operation_id
            WHERE w.account_id=$a AND w.profile_id=$p AND w.profile_revision<$r AND w.tier=$t
                AND w.status='complete' AND s.state='selected' AND s.selected_json IS NOT NULL
            ORDER BY w.profile_revision DESC
            """, ("$a", accountId), ("$p", profile.ProfileId), ("$r", profile.Revision), ("$t", tier));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            // Portrait replacement advances the revision without changing the character.
            if (Deserialize<CharacterConversationSnapshot>(reader.GetString(1)).UserInput == profile.UserInput)
            {
                return reader.GetString(0);
            }
        }
        return null;
    }

    // The caller must authorize the current membership. Recovery never grants authority from saved history.
    internal void RecoverWorkshopMembership(string accountId, string operationId,
        (string GroupId, long AccessRevision) expected, string groupId, long accessRevision)
    {
        using var connection = Open(); EnsureWorkshop(connection);
        using var transaction = connection.BeginTransaction(deferred: false);
        RequireWorkshopSnapshot(connection, transaction, accountId, operationId, expected);
        var row = ReadWorkshop(connection, transaction, accountId, operationId)!;
        var generation = Read(connection, transaction, accountId, operationId);
        // A dispatched provider request may already be billed. Fence its result, but require explicit retry.
        Execute(connection, transaction, """
            UPDATE wand_generation_sessions SET state='unavailable',error_code='generation_interrupted',
                lease_token=NULL,lease_until=0
            WHERE account_id=$a AND operation_id=$o AND state='running'
            """, ("$a", accountId), ("$o", operationId));
        var status = row.Status;
        if (generation?.SelectedWand is not null)
        {
            status = row.KeepsakeId is not null || row.Status == "complete" ? "complete" : "generating";
        }
        else if (generation?.Status is "running" or "unavailable")
        {
            status = "failed";
        }
        else if (generation?.Status is "queued" or "ready")
        {
            // Manufacture has frozen its evidence; recovery must not reopen the conversation.
            status = "generating";
        }
        else if (row.Status is "failed" or "paused")
        {
            // FailWorkshop only transitions generating rows. Legacy authoring failures
            // are migrated by EnsureWorkshop before this row is read.
            status = row.FinishRequested || row.Status == "failed" || row.PausedFromStatus == "generating"
                ? "generating" : "asking";
        }
        Execute(connection, transaction, """
            UPDATE mobile_wand_workshops SET launch_group_id=$g,launch_access_revision=$r,status=$status,
                paused_from_status=NULL,error_code=$error,retry_requested=0,
                question_lease=NULL,question_lease_until=0,updated_at=$now
            WHERE account_id=$a AND operation_id=$o
            """, ("$g", groupId), ("$r", accessRevision), ("$status", status),
            ("$error", status == "failed" ? generation?.ErrorCode ?? "generation_interrupted" : null),
            ("$now", Now), ("$a", accountId), ("$o", operationId));
        transaction.Commit();
    }

    private void RequireWorkshopSnapshot(SqliteConnection connection, SqliteTransaction transaction,
        string accountId, string operationId, (string GroupId, long AccessRevision)? expected)
    {
        if (expected is null) { return; }
        var row = ReadWorkshop(connection, transaction, accountId, operationId);
        if (row is null || row.LaunchGroupId != expected.Value.GroupId
            || row.LaunchAccessRevision != expected.Value.AccessRevision)
        {
            throw new WandGenerationException("wand_session_unavailable", 404);
        }
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
        if (!IsPreparedSnapshot(profile)) throw new WandGenerationException("character_not_ready", 409);
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

        var steps = Array.Empty<MobileWandWorkshopStep>();
        try
        {
            Execute(connection, transaction, """
                INSERT INTO mobile_wand_workshops(account_id,operation_id,profile_id,profile_revision,tier,
                    status,launch_group_id,launch_access_revision,profile_json,steps_json,messages_json,updated_at)
                VALUES($a,$o,$p,$r,$t,'asking',$group,$accessRevision,$profile,$steps,'[]',$now)
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

    internal MobileWandWorkshopMessage[] ReadWorkshopMessages(string accountId, string operationId)
    {
        using var connection = Open(); EnsureWorkshop(connection);
        var row = ReadWorkshop(connection, null, accountId, operationId)
            ?? throw new WandGenerationException("wand_session_unavailable", 404);
        return Deserialize<MobileWandWorkshopMessage[]>(row.MessagesJson);
    }

    internal CharacterConversationSnapshot ReadWorkshopProfile(string accountId, string operationId)
    {
        using var connection = Open(); EnsureWorkshop(connection);
        var row = ReadWorkshop(connection, null, accountId, operationId)
            ?? throw new WandGenerationException("wand_session_unavailable", 404);
        var profile = Deserialize<CharacterConversationSnapshot>(row.ProfileJson);
        if (!IsPreparedSnapshot(profile)) throw new WandGenerationException("character_not_ready", 409);
        return profile;
    }

    internal MobileWandWorkshopMessage[] AppendWorkshopMessage(string accountId, string operationId,
        string messageId, string role, string text, string sourceMode)
    {
        if (!Guid.TryParseExact(messageId, "N", out var parsedId) || parsedId == Guid.Empty
            || role is not ("user" or "assistant") || sourceMode is not ("text" or "voice")
            || !WorkshopAnswerValidation.IsValid(text))
            throw new WandGenerationException("invalid_wand_message", 400);
        messageId = parsedId.ToString("N");

        using var connection = Open(); EnsureWorkshop(connection);
        using var transaction = connection.BeginTransaction(deferred: false);
        var row = ReadWorkshop(connection, transaction, accountId, operationId)
            ?? throw new WandGenerationException("wand_session_unavailable", 404);
        var messages = Deserialize<MobileWandWorkshopMessage[]>(row.MessagesJson);
        var existing = messages.FirstOrDefault(item => item.MessageId == messageId);
        if (existing is not null)
        {
            if (existing.Role != role || existing.Text != text.Trim() || existing.SourceMode != sourceMode)
                throw new WandGenerationException("wand_message_id_conflict");
            transaction.Commit();
            return messages;
        }
        var assistantRecovery = role == "assistant" && row.Status == "paused" && row.PausedFromStatus == "asking";
        if (row.Status != "asking" && !assistantRecovery) throw new WandGenerationException("wand_message_locked");
        if (messages.Length >= 32) throw new WandGenerationException("wand_conversation_limit", 413);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        messages = [.. messages, new MobileWandWorkshopMessage(messageId, 1, role, text.Trim(), sourceMode, now)];
        Execute(connection, transaction, "UPDATE mobile_wand_workshops SET messages_json=$messages,updated_at=$now WHERE account_id=$a AND operation_id=$o AND (status='asking' OR (status='paused' AND paused_from_status='asking' AND $role='assistant'))",
            ("$messages", Serialize(messages)), ("$now", now / 1000), ("$a", accountId), ("$o", operationId), ("$role", role));
        transaction.Commit();
        return messages;
    }

    internal MobileWandWorkshopMessage[] UpdateWorkshopMessage(string accountId, string operationId,
        string messageId, int expectedRevision, string text, string sourceMode)
    {
        if (!WorkshopAnswerValidation.IsValid(text) || sourceMode is not ("text" or "voice"))
            throw new WandGenerationException("invalid_wand_message", 400);
        using var connection = Open(); EnsureWorkshop(connection);
        using var transaction = connection.BeginTransaction(deferred: false);
        var row = ReadWorkshop(connection, transaction, accountId, operationId)
            ?? throw new WandGenerationException("wand_session_unavailable", 404);
        if (row.Status != "asking") throw new WandGenerationException("wand_message_locked");
        var messages = Deserialize<MobileWandWorkshopMessage[]>(row.MessagesJson);
        var index = Array.FindIndex(messages, item => item.MessageId == messageId && item.Role == "user");
        if (index < 0 || messages[index].Revision != expectedRevision)
            throw new WandGenerationException("wand_message_revision_conflict");
        messages[index] = messages[index] with { Revision = expectedRevision + 1, Text = text.Trim(), SourceMode = sourceMode };
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Execute(connection, transaction, "UPDATE mobile_wand_workshops SET messages_json=$messages,updated_at=$now WHERE account_id=$a AND operation_id=$o AND status='asking'",
            ("$messages", Serialize(messages)), ("$now", now), ("$a", accountId), ("$o", operationId));
        transaction.Commit();
        return messages;
    }

    internal bool FinishWorkshopConversation(string accountId, string operationId,
        string expectedProfileId, long expectedProfileRevision, string? expectedUserEvidence = null)
    {
        using var connection = Open(); EnsureWorkshop(connection);
        using var transaction = connection.BeginTransaction(deferred: false);
        var row = ReadWorkshop(connection, transaction, accountId, operationId)
            ?? throw new WandGenerationException("wand_session_unavailable", 404);
        if (row.ProfileId != expectedProfileId || row.ProfileRevision != expectedProfileRevision)
            throw new WandGenerationException("wand_profile_revision_mismatch", 409);
        var profile = Deserialize<CharacterConversationSnapshot>(row.ProfileJson);
        if (!IsPreparedSnapshot(profile)) throw new WandGenerationException("character_not_ready", 409);
        if (row.Status is "generating" or "complete")
        {
            transaction.Commit();
            return false;
        }
        var messages = Deserialize<MobileWandWorkshopMessage[]>(row.MessagesJson);
        if (!messages.Any(message => message.Role == "user" && !string.IsNullOrWhiteSpace(message.Text)))
            throw new WandGenerationException("wand_conversation_evidence_required", 409);
        if (expectedUserEvidence is not null
            && !string.Equals(expectedUserEvidence, UserEvidenceDigest(messages), StringComparison.Ordinal))
            throw new WandGenerationException("wand_conversation_changed", 409);
        if (row.Status == "paused" && row.PausedFromStatus == "asking")
        {
            if (row.FinishRequested) { transaction.Commit(); return false; }
            Execute(connection, transaction, "UPDATE mobile_wand_workshops SET finish_requested=1,updated_at=$now WHERE account_id=$a AND operation_id=$o AND status='paused' AND paused_from_status='asking'",
                ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("$a", accountId), ("$o", operationId));
            transaction.Commit();
            return true;
        }
        if (row.Status != "asking") throw new WandGenerationException("wand_conversation_not_active", 409);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var changed = Execute(connection, transaction, "UPDATE mobile_wand_workshops SET status='generating',finish_requested=1,error_code=NULL,retry_requested=0,updated_at=$now WHERE account_id=$a AND operation_id=$o AND status='asking'",
            ("$now", now), ("$a", accountId), ("$o", operationId));
        transaction.Commit();
        return changed == 1;
    }

    internal static string UserEvidenceDigest(IEnumerable<MobileWandWorkshopMessage> messages)
    {
        var evidence = messages.Where(message => message.Role == "user")
            .Select(message => new { message.MessageId, message.Revision, message.Text });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(evidence))));
    }

    private static bool IsPreparedSnapshot(CharacterConversationSnapshot profile) =>
        !string.IsNullOrWhiteSpace(profile.ProfileId) && profile.Revision > 0
        && profile.Enrichment is { NeedsClarification: false } enrichment
        && enrichment.InputRevision == profile.Revision;

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
        var messages = Deserialize<MobileWandWorkshopMessage[]>(row.MessagesJson);
        var publicWand = wand is null ? null : new MobileWandPublicResult(wand.WandId, wand.Name,
            wand.Material, wand.Core, wand.Temperament, wand.Lore, wand.DesignDescription,
            wand.Affinity, wand.Weakness, wand.Spells, wand.CombatArchetype, wand.CombatRulesVersion);
        var state = new MobileWandWorkshopState(operationId, row.ProfileId, row.ProfileRevision, row.Tier,
            status, active, steps, publicWand, row.KeepsakeId,
            row.ArtworkError is null ? null : new Kufr.Backend.Features.Keepsakes.OwnedWandArtworkReference(
                "", null, "failed", true, row.ArtworkError), error, Messages: messages);
        return state with
        {
            Verdict = status == "complete" ? state.VerdictText() : null,
            CanRetry = status == "failed" && CanRetryGeneration(accountId, operationId),
            RecoveryKind = status == "failed" && CanRetryGeneration(accountId, operationId)
                ? "generation" : null
        };
    }

    internal MobileWandWorkshopJob[] PendingWorkshops()
    {
        using var connection = Open(); EnsureWorkshop(connection);
        using var command = Command(connection, null, """
            SELECT account_id,operation_id,tier,profile_json,steps_json,retry_requested,launch_group_id,launch_access_revision,messages_json
            FROM mobile_wand_workshops WHERE status='generating' ORDER BY updated_at LIMIT 8
            """);
        using var reader = command.ExecuteReader();
        var jobs = new List<MobileWandWorkshopJob>();
        while (reader.Read()) jobs.Add(new MobileWandWorkshopJob(reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
            Deserialize<CharacterConversationSnapshot>(reader.GetString(3)),
            Deserialize<MobileWandWorkshopStep[]>(reader.GetString(4)), reader.GetInt32(5) != 0,
            reader.GetString(6), reader.GetInt64(7)) { Messages = Deserialize<MobileWandWorkshopMessage[]>(reader.GetString(8)) });
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

    internal void CompleteWorkshop(string accountId, string operationId, string keepsakeId,
        (string GroupId, long AccessRevision)? expected = null)
    {
        using var connection = Open(); EnsureWorkshop(connection);
        Execute(connection, null, """
            UPDATE mobile_wand_workshops SET status='complete',keepsake_id=$k,error_code=NULL,artwork_error=NULL,
                updated_at=$now WHERE account_id=$a AND operation_id=$o AND status='generating'
                AND ($g IS NULL OR (launch_group_id=$g AND launch_access_revision=$r))
            """, ("$k", keepsakeId), ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            ("$a", accountId), ("$o", operationId), ("$g", expected?.GroupId), ("$r", expected?.AccessRevision));
    }

    internal void CompleteWorkshopWithoutArtwork(string accountId, string operationId, string errorCode,
        (string GroupId, long AccessRevision)? expected = null)
    {
        using var connection = Open(); EnsureWorkshop(connection);
        Execute(connection, null, """
            UPDATE mobile_wand_workshops SET status='complete',keepsake_id=NULL,error_code=NULL,
                artwork_error=$e,updated_at=$now WHERE account_id=$a AND operation_id=$o AND status='generating'
                AND ($g IS NULL OR (launch_group_id=$g AND launch_access_revision=$r))
            """, ("$e", errorCode), ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            ("$a", accountId), ("$o", operationId), ("$g", expected?.GroupId), ("$r", expected?.AccessRevision));
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

    internal void FailWorkshop(string accountId, string operationId, string errorCode,
        (string GroupId, long AccessRevision)? expected = null)
    {
        using var connection = Open(); EnsureWorkshop(connection);
        Execute(connection, null, """
            UPDATE mobile_wand_workshops SET status='failed',error_code=$e,retry_requested=0,updated_at=$now
            WHERE account_id=$a AND operation_id=$o AND status='generating'
                AND ($g IS NULL OR (launch_group_id=$g AND launch_access_revision=$r))
            """, ("$e", errorCode), ("$now", Now), ("$a", accountId), ("$o", operationId),
            ("$g", expected?.GroupId), ("$r", expected?.AccessRevision));
    }

    internal void ResumeWorkshop(string accountId, string operationId)
    {
        using var connection = Open(); EnsureWorkshop(connection);
        Execute(connection, null, "UPDATE mobile_wand_workshops SET status='generating',error_code=NULL,retry_requested=1,updated_at=$now WHERE account_id=$a AND operation_id=$o AND status='failed'",
            ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("$a", accountId), ("$o", operationId));
    }

    internal bool PauseWorkshopForBoss(string accountId, string operationId,
        (string GroupId, long AccessRevision)? expected = null)
    {
        using var connection = Open(); EnsureWorkshop(connection);
        using var transaction = connection.BeginTransaction(deferred: false);
        RequireWorkshopSnapshot(connection, transaction, accountId, operationId, expected);
        var row = ReadWorkshop(connection, transaction, accountId, operationId)
            ?? throw new WandGenerationException("wand_session_unavailable", 404);
        if (row.Status == "paused") { transaction.Commit(); return false; }
        if (row.Status is not ("authoring" or "asking" or "generating"))
        { transaction.Commit(); return false; }
        var steps = Deserialize<MobileWandWorkshopStep[]>(row.StepsJson);
        if (row.Status == "authoring")
        {
            var index = Array.FindIndex(steps, step => step.AuthoringStatus == "authoring");
            if (index >= 0) steps[index] = steps[index] with { AuthoringStatus = "pending" };
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Execute(connection, transaction, """
            UPDATE mobile_wand_workshops SET status='paused',paused_from_status=$from,
                error_code='wand_workshop_boss_captured',steps_json=$steps,question_lease=NULL,
                question_lease_until=0,updated_at=$now WHERE account_id=$a AND operation_id=$o
            """, ("$from", row.Status), ("$steps", Serialize(steps)), ("$now", now),
            ("$a", accountId), ("$o", operationId));
        transaction.Commit();
        return true;
    }

    // The route must recheck membership, consent, current prepared profile and boss rescue first.
    internal bool ResumeWorkshopAfterBoss(string accountId, string operationId)
    {
        using var connection = Open(); EnsureWorkshop(connection);
        using var transaction = connection.BeginTransaction(deferred: false);
        var row = ReadWorkshop(connection, transaction, accountId, operationId)
            ?? throw new WandGenerationException("wand_session_unavailable", 404);
        if (row.Status != "paused" || row.PausedFromStatus is not ("authoring" or "asking" or "generating"))
        { transaction.Commit(); return false; }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Execute(connection, transaction, """
            UPDATE mobile_wand_workshops SET status=CASE WHEN finish_requested=1 THEN 'generating' ELSE paused_from_status END,
                paused_from_status=NULL,finish_requested=0,error_code=NULL,retry_requested=0,updated_at=$now
            WHERE account_id=$a AND operation_id=$o AND status='paused'
            """, ("$now", now), ("$a", accountId), ("$o", operationId));
        transaction.Commit();
        return true;
    }

    private WorkshopRow? ReadWorkshop(SqliteConnection connection, SqliteTransaction? transaction,
        string accountId, string operationId)
    {
        using var command = Command(connection, transaction, """
            SELECT account_id,operation_id,profile_id,profile_revision,tier,status,paused_from_status,finish_requested,profile_json,
                steps_json,messages_json,keepsake_id,error_code,artwork_error,retry_requested,question_lease,question_lease_until,
                launch_group_id,launch_access_revision FROM mobile_wand_workshops
            WHERE account_id=$a AND operation_id=$o
            """, ("$a", accountId), ("$o", operationId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetInt64(3), reader.GetInt32(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetInt32(7) != 0, reader.GetString(8), reader.GetString(9), reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13), reader.GetInt32(14) != 0,
            reader.IsDBNull(15) ? null : reader.GetString(15), reader.GetInt64(16), reader.GetString(17), reader.GetInt64(18)) : null;
    }


}

internal static class WorkshopAnswerValidation
{
    internal static bool IsValid(string? answer) => !string.IsNullOrWhiteSpace(answer)
        && answer.Length <= 2000
        && !answer.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t'));
}
