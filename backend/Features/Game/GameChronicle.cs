using System.Text.Json;
using Kufr.Backend.Features.Chronicle;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Game;

internal sealed partial class GameStore
{
    internal void EnsureChronicleOutbox(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS game_chronicle_outbox(
                event_id TEXT PRIMARY KEY,event_json TEXT NOT NULL,created_at INTEGER NOT NULL,
                delivered_at INTEGER);
            CREATE TABLE IF NOT EXISTS game_chronicle_backfill_skipped(
                source_kind TEXT NOT NULL,source_id TEXT NOT NULL,reason_code TEXT NOT NULL,marked_at INTEGER NOT NULL,
                PRIMARY KEY(source_kind,source_id));
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>Queues at most <paramref name="limit"/> previously completed game records per call.</summary>
    internal int BackfillChronicleOutbox(int limit = 100)
    {
        limit = Math.Clamp(limit, 1, 500);
        lock (_writeGate)
        {
            using var connection = Open();
            EnsureChronicleOutbox(connection);
            using var transaction = connection.BeginTransaction(deferred: false);
            var candidates = new List<(string Kind, string Id, string Body, long OccurredAt)>();
            ReadBackfillCandidates(connection, transaction, "game_matches", "match", limit, candidates);
            if (TableExists(connection, transaction, "game_team_matches"))
                ReadBackfillCandidates(connection, transaction, "game_team_matches", "team", limit, candidates);
            if (TableExists(connection, transaction, "game_boss_attempts"))
                ReadBackfillCandidates(connection, transaction, "game_boss_attempts", "boss", limit, candidates);

            var ordered = candidates.OrderBy(candidate => candidate.OccurredAt)
                .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Kind, StringComparer.Ordinal)
                .Take(limit).ToArray();
            var queued = 0;
            foreach (var candidate in ordered)
            {
                try
                {
                    ChronicleOutboxEnvelope? envelope = candidate.Kind switch
                    {
                        "match" => BuildMatchBackfill(connection, transaction, candidate.Id, candidate.Body),
                        "team" => BuildTeamBackfill(connection, transaction, candidate.Id, candidate.Body),
                        "boss" => BuildBossBackfill(connection, transaction, candidate.Body),
                        _ => null
                    };
                    if (envelope is not null)
                    {
                        QueueChronicleEnvelope(connection, transaction, envelope);
                        queued++;
                    }
                }
                catch (Exception exception) when (exception is JsonException or InvalidDataException)
                {
                    MarkBackfillSkipped(connection, transaction, candidate.Kind, candidate.Id, "invalid_state");
                }
            }

            var remaining = Math.Max(0, limit - ordered.Length);
            if (remaining > 0)
            {
                remaining = Math.Max(0, remaining - MarkBackfillRowsWithoutTime(connection, transaction, "game_matches", "match", remaining));
                if (remaining > 0 && TableExists(connection, transaction, "game_team_matches"))
                {
                    remaining = Math.Max(0, remaining - MarkBackfillRowsWithoutTime(connection, transaction, "game_team_matches", "team", remaining));
                }
                if (remaining > 0 && TableExists(connection, transaction, "game_boss_attempts"))
                    MarkBackfillRowsWithoutTime(connection, transaction, "game_boss_attempts", "boss", remaining);
            }
            transaction.Commit();
            return queued;
        }
    }

