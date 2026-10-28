using System.Text.Json;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.SealedCase;

internal sealed record SealedPlayerCard(string Id, string Name, string Seat, string State, string? Location, string? Mask, string? PortraitUrl, bool IsAi = false);
internal sealed record SealedClueView(string Id, string Chain, string Family, string[] Candidates, string SourceEvent);
internal sealed record SealedSource(string Id, string Label);
internal sealed record SealedChoice(string Id, string Label, string Benefit, string Cost, string Risk, SealedPlanConfirmation Plan);
internal sealed record SealedStatement(string Id, string Actor, string Body, string[] Recipients, bool Public, long CreatedAt);
internal sealed record SealedRecap(int Chapter, string Actor, string? Target, string Intent, string Result, string EventRef,
    string? Reason = null, SealedModifiers? Modifiers = null, SealedRolls? Rolls = null, bool TraceCreated = false);
internal sealed record SealedFinaleView(string Phase, int BallotSize, bool OwnSubmitted, string[] OwnBallot,
    string[] RunoffCandidates, int RunoffPlaces, string[]? Selected, string? Winner, string[]? Conspirators, SealedRecap[]? Reconstruction);
internal sealed record SealedPlayView(string Id, string Phase, long ServerNow, int Chapter, int Chapters,
    long? PlanningEndsAt, long? QuietEndsAt, long? FinaleEndsAt, int ExpectedPlayers, int ReadyPlayers,
    string OwnId, bool OwnReady, bool OwnScenarioConsent, bool CanManage, bool CanStart, long? LobbyRevision,
    string OwnState, string? OwnTeam, string? OwnKit, string[] TeamMembers, string? Location,
    Dictionary<string, string> Locations, string[] AdjacentLocations, SealedPlayerCard[] Players,
    SealedAction? OwnPlan, SealedChoice[] Choices, SealedClueView[] Clues, SealedItem[] OwnItems, SealedItem[] VisibleItems,
    string[] Notes, SealedStatement[] Statements, SealedRecap[] OwnOutcomes, SealedFinaleView? Finale, SealedSource[]? Sources = null, bool OwnOversight = false, bool CanManageAi = false, bool AiAvailable = false, bool CanJoin = true)
{
    public SealedNarration PublicNarration => SealedCaseAi.Factual(this, true);
    public SealedNarration PrivateNarration => SealedCaseAi.Factual(this, false);
}

internal sealed partial class SealedCaseStore
{
    private static SealedStoredRosterMember[] RosterMembers(SqliteConnection connection, SqliteTransaction transaction, string caseId)
    {
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT roster_json FROM sealed_case_rosters WHERE case_id=$case"; query.Parameters.AddWithValue("$case", caseId);
        return query.ExecuteScalar() is string json ? JsonSerializer.Deserialize<SealedStoredRosterMember[]>(json, Json)! : [];
    }

    private static CustomerAdmissionGuard DemandPlayerAdmission(Func<CustomerAdmissionGuard> authorize, string actor, string group, bool allowOwner = false)
    {
        var held = authorize();
        if (held.Proof.AccountId == actor && held.Proof.LaunchGroupId == group && held.Proof.LaunchGroupRevokedAt is null
            && (held.Proof.Roles.Contains(AccountRoles.Customer, StringComparer.Ordinal) || allowOwner && held.Proof.Roles.Contains(AccountRoles.Owner, StringComparer.Ordinal))) { return held; }
        held.Dispose(); throw new SealedCasePersistenceException("case_not_found", 404);
    }

    internal string? LatestCase(string groupId)
    {
        using var connection = Open(); using var query = connection.CreateCommand();
        query.CommandText = "SELECT id FROM sealed_cases WHERE group_id=$group AND status!='privacy_terminated' ORDER BY created_at DESC,rowid DESC LIMIT 1";
        query.Parameters.AddWithValue("$group", groupId); return query.ExecuteScalar() as string;
    }