    internal int ReadChronicleBackfillSkippedCount()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM game_chronicle_backfill_skipped";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static bool TableExists(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$table";
        command.Parameters.AddWithValue("$table", table);
        return command.ExecuteScalar() is not null;
    }

    private static void ReadBackfillCandidates(SqliteConnection connection, SqliteTransaction transaction,
        string table, string kind, int limit, ICollection<(string Kind, string Id, string Body, long OccurredAt)> candidates)
    {
        var modeExpression = kind == "match" ? "CASE WHEN json_extract(state_json,'$.mode')='yolo' THEN 'yolo' ELSE 'duel' END" : "'" + kind + "'";
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT id,state_json,json_extract(state_json,'$.completedAt')
            FROM {table} source
            WHERE json_valid(state_json)=1
                AND json_extract(state_json,'$.status')='complete'
                AND json_type(state_json,'$.completedAt')='integer'
                AND NOT EXISTS(SELECT 1 FROM game_chronicle_outbox o
                    WHERE o.event_id=({modeExpression} || ':' || source.id))
                AND NOT EXISTS(SELECT 1 FROM game_chronicle_backfill_skipped s
                    WHERE s.source_kind IN ({modeExpression},$journalKind) AND s.source_id=source.id)
            ORDER BY json_extract(state_json,'$.completedAt'),id LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$journalKind", kind);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            candidates.Add((kind, reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        }
    }

    private static ChronicleOutboxEnvelope BuildMatchBackfill(SqliteConnection connection,
        SqliteTransaction transaction, string matchId, string body)
    {
        var state = JsonSerializer.Deserialize<State>(body, Json)
            ?? throw new InvalidDataException("Completed game match cannot be read for Chronicle backfill.");
        return BuildChronicleEnvelope(connection, transaction, matchId, state);
    }

    private static ChronicleOutboxEnvelope BuildTeamBackfill(SqliteConnection connection,
        SqliteTransaction transaction, string matchId, string body)
    {
        var state = JsonSerializer.Deserialize<TeamState>(body, Json)
            ?? throw new InvalidDataException("Completed team match cannot be read for Chronicle backfill.");
        return BuildTeamChronicleEnvelope(connection, transaction, matchId, state);
    }

    private static ChronicleOutboxEnvelope BuildBossBackfill(SqliteConnection connection,
        SqliteTransaction transaction, string body)
    {
        var attempt = JsonSerializer.Deserialize<BossAttempt>(body, Json)
            ?? throw new InvalidDataException("Completed boss attempt cannot be read for Chronicle backfill.");
        return BuildPersonalBossEnvelope(connection, transaction, attempt);
    }

    private static int MarkBackfillRowsWithoutTime(SqliteConnection connection, SqliteTransaction transaction,
        string table, string kind, int limit)
    {
        if (limit <= 0) { return 0; }
        var modeExpression = kind == "match" ? "CASE WHEN json_extract(state_json,'$.mode')='yolo' THEN 'yolo' ELSE 'duel' END" : "'" + kind + "'";
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO game_chronicle_backfill_skipped(source_kind,source_id,reason_code,marked_at)
            SELECT {modeExpression},source.id,
                CASE WHEN json_type(state_json,'$.completedAt') IS NULL OR json_type(state_json,'$.completedAt')='null'
                    THEN 'missing_completed_at' ELSE 'invalid_completed_at' END,$now
            FROM {table} source
            WHERE json_valid(state_json)=1 AND json_extract(state_json,'$.status')='complete'
                AND COALESCE(json_type(state_json,'$.completedAt'),'null')<>'integer'
                AND NOT EXISTS(SELECT 1 FROM game_chronicle_outbox o WHERE o.event_id=({modeExpression} || ':' || source.id))
                AND NOT EXISTS(SELECT 1 FROM game_chronicle_backfill_skipped s
                    WHERE s.source_kind={modeExpression} AND s.source_id=source.id)
            ORDER BY source.id LIMIT $limit
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$limit", limit);
        return command.ExecuteNonQuery();
    }

    private static void MarkBackfillSkipped(SqliteConnection connection, SqliteTransaction transaction,
        string kind, string id, string reason)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO game_chronicle_backfill_skipped(source_kind,source_id,reason_code,marked_at) VALUES($kind,$id,$reason,$now)";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }

    private void EnqueueChronicleOutbox(SqliteConnection connection, SqliteTransaction transaction, string matchId, State state)
    {
        var envelope = BuildChronicleEnvelope(connection, transaction, matchId, state);
        QueueChronicleEnvelope(connection, transaction, envelope);
    }

    private static void QueueChronicleEnvelope(SqliteConnection connection, SqliteTransaction transaction,
        ChronicleOutboxEnvelope envelope)
    {
        var body = JsonSerializer.Serialize(envelope, Json);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO game_chronicle_outbox(event_id,event_json,created_at) VALUES($id,$body,$created) ON CONFLICT(event_id) DO NOTHING";
        command.Parameters.AddWithValue("$id", $"{envelope.Event.SourceKind}:{envelope.Event.SourceId}");
        command.Parameters.AddWithValue("$body", body);
        command.Parameters.AddWithValue("$created", envelope.Event.OccurredAt);
        command.ExecuteNonQuery();
    }

    private static void EnqueueBossArrivalChronicle(SqliteConnection connection, SqliteTransaction transaction,
        string groupId, IReadOnlySet<string> eligible, int referenceXp)
    {
        var owners = eligible.Order(StringComparer.Ordinal).Take(32).ToArray();
        var participants = owners.Select(owner => new ChronicleParticipant(owner, "Člen skupiny", "group-member")).ToArray();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var input = new ChronicleEventInput("boss", "arrival-" + groupId, groupId, now,
            "Baron přebírá dílnu", "Baron převzal dílnu. Ollivander je v zajetí.",
            participants, Story: ["Dílnu převzal Baron. Ollivander čeká na osvobození."],
            ShareText: "Baron převzal dílnu. Ollivander je v zajetí.", IllustrationRequested: true);
        QueueChronicleEnvelope(connection, transaction, new(input, owners,
            owners.ToDictionary(owner => owner, _ => (IReadOnlyList<ChronicleAward>)[], StringComparer.Ordinal)));
    }

    private static void EnqueuePersonalBossChronicle(SqliteConnection connection, SqliteTransaction transaction,
        BossAttempt attempt)
        => QueueChronicleEnvelope(connection, transaction, BuildPersonalBossEnvelope(connection, transaction, attempt));

    private static ChronicleOutboxEnvelope BuildPersonalBossEnvelope(SqliteConnection connection,
        SqliteTransaction transaction, BossAttempt attempt)
    {
        var owner = Participant(attempt.UserId, "Kouzelník", "challenger", attempt.ChronicleProfile);
        var bossId = "baron-" + attempt.GroupId;
        var boss = new ChronicleParticipant(bossId, "Baron", "opponent", "Baron", null, null, "boss-v1");
        var winnerId = attempt.Result == "win" ? attempt.UserId : attempt.Result == "loss" ? bossId : null;
        var summary = attempt.Result == "win" ? "Baron byl poražen." : "Baron přemohl kouzelníka.";
        var rounds = attempt.Rounds.Select(round =>
            $"Výměna {round.Index}: kouzelník použil {ChronicleAction(round.PlayerChoice)}, Baron použil {ChronicleAction(round.OpponentChoice)}; "
                + (round.Winner == "player" ? "bod získal kouzelník." : round.Winner == "opponent" ? "bod získal Baron." : "výměna skončila remízou."))
            .ToArray();
        var decisions = attempt.Rounds.Select(round => new ChronicleDecision(attempt.UserId, ChronicleAction(round.PlayerChoice),
            round.Winner == "player" ? "V této výměně jsi získal bod." : round.Winner == "opponent"
                ? "V této výměně bod získal Baron." : "Výměna skončila remízou.", $"round:{round.Index}")).ToArray();
        IReadOnlyList<ChronicleAward> awards = attempt.Result == "win" && !HasEarlierBossWin(connection, transaction, attempt)
            ? [new("first-boss-victory", "Reklamace vyřízena osobně", "account", "První potvrzená osobní výhra nad Baronem.")]
            : [];
        var input = new ChronicleEventInput("boss", attempt.Id, attempt.GroupId,
            attempt.CompletedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "Osobní souboj s Baronem", summary,
            [owner, boss], attempt.Result, winnerId, rounds, decisions, summary,
            IllustrationRequested: awards.Count > 0, Visibility: "private");
        return new(input, [attempt.UserId],
            new Dictionary<string, IReadOnlyList<ChronicleAward>>(StringComparer.Ordinal) { [attempt.UserId] = awards });
    }

    private static bool HasEarlierBossWin(SqliteConnection connection, SqliteTransaction transaction, BossAttempt attempt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM game_boss_attempts WHERE id<>$id AND user_id=$owner
                AND json_extract(state_json,'$.status')='complete' AND json_extract(state_json,'$.result')='win'
                AND (json_extract(state_json,'$.completedAt')<$at
                    OR (json_extract(state_json,'$.completedAt')=$at AND id<$id)) LIMIT 1
            """;
        command.Parameters.AddWithValue("$id", attempt.Id);
        command.Parameters.AddWithValue("$owner", attempt.UserId);
        command.Parameters.AddWithValue("$at", attempt.CompletedAt ?? 0);
        return command.ExecuteScalar() is not null;
    }

    private static void EnqueueBossRescueChronicle(SqliteConnection connection, SqliteTransaction transaction,
        BossAttempt attempt, IReadOnlySet<string> eligible)
    {
        var owners = eligible.Where(owner => owner != attempt.UserId).Order(StringComparer.Ordinal).Take(31).Prepend(attempt.UserId).ToArray();
        var participants = owners.Select(owner => owner == attempt.UserId
            ? Participant(owner, "Kouzelník", "rescuer", attempt.ChronicleProfile)
            : new ChronicleParticipant(owner, "Člen skupiny", "group-member")).ToArray();
        var awards = owners.ToDictionary(owner => owner, _ => (IReadOnlyList<ChronicleAward>)[], StringComparer.Ordinal);
        var input = new ChronicleEventInput("boss", "rescue-" + attempt.GroupId, attempt.GroupId,
            attempt.CompletedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "Osvobození Ollivandera",
            "Ollivander je volný. Dílna se znovu otevírá skupině.", participants, "rescue", attempt.UserId,
            [$"Barona porazila postava {participants[0].DisplayName}. Ollivander je volný."] , ShareText: "Ollivander je volný. Dílna se znovu otevírá skupině.",
            IllustrationRequested: true);
        QueueChronicleEnvelope(connection, transaction, new(input, owners, awards));
    }

    private static void EnqueueTeamChronicleOutbox(SqliteConnection connection, SqliteTransaction transaction,
        string matchId, TeamState state)
        => QueueChronicleEnvelope(connection, transaction, BuildTeamChronicleEnvelope(connection, transaction, matchId, state));

    private static ChronicleOutboxEnvelope BuildTeamChronicleEnvelope(SqliteConnection connection,
        SqliteTransaction transaction, string matchId, TeamState state)
    {
        var owners = state.A.Concat(state.B).ToArray();
        var participants = owners.Select(accountId => Participant(accountId, "Kouzelník",
            state.A.Contains(accountId, StringComparer.Ordinal) ? "team-a" : "team-b",
            state.FrozenProfiles.GetValueOrDefault(accountId))).ToArray();
        var awards = new Dictionary<string, IReadOnlyList<ChronicleAward>>(StringComparer.Ordinal);
        foreach (var owner in owners)
        {
            IReadOnlyList<ChronicleAward> earned = HasPriorTeamMatch(connection, transaction, matchId, state, owner)
                ? [] : [new("first-team-match", "Společný podpis", "account", "První dokončený týmový zápas.")];
            awards[owner] = earned;
        }
        var story = state.Rounds.Select(round =>
            $"Výměna {round.Index}: tým A získal {round.TeamAPoints} bodů, tým B {round.TeamBPoints}.")
            .ToArray();
        var input = new ChronicleEventInput("team", matchId, state.GroupId,
            state.CompletedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "Týmový souboj",
            $"{state.Rounds.Sum(round => round.TeamAPoints)} : {state.Rounds.Sum(round => round.TeamBPoints)}",
            participants, state.Result, null, story,
            IllustrationRequested: awards.Values.Any(value => value.Count > 0));
        return new(input, owners, awards);
    }

    private static bool HasPriorTeamMatch(SqliteConnection connection, SqliteTransaction transaction,
        string matchId, TeamState state, string accountId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM game_team_matches WHERE id<>$id
                AND json_extract(state_json,'$.status')='complete'
                AND (json_extract(state_json,'$.completedAt')<$at OR (json_extract(state_json,'$.completedAt')=$at AND id<$id))
                AND (EXISTS(SELECT 1 FROM json_each(state_json,'$.a') WHERE value=$owner)
                    OR EXISTS(SELECT 1 FROM json_each(state_json,'$.b') WHERE value=$owner)) LIMIT 1
            """;
        command.Parameters.AddWithValue("$id", matchId);
        command.Parameters.AddWithValue("$owner", accountId);
        command.Parameters.AddWithValue("$at", state.CompletedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return command.ExecuteScalar() is not null;
    }

    internal void DrainChronicleOutbox(ChronicleStore chronicle)
    {
        var rows = new List<(string Id, string Body)>();
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            EnsureChronicleOutbox(connection);
            command.CommandText = "SELECT event_id,event_json FROM game_chronicle_outbox WHERE delivered_at IS NULL ORDER BY created_at LIMIT 20";
            using var reader = command.ExecuteReader();
            while (reader.Read()) { rows.Add((reader.GetString(0), reader.GetString(1))); }
        }
        foreach (var (id, body) in rows)
        {
            var envelope = JsonSerializer.Deserialize<ChronicleOutboxEnvelope>(body, Json)
                ?? throw new InvalidDataException("Game chronicle outbox record is empty.");
            chronicle.Ingest(envelope);
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE game_chronicle_outbox SET delivered_at=$now WHERE event_id=$id AND delivered_at IS NULL";
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    private static ChronicleOutboxEnvelope BuildChronicleEnvelope(SqliteConnection connection,
        SqliteTransaction transaction, string matchId, State state)
    {
        var occurredAt = state.CompletedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var leftProfile = state.ChallengerPublicProfile;
        var rightProfile = state.OpponentPublicProfile;
        var participants = new[]
        {
            Participant(state.ChallengerId, state.ChallengerName, "challenger", leftProfile),
            Participant(state.OpponentId, state.OpponentName, "opponent", rightProfile)
        };
        var decisions = new List<ChronicleDecision>();
        if (state.Mode == "yolo")
        {
            decisions.AddRange(state.ChallengerYoloDecisions.Select(decision =>
                new ChronicleDecision(state.ChallengerId, decision.Title, decision.Consequence, decision.Choice)));
            decisions.AddRange(state.OpponentYoloDecisions.Select(decision =>
                new ChronicleDecision(state.OpponentId, decision.Title, decision.Consequence, decision.Choice)));
        }
        var story = state.Mode == "yolo"
            ? state.YoloBeats.Select(beat => beat.Text).ToArray()
            : BuildRoundsStory(state);
        var awards = BuildAwards(connection, transaction, matchId, state);
        var title = state.Mode == "yolo" ? state.YoloSetting?.Title ?? "Příběh z YOLO"
            : state.Mode == "tactical" ? "Taktický souboj" : "Rychlý souboj";
        var outcome = state.Mode == "yolo" ? state.YoloOutcome : state.Result;
        var winnerId = state.Mode == "yolo" ? state.YoloWinnerId
            : state.Result == "challenger" ? state.ChallengerId
            : state.Result == "opponent" ? state.OpponentId : null;
        var summary = state.Mode == "yolo" ? YoloSummary(state.YoloVerdict, state.YoloSetting)
            : $"{state.ChallengerScore} : {state.OpponentScore}";
        var firstOrdinaryExperience = state.Mode != "yolo"
            && (!HasEarlierModeParticipation(connection, transaction, matchId, state, state.ChallengerId, state.Mode)
                || !HasEarlierModeParticipation(connection, transaction, matchId, state, state.OpponentId, state.Mode));
        var input = new ChronicleEventInput(
            state.Mode == "yolo" ? "yolo" : "duel", matchId, state.GroupId, occurredAt,
            title, summary, participants, outcome, winnerId, story, decisions,
            state.Mode == "yolo" ? state.YoloVerdict : summary,
            IllustrationRequested: state.Mode == "yolo" || awards.Count > 0 || firstOrdinaryExperience,
            DecisiveChoice: state.Mode == "yolo" ? ResolvePublicDecisiveChoice(state) : null);
        return new(input, [state.ChallengerId, state.OpponentId], awards);
    }

    private static bool HasEarlierModeParticipation(SqliteConnection connection, SqliteTransaction transaction,
        string matchId, State state, string ownerId, string mode)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM game_matches WHERE id<>$id AND (challenger_id=$owner OR opponent_id=$owner)
                AND json_extract(state_json,'$.status')='complete' AND json_extract(state_json,'$.mode')=$mode
                AND (json_extract(state_json,'$.completedAt')<$at OR (json_extract(state_json,'$.completedAt')=$at AND id<$id))
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$id", matchId);
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$mode", mode);
        command.Parameters.AddWithValue("$at", state.CompletedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return command.ExecuteScalar() is not null;
    }

    private static string YoloSummary(string? verdict, GameYoloSetting? setting)
    {
        var text = verdict ?? "Příběh zápasu je uložený.";
        if (setting is not null)
        {
            text += $" Cíl: {setting.Objective} Situace: {setting.Situation}";
        }
        return text.Length <= 1000 ? text : text[..1000];
    }

    private static string? ResolvePublicDecisiveChoice(State state)
    {
        if (string.IsNullOrWhiteSpace(state.YoloDecisiveChoice)) { return null; }
        var separator = state.YoloDecisiveChoice.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(state.YoloDecisiveChoice.AsSpan(separator + 1), out var act)) { return null; }
        var ownerId = state.YoloDecisiveChoice[..separator];
        IReadOnlyList<GameYoloDecision> decisions;
        string? displayName;
        if (ownerId == state.ChallengerId)
        {
            decisions = state.ChallengerYoloDecisions;
            displayName = state.ChallengerPublicProfile?.Name ?? state.ChallengerName;
        }
        else if (ownerId == state.OpponentId)
        {
            decisions = state.OpponentYoloDecisions;
            displayName = state.OpponentPublicProfile?.Name ?? state.OpponentName;
        }
        else { return null; }
        var decision = decisions.FirstOrDefault(item => item.Act == act);
        if (decision is null) { return null; }
        var text = $"{displayName}: {decision.Title} — {decision.Consequence}";
        return text.Length <= 300 ? text : text[..300];
    }

    private static ChronicleParticipant Participant(string id, string displayName, string role, GameYoloProfile? profile)
    {
        return new(id, profile?.Name ?? displayName, role, profile?.Name, profile?.WandName,
            profile is null ? null : JsonSerializer.Serialize(profile, Json),
            profile?.ProfileId is { } profileId && profile.ProfileRevision is { } revision
                ? $"character:{profileId}:{revision}:wand:{profile.WandId ?? "none"}:portrait:{profile.PortraitSha256 ?? "none"}"
                : "unknown");
    }

    private static string[] BuildRoundsStory(State state)
    {
        return state.Rounds.Select((round, index) =>
        {
            var winner = round.Winner switch
            {
                "challenger" => state.ChallengerName,
                "opponent" => state.OpponentName,
                _ => null
            };
            var points = 1 + round.ChallengerBid + round.OpponentBid;
            var unit = points == 1 ? "bod" : points is >= 2 and <= 4 ? "body" : "bodů";
            return $"Výměna {index + 1}: {state.ChallengerName} — {ChronicleAction(round.ChallengerChoice)}, sázka {round.ChallengerBid}; "
                + $"{state.OpponentName} — {ChronicleAction(round.OpponentChoice)}, sázka {round.OpponentBid}. "
                + (winner is null ? "Remíza, bez bodů." : $"{winner}: +{points} {unit}.");
        }).ToArray();
    }

    private static string ChronicleAction(string? action) => action switch
    {
        "strike" => "úder", "trick" => "úskok", "shield" => "štít", _ => "nezaznamenaná volba"
    };

    private static IReadOnlyDictionary<string, IReadOnlyList<ChronicleAward>> BuildAwards(SqliteConnection connection,
        SqliteTransaction transaction, string matchId, State state)
    {
        var result = new Dictionary<string, IReadOnlyList<ChronicleAward>>(StringComparer.Ordinal);
        var left = new List<ChronicleAward>();
        var right = new List<ChronicleAward>();
        if (state.Mode == "yolo")
        {
            if (!HasPriorMatch(connection, transaction, matchId, state, state.ChallengerId, "yolo"))
                left.Add(new("expert-victim", "Oběť odborníka", $"group:{state.GroupId}", "První dokončený YOLO zápas ve skupině."));
            if (!HasPriorMatch(connection, transaction, matchId, state, state.OpponentId, "yolo"))
                right.Add(new("expert-victim", "Oběť odborníka", $"group:{state.GroupId}", "První dokončený YOLO zápas ve skupině."));
            AddYoloDecisionAwards(matchId, state.ChallengerYoloDecisions,
                state.OpponentYoloDecisions, left, state);
            AddYoloDecisionAwards(matchId, state.OpponentYoloDecisions,
                state.ChallengerYoloDecisions, right, state);
            if (state.YoloOutcome == "fiasco" && !HasPriorFiasco(connection, transaction, matchId, state))
            {
                var pair = string.Join(":", new[] { state.ChallengerId, state.OpponentId }.Order(StringComparer.Ordinal));
                left.Add(new("accomplices", "Spolupachatelé", $"pair:{state.GroupId}:{pair}", "První společné fiasko této dvojice."));
                right.Add(new("accomplices", "Spolupachatelé", $"pair:{state.GroupId}:{pair}", "První společné fiasko této dvojice."));
            }
        }
        if (state.Result is "challenger" or "opponent")
        {
            var winnerId = state.Result == "challenger" ? state.ChallengerId : state.OpponentId;
            var loserId = winnerId == state.ChallengerId ? state.OpponentId : state.ChallengerId;
            var winnerAwards = winnerId == state.ChallengerId ? left : right;
            if (!HasPriorWin(connection, transaction, matchId, state, winnerId, state.Mode))
                winnerAwards.Add(new($"first-win-{state.Mode}", state.Mode == "tactical" ? "První taktické vítězství" : state.Mode == "yolo" ? "První vítězství v YOLO" : "První vítězství",
                    "account", "První potvrzená výhra v tomto režimu."));
            if (IsComeback(state))
                winnerAwards.Add(new("last-round-turnaround", "Obrat v závěru", $"event:{matchId}", "Před poslední výměnou jsi prohrával a zápas vyhrál."));
            if (FindPreviousPairMatch(connection, transaction, matchId, state, winnerId, loserId) is { } prior
                && prior.WinnerId == loserId)
                winnerAwards.Add(new("successful-rematch", "Úspěšná odveta", $"pair:{state.GroupId}:{winnerId}:{loserId}:{state.Mode}", $"Předchozí prohra v zápase {prior.Id}; odveta ve stejném režimu."));
        }
        AddLegacyGameAwards(connection, transaction, matchId, state, state.ChallengerId, left);
        AddLegacyGameAwards(connection, transaction, matchId, state, state.OpponentId, right);
        result[state.ChallengerId] = left;
        result[state.OpponentId] = right;
        return result;
    }

    private static void AddYoloDecisionAwards(string matchId, IReadOnlyList<GameYoloDecision> own,
        IReadOnlyList<GameYoloDecision> opponent, ICollection<ChronicleAward> awards, State state)
    {
        var delivered = GameYoloScenario.Delivery(own, opponent, state.YoloSetting).GoalMet;
        if (delivered && own.Any(decision => decision.Act == 2 && decision.Choice == "protect")
            && own.Any(decision => decision.Act == 3 && decision.Choice == "finish"))
        {
            awards.Add(new("protection-paid-off", "Pojistka není zbabělost", $"event:{matchId}", "Ochranný plán přispěl k dokončení původního záměru."));
        }
        if (delivered && own.Any(decision => decision.Act == 2 && decision.Choice == "shortcut")
            && own.Any(decision => decision.Act == 3 && decision.Choice == "risk"))
        {
            awards.Add(new("shortcut-paid-off", "Zkratka s koncem", $"event:{matchId}", "Připravená zkratka uspěla při závěrečném riskantním dokončení."));
        }
    }

    private static void AddLegacyGameAwards(SqliteConnection connection, SqliteTransaction transaction,
        string matchId, State state, string ownerId, ICollection<ChronicleAward> awards)
    {
        var isChallenger = ownerId == state.ChallengerId;
        if (state.Rounds.Any(round => HasMindReaderEvidence(isChallenger, round.Winner,
            round.ChallengerBid, round.OpponentBid)))
        {
            awards.Add(new("mind-reader", "Čtenář myšlenek", $"group:{state.GroupId}", "Vyhraná výměna proti soupeři, který vsadil jiskry."));
        }
        var opponentId = isChallenger ? state.OpponentId : state.ChallengerId;
        if (FindEarlierOpponentMatch(connection, transaction, matchId, state, ownerId, opponentId) is { } prior)
        {
            awards.Add(new("remembered-opponent", "Soupeř s pamětí", $"group:{state.GroupId}", $"Druhý dokončený zápas se stejným soupeřem; první zápas {prior}."));
        }
    }

    internal static bool HasMindReaderEvidence(bool isChallenger, string? winner, int challengerBid, int opponentBid) =>
        winner == (isChallenger ? "challenger" : "opponent")
            && (isChallenger ? opponentBid : challengerBid) > 0;

    private static string? FindEarlierOpponentMatch(SqliteConnection connection, SqliteTransaction transaction,
        string matchId, State state, string ownerId, string opponentId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id FROM game_matches WHERE id<>$id AND group_id=$group
                AND ((challenger_id=$owner AND opponent_id=$opponent) OR (challenger_id=$opponent AND opponent_id=$owner))
                AND json_extract(state_json,'$.status')='complete'
                AND (json_extract(state_json,'$.completedAt')<$at OR (json_extract(state_json,'$.completedAt')=$at AND id<$id))
            ORDER BY json_extract(state_json,'$.completedAt') DESC,id DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$id", matchId);
        command.Parameters.AddWithValue("$group", state.GroupId);
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$opponent", opponentId);
        command.Parameters.AddWithValue("$at", state.CompletedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return command.ExecuteScalar() as string;
    }

    private static bool HasPriorMatch(SqliteConnection connection, SqliteTransaction transaction,
        string matchId, State state, string ownerId, string mode)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM game_matches WHERE id<>$id AND group_id=$group
                AND (challenger_id=$owner OR opponent_id=$owner)
                AND json_extract(state_json,'$.status')='complete'
                AND json_extract(state_json,'$.mode')=$mode
                AND (json_extract(state_json,'$.completedAt')<$at OR (json_extract(state_json,'$.completedAt')=$at AND id<$id)) LIMIT 1
            """;
        command.Parameters.AddWithValue("$id", matchId);
        command.Parameters.AddWithValue("$group", state.GroupId);
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$mode", mode);
        command.Parameters.AddWithValue("$at", state.CompletedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return command.ExecuteScalar() is not null;
    }

    private static bool HasPriorWin(SqliteConnection connection, SqliteTransaction transaction,
        string matchId, State state, string ownerId, string mode)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT state_json,challenger_id FROM game_matches WHERE id<>$id
                AND (challenger_id=$owner OR opponent_id=$owner)
                AND json_extract(state_json,'$.status')='complete'
                AND json_extract(state_json,'$.mode')=$mode
                AND (json_extract(state_json,'$.completedAt')<$at OR (json_extract(state_json,'$.completedAt')=$at AND id<$id))
            """;
        command.Parameters.AddWithValue("$id", matchId);
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$mode", mode);
        command.Parameters.AddWithValue("$at", state.CompletedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var previous = JsonSerializer.Deserialize<State>(reader.GetString(0), Json)!;
            if (previous.Result == (reader.GetString(1) == ownerId ? "challenger" : "opponent")) { return true; }
        }
        return false;
    }

    private static (string Id, string? WinnerId)? FindPreviousPairMatch(SqliteConnection connection, SqliteTransaction transaction,
        string matchId, State state, string winnerId, string loserId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id,state_json,challenger_id,opponent_id FROM game_matches WHERE id<>$id AND group_id=$group
                AND ((challenger_id=$winner AND opponent_id=$loser) OR (challenger_id=$loser AND opponent_id=$winner))
                AND json_extract(state_json,'$.status')='complete' AND json_extract(state_json,'$.mode')=$mode
                AND (json_extract(state_json,'$.completedAt')<$at OR (json_extract(state_json,'$.completedAt')=$at AND id<$id))
            ORDER BY json_extract(state_json,'$.completedAt') DESC,id DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$id", matchId);
        command.Parameters.AddWithValue("$group", state.GroupId);
        command.Parameters.AddWithValue("$winner", winnerId);
        command.Parameters.AddWithValue("$loser", loserId);
        command.Parameters.AddWithValue("$mode", state.Mode);
        command.Parameters.AddWithValue("$at", state.CompletedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) { return null; }
        var previous = JsonSerializer.Deserialize<State>(reader.GetString(1), Json)!;
        var priorWinner = previous.Result switch
        {
            "challenger" => reader.GetString(2),
            "opponent" => reader.GetString(3),
            _ => null
        };
        return (reader.GetString(0), priorWinner);
    }

    private static bool HasPriorFiasco(SqliteConnection connection, SqliteTransaction transaction,
        string matchId, State state)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM game_matches WHERE id<>$id AND group_id=$group
                AND ((challenger_id=$left AND opponent_id=$right) OR (challenger_id=$right AND opponent_id=$left))
                AND json_extract(state_json,'$.status')='complete' AND json_extract(state_json,'$.yoloOutcome')='fiasco'
                AND (json_extract(state_json,'$.completedAt')<$at OR (json_extract(state_json,'$.completedAt')=$at AND id<$id)) LIMIT 1
            """;
        command.Parameters.AddWithValue("$id", matchId);
        command.Parameters.AddWithValue("$group", state.GroupId);
        command.Parameters.AddWithValue("$left", state.ChallengerId);
        command.Parameters.AddWithValue("$right", state.OpponentId);
        command.Parameters.AddWithValue("$at", state.CompletedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return command.ExecuteScalar() is not null;
    }

    private static bool IsComeback(State state)
    {
        var challenger = 0;
        var opponent = 0;
        foreach (var round in state.Rounds.Take(Math.Max(0, state.Rounds.Count - 1)))
        {
            var points = 1 + round.ChallengerBid + round.OpponentBid;
            if (round.Winner == "challenger") challenger += points;
            else if (round.Winner == "opponent") opponent += points;
        }
        return state.Result == "challenger" && challenger < opponent
            || state.Result == "opponent" && opponent < challenger;
    }
}