    internal SealedCaseActionReceipt StartPlayableCase(string caseId, string groupId, string actor, string operationId,
        long expectedRevision, Func<CustomerAdmissionGuard> authorize, Func<IReadOnlySet<string>> eligible)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length is < 8 or > 128) { throw new SealedCasePersistenceException("invalid_operation_id", 400); }
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using var admission = AcquireCaseAccess(connection, transaction, caseId, () => DemandPlayerAdmission(authorize, actor, groupId, allowOwner: true), actor);
        var revision = ReadCaseRevision(connection, transaction, caseId, groupId);
        using var setupQuery = connection.CreateCommand(); setupQuery.Transaction = transaction;
        setupQuery.CommandText = "SELECT setup_json,status FROM sealed_cases WHERE id=$case"; setupQuery.Parameters.AddWithValue("$case", caseId);
        string setupJson; string status;
        using (var reader = setupQuery.ExecuteReader()) { reader.Read(); setupJson = reader.GetString(0); status = reader.GetString(1); }
        using var setup = JsonDocument.Parse(setupJson);
        if (setup.RootElement.GetProperty("createdBy").GetString() != actor) { throw new SealedCasePersistenceException("sealed_case_host_required", 403); }
        using var replay = connection.CreateCommand(); replay.Transaction = transaction;
        replay.CommandText = "SELECT event_id,revision,kind,expected_revision FROM sealed_case_events WHERE case_id=$case AND actor_id=$actor AND idempotency_key=$key";
        replay.Parameters.AddWithValue("$case", caseId); replay.Parameters.AddWithValue("$actor", actor); replay.Parameters.AddWithValue("$key", operationId);
        using (var reader = replay.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(2) != "case-started" || reader.GetInt64(3) != expectedRevision) { throw new SealedCasePersistenceException("idempotency_key_reused", 409); }
                var receipt = new SealedCaseActionReceipt(reader.GetString(0), reader.GetInt64(1), true, "running");
                reader.Close(); transaction.Commit(); return receipt;
            }
        }
        if (revision != expectedRevision) { throw new SealedCasePersistenceException("revision_conflict", 409); }
        var members = RosterMembers(connection, transaction, caseId);
        var expected = setup.RootElement.GetProperty("expectedPlayers").GetInt32();
        if (status != "lobby" || members.Length != expected) { throw new SealedCasePersistenceException("sealed_case_lobby_not_ready", 409); }
        var allowed = eligible();
        if (members.Any(m => !m.IsAi && !allowed.Contains(m.AccountId))) { throw new SealedCasePersistenceException("sealed_case_roster_invalidated", 409); }
        var latest = LobbyEntries(connection, transaction, caseId);
        if (members.Any(m => !latest.TryGetValue(m.AccountId, out var entry) || !entry.ScenarioConsent))
        { throw new SealedCasePersistenceException("sealed_case_scenario_consent_required", 409); }
        using var rosterQuery = connection.CreateCommand(); rosterQuery.Transaction = transaction;
        rosterQuery.CommandText = "SELECT server_seed,frozen_at FROM sealed_case_rosters WHERE case_id=$case"; rosterQuery.Parameters.AddWithValue("$case", caseId);
        string seed; long frozenAt;
        using (var reader = rosterQuery.ExecuteReader()) { reader.Read(); seed = reader.GetString(0); frozenAt = reader.GetInt64(1); }
        var variant = setup.RootElement.GetProperty("variant").GetString()!;
        var game = SealedScenario.Create(new(caseId, seed, members, frozenAt), variant);
        var now = clock.GetUtcNow().ToUnixTimeSeconds(); var next = revision + 1;
        using var create = connection.CreateCommand(); create.Transaction = transaction;
        create.CommandText = """
            INSERT INTO sealed_case_world VALUES($case,$version,$world,$next,$now);
            INSERT INTO sealed_case_timing(case_id,variant,original_players,started_at) VALUES($case,$variant,$players,$now);
            UPDATE sealed_cases SET status='running',revision=$next,started_at=$now,chapter=0,chapter_deadline=$deadline WHERE id=$case;
            INSERT INTO sealed_case_events(event_id,case_id,actor_id,idempotency_key,expected_revision,revision,kind,payload_json,created_at)
                VALUES($event,$case,$actor,$key,$old,$next,'case-started',$payload,$now);
            """;
        create.Parameters.AddWithValue("$case", caseId); create.Parameters.AddWithValue("$version", ContentVersionJson);
        create.Parameters.AddWithValue("$world", JsonSerializer.Serialize(game.Checkpoint(), Json)); create.Parameters.AddWithValue("$next", next);
        create.Parameters.AddWithValue("$now", now); create.Parameters.AddWithValue("$variant", variant); create.Parameters.AddWithValue("$players", expected);
        var schedule = SealedCaseRules.Schedule(variant, expected);
        create.Parameters.AddWithValue("$deadline", now + schedule.IntroductionSeconds + schedule.PlanningSeconds);
        create.Parameters.AddWithValue("$event", caseId + ":prelude"); create.Parameters.AddWithValue("$actor", actor);
        create.Parameters.AddWithValue("$key", operationId); create.Parameters.AddWithValue("$old", revision);
        create.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new { scenario = game.Scenario!.ScenarioId, prelude = SealedScenario.Definition.GetProperty("prelude").GetProperty("public_facts") }, Json));
        create.ExecuteNonQuery(); transaction.Commit(); return new(caseId + ":prelude", next, false, "running");
    }

    private static Dictionary<string, SealedCaseLobbyEntry> LobbyEntries(SqliteConnection connection, SqliteTransaction transaction, string caseId)
    {
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT actor_id,payload_json FROM sealed_case_events WHERE case_id=$case AND kind='lobby' ORDER BY revision";
        query.Parameters.AddWithValue("$case", caseId); using var reader = query.ExecuteReader(); var latest = new Dictionary<string, SealedCaseLobbyEntry>(StringComparer.Ordinal);
        while (reader.Read()) { latest[reader.GetString(0)] = JsonSerializer.Deserialize<SealedCaseLobbyEntry>(reader.GetString(1), Json)!; }
        reader.Close();
        query.CommandText = "SELECT DISTINCT actor_id FROM sealed_case_events WHERE case_id=$case AND kind='scenario-consent'";
        using var consents = query.ExecuteReader();
        while (consents.Read())
        {
            var id = consents.GetString(0); if (latest.TryGetValue(id, out var entry)) { latest[id] = entry with { ScenarioConsent = true }; }
        }
        consents.Close();
        foreach (var bot in AiPlayers(connection, transaction, caseId))
        { latest[bot.Key] = new(bot.Key, true, true, "ai:" + bot.Key, true); }
        return latest;
    }

    internal void AdvancePlayableCase(string caseId, string groupId, string actor, Func<CustomerAdmissionGuard> authorize)
        => AdvancePlayableCaseCore(caseId, groupId, () => DemandPlayerAdmission(authorize, actor, groupId, allowOwner: true));

    internal void AdvanceAiCase(string caseId, string groupId, Func<IDisposable> authorize)
        => AdvancePlayableCaseCore(caseId, groupId, authorize);

    private void AdvancePlayableCaseCore(string caseId, string groupId, Func<IDisposable> authorize)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            SealedStoredWorld world;
            try { world = ReadWorld(caseId, groupId); }
            catch (SealedCasePersistenceException error) when (error.Code == "world_not_initialized") { return; }
            var timing = ReadTimeline(caseId, groupId, authorize);
            if (timing.PausedAt is not null) { return; }
            var schedule = SealedCaseRules.Schedule(timing.Variant, timing.OriginalPlayers);
            var next = world.LastChapter + 1;
            var deadline = timing.IntroductionEndsAt + (next - 1L) * (schedule.PlanningSeconds + schedule.QuietSeconds) + schedule.PlanningSeconds;
            if (next > schedule.Chapters || clock.GetUtcNow().ToUnixTimeSeconds() < deadline) { return; }
            try { ResolveConfirmedChapter(caseId, groupId, next, world.Revision, authorize); }
            catch (SealedCasePersistenceException error) when (error.Code is "revision_conflict" or "resolved_chapter_conflict") { }
        }
    }

    internal SealedPlayView ReadPlay(string caseId, string groupId, string actor, Func<CustomerAdmissionGuard> authorize)
        => ReadPlayCore(caseId, groupId, actor, () => authorize(), null);

    internal SealedPlayView ReadAiPlay(string caseId, string groupId, string bot, Func<CustomerAccountAdmission> authorize)
        => ReadPlayCore(caseId, groupId, bot, () => authorize(), bot);

    private SealedPlayView ReadPlayCore(string caseId, string groupId, string actor, Func<IDisposable> authorize, string? aiPlayer)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: true);
        IDisposable? playerAdmission = null;
        using var admission = AcquireCaseAccess(connection, transaction, caseId,
            () => playerAdmission = DemandAiOrPlayerAdmission(connection, transaction, caseId, groupId, actor, authorize, aiPlayer is not null, allowOwner: true), actor);
        var reservation = playerAdmission switch
        {
            CustomerAdmissionGuard human => human.AccountReservation,
            CustomerAccountAdmission owner => owner.AccountReservation,
            _ => throw new SealedCasePersistenceException("case_not_found", 404)
        };
        using var eligibleQuery = reservation.Command("""
            SELECT user.Id FROM AspNetUsers user WHERE user.LaunchGroupId=$group AND user.LaunchGroupRevokedAt IS NULL
                AND user.Disabled=0 AND NOT EXISTS(SELECT 1 FROM DeletionRequests deletion WHERE deletion.UserId=user.Id)
                AND EXISTS(SELECT 1 FROM AspNetUserRoles membership JOIN AspNetRoles role ON role.Id=membership.RoleId WHERE membership.UserId=user.Id AND role.Name=$customer)
            """);
        eligibleQuery.Parameters.AddWithValue("$group", groupId); eligibleQuery.Parameters.AddWithValue("$customer", AccountRoles.Customer);
        var eligible = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = eligibleQuery.ExecuteReader()) { while (reader.Read()) { eligible.Add(reader.GetString(0)); } }
        var bots = AiPlayers(connection, transaction, caseId);
        eligible.UnionWith(bots.Keys);
        var revision = ReadCaseRevision(connection, transaction, caseId, groupId);
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT setup_json FROM sealed_cases WHERE id=$case"; query.Parameters.AddWithValue("$case", caseId);
        using var setup = JsonDocument.Parse((string)query.ExecuteScalar()!);
        var expected = setup.RootElement.GetProperty("expectedPlayers").GetInt32(); var variant = setup.RootElement.GetProperty("variant").GetString()!;
        var canManage = aiPlayer is null && setup.RootElement.GetProperty("createdBy").GetString() == actor;
        var canManageAi = canManage && playerAdmission is CustomerAdmissionGuard humanAdmission
            && humanAdmission.Proof.Roles.Contains(AccountRoles.Owner, StringComparer.Ordinal);
        var latest = LobbyEntries(connection, transaction, caseId); latest.TryGetValue(actor, out var ownEntry);
        var members = RosterMembers(connection, transaction, caseId);
        var names = new Dictionary<string, string>(StringComparer.Ordinal); var portraits = new HashSet<string>(StringComparer.Ordinal);
        query.CommandText = "SELECT subject_account,profile_json,image IS NOT NULL FROM sealed_case_private_snapshots WHERE case_id=$case";
        using (var reader = query.ExecuteReader())
        {
            while (reader.Read())
            {
                names[reader.GetString(0)] = JsonSerializer.Deserialize<CharacterProfile>(reader.GetString(1), Json)!.UserInput.Name;
                if (reader.GetBoolean(2)) { portraits.Add(reader.GetString(0)); }
            }
        }
        foreach (var bot in bots) { names[bot.Key] = bot.Value; }
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var locations = SealedScenario.Definition.GetProperty("map").GetProperty("zones").EnumerateArray()
            .ToDictionary(z => z.GetProperty("id").GetString()!, z => z.GetProperty("name").GetString()!, StringComparer.Ordinal);
        var schedule = SealedCaseRules.Schedule(variant, expected);
        SealedStoredWorld? world = null;
        try { world = ReadWorld(connection, transaction, caseId, revision); }
        catch (SealedCasePersistenceException error) when (error.Code == "world_not_initialized") { }
        var ready = latest.Values.Count(entry => entry.Ready && entry.Consent && entry.SnapshotRef is not null && eligible.Contains(entry.PlayerId));
        if (world is null)
        {
            var lobbyPlayers = latest.Values.Where(e => e.Ready && e.SnapshotRef is not null && eligible.Contains(e.PlayerId)).Select(e =>
                new SealedPlayerCard(e.PlayerId, names.GetValueOrDefault(e.PlayerId, "Host"), "", "Připraven", null, null,
                    portraits.Contains(e.PlayerId) ? $"/app/v1/sealed-cases/{caseId}/portraits/{e.PlayerId}" : null, bots.ContainsKey(e.PlayerId))).ToArray();
            transaction.Commit();
            return new(caseId, "lobby", now, 0, schedule.Chapters, null, null, null, expected, ready, actor,
                ownEntry is { Ready: true, Consent: true, SnapshotRef: not null }, ownEntry?.ScenarioConsent == true,
                canManage, canManage && members.Length == expected && members.All(m => eligible.Contains(m.AccountId) && latest.GetValueOrDefault(m.AccountId)?.ScenarioConsent == true), revision,
                "Awake", null, null, [], null, locations, [], lobbyPlayers, null, [], [], [], [], [], [], [], null, CanManageAi: canManageAi, CanJoin: playerAdmission is CustomerAdmissionGuard ownHuman && ownHuman.Proof.Roles.Contains(AccountRoles.Customer, StringComparer.Ordinal));
        }
        var member = playerAdmission is CustomerAdmissionGuard cookie && !cookie.Proof.Roles.Contains(AccountRoles.Customer, StringComparer.Ordinal)
            ? null : members.SingleOrDefault(m => m.AccountId == actor);
        if (member is null)
        {
            if (!canManage) { throw new SealedCasePersistenceException("case_not_found", 404); }
            var operatorTiming = ReadTiming(connection, transaction, caseId, revision, now);
            var publicPlayers = members.Select(m => new SealedPlayerCard(m.PlayerId, names.GetValueOrDefault(m.AccountId, "Host"), m.SeatId, "Stav neznáš", null, null, null, m.IsAi)).ToArray();
            var publicMessages = Statements(connection, transaction, caseId, actor).Where(m => m.Public).ToArray();
            var operatorFinale = operatorTiming.Phase == "finished" ? ReadFinale(connection, transaction, caseId, members, actor, operatorTiming) : null;
            transaction.Commit();
            return new(caseId, operatorTiming.Phase, now, operatorTiming.Chapter, operatorTiming.Chapters, operatorTiming.PlanningEndsAt,
                operatorTiming.QuietEndsAt, operatorTiming.FinaleEndsAt, expected, ready, actor, false, true, true, false, null,
                "Operator", null, null, [], null, locations, [], publicPlayers, null, [], [], [], [], [], publicMessages, [], operatorFinale, CanJoin: false);
        }
        var player = world.Checkpoint.Players.Single(p => p.Id == member.PlayerId);
        var scene = world.Checkpoint.Scenario ?? throw new SealedCasePersistenceException("sealed_case_scenario_unavailable", 409);
        var timing = ReadTiming(connection, transaction, caseId, revision, now);
        var state = player.State;
        if (state == SealedPlayerState.Sleeping && player.SleepingUntilStart <= timing.Chapter
            || state == SealedPlayerState.Unconscious && player.UnconsciousUntilEnd < timing.Chapter) { state = SealedPlayerState.Awake; }
        var cards = members.Select(m =>
        {
            var observed = scene.Observations?.GetValueOrDefault(player.Id, []).SingleOrDefault(o => o.PlayerId == m.PlayerId);
            var label = m.PlayerId == player.Id ? state.ToString() : observed is null ? "Stav neznáš"
                : observed.Chapter < world.LastChapter ? "Posledně: " + observed.Appearance : observed.Appearance;
            return new SealedPlayerCard(m.PlayerId, names.GetValueOrDefault(m.AccountId, "Host"), m.SeatId, label,
                m.PlayerId == player.Id ? player.Location : observed?.Location, observed?.Mask,
                portraits.Contains(m.AccountId) ? $"/app/v1/sealed-cases/{caseId}/portraits/{m.PlayerId}" : null, m.IsAi);
        }).ToArray();
        var ownPlan = ReadConfirmedPlans(connection, transaction, caseId, timing.Chapter).GetValueOrDefault(player.Id);
        var clues = scene.Clues.Where(c => scene.KnownClues.GetValueOrDefault(player.Id, []).Contains(c.Id))
            .Select(c => new SealedClueView(c.Id, c.Chain, c.Family, c.Candidates.ToArray(), c.SourceEvent)).ToArray();
        var statements = Statements(connection, transaction, caseId, actor).Where(m => m.Public || m.Actor == actor || m.Recipients.Contains(actor))
            .Select(m => m.Actor == actor ? m : m with { Recipients = [] }).ToArray();
        var outcomes = Recap(connection, transaction, caseId).Where(e => e.Actor == player.Id && ReadConfirmedPlans(connection, transaction, caseId, e.Chapter).ContainsKey(player.Id))
            .Select(e => e with { Result = "Plán přijatý · výsledek zatím neznáš", EventRef = "", Reason = null, Modifiers = null, Rolls = null, TraceCreated = false }).ToArray();
        var finale = timing.Phase is "finale" or "finished" ? ReadFinale(connection, transaction, caseId, members, actor, timing) : null;
        var choices = Choices(player.Id, state, player.Location, timing.Chapter, ownPlan?.Revision ?? 0, clues, scene, timing.Variant);
        transaction.Commit();
        return new(caseId, timing.Phase, now, timing.Chapter, timing.Chapters, timing.PlanningEndsAt, timing.QuietEndsAt, timing.FinaleEndsAt,
            expected, ready, actor, true, true, canManage, false, null, state.ToString(), member.Team.ToString(), member.Kit.ToString(),
            member.Team == SealedSecretTeam.Conspirator ? members.Where(m => m.Team == SealedSecretTeam.Conspirator).Select(m => m.PlayerId).ToArray() : [],
            player.Location, locations, world.Checkpoint.Adjacency.GetValueOrDefault(player.Location, []), cards, ownPlan, choices, clues,
            scene.Items.Values.Where(i => i.OwnerId == player.Id).ToArray(),
            scene.Items.Values.Where(i => i.OwnerId != player.Id && scene.Observations?.GetValueOrDefault(player.Id, [])
                .Any(o => o.PlayerId == i.OwnerId && o.Chapter == world.LastChapter && o.Location == player.Location) == true).ToArray(),
            scene.Notes.GetValueOrDefault(player.Id, []), statements, outcomes, finale,
            scene.Clues.Where(c => c.Location == player.Location && c.ReleaseChapter <= timing.Chapter).Select(c => new SealedSource(c.Id, "Otisk " + c.Id)).ToArray(),
            scene.Oversight?.GetValueOrDefault(timing.Chapter) == player.Id);
    }

    private static SealedChoice[] Choices(string actor, SealedPlayerState state, string location, int chapter, int actionRevision,
        SealedClueView[] clues, SealedScenarioState scene, string variant)
    {
        SealedChoice Choice(string id, string label, string benefit, string risk, SealedIntent intent, string? resource = null, string? method = null)
            => new(id, label, benefit, "Jeden hlavní tah", risk, new(chapter, actionRevision, true, intent, Method: method, ResourceId: resource));
        if (state == SealedPlayerState.Unconscious)
        {
            return [Choice("dream-scene", "Vrátit se k vlastní scéně", "Vlastní zaznamenaná vzpomínka", "Sen nepotvrdí cizí úmysl", SealedIntent.Dream, method: "SCENE"),
                Choice("dream-voice", "Naslouchat vlastní vzpomínce", "Vlastní přijatá výpověď", "Vzpomínka není nový důkaz", SealedIntent.Dream, method: "STATEMENT"),
                Choice("dream-context", "Prohlédnout vlastní souvislosti", "Vlastní stav a předměty", "Bez přístupu k cizím plánům", SealedIntent.Dream, method: "CONTEXT")];
        }
        if (state == SealedPlayerState.Echo)
        {
            return [Choice("echo-review", "Projít vlastní stopy", "Přehled již známých souvislostí", "Nevytvoří nové svědectví", SealedIntent.EchoReview),
                Choice("echo-scene", "Prozkoumat ozvěnu scény", "Jedna dostupná doložená stopa", "Bez zásahu do těl a předmětů", SealedIntent.EchoScene, location),
                Choice("echo-deliver", "Připravit vlastní vzkaz", "Doručení již známé stopy vybranému hráči", "Vyžaduje známou stopu a adresáta", SealedIntent.EchoDeliver, clues.FirstOrDefault()?.Id)];
        }
        return [Choice("investigate", "Prozkoumat místo", "Nejvýše jedna dostupná stopa", "Místo zatím nemusí mít nový důkaz", SealedIntent.Investigate),
            Choice("guard", "Chránit sebe", "Ochrana při skutečném střetu", "Nebudeš současně pátrat", SealedIntent.Guard),
            Choice("prepare", "Připravit příští pátrání", "Bonus pro příští příslušnou akci", "Příprava se neuchová navždy", SealedIntent.Prepare) with
                { Plan = new(chapter, actionRevision, true, SealedIntent.Prepare, PreparedIntent: SealedIntent.Investigate) }];
    }
}
