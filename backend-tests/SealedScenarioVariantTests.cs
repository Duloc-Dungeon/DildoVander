using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kufr.Backend.Features.SealedCase;
using Microsoft.Data.Sqlite;

internal static class SealedScenarioVariantTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static void Run()
    {
        var summaries = SealedScenarioCatalog.Summaries();
        Check(summaries.Length == 3 && summaries.Select(s => s.Id).Distinct().Count() == 3, "three distinct scenario packs");
        Check(SealedScenarioCatalog.Resolve().Id == SealedScenarioCatalog.DefaultId, "old clients retain the original default");
        foreach (var invalid in new[] { "", "unknown", "../scenario.json", "hp_smrtijedi_bystrozorove_v1" })
        { Reject(() => SealedScenarioCatalog.Resolve(invalid), "sealed_case_scenario_unknown"); }

        foreach (var pack in summaries)
        {
            for (var count = 8; count <= 30; count++)
            {
                VerifyMechanics(pack.Id, "full", count);
                if (count <= 12) { VerifyMechanics(pack.Id, "first", count); }
            }
            VerifyPersistence(pack.Id);
        }
        VerifyLegacyReplay();
        Console.WriteLine("PASS scenario catalog, all 8–30/full and 8–12/first rosters, evidence ambiguity, immutable snapshots, durable versions and legacy replay.");
    }

    private static SealedStoredRoster Roster(int count, string seed)
        => new("variant-case", seed, SealedCaseRosterAssignments.Assign(
            Enumerable.Range(1, count).Select(i => "player-" + i).ToArray(), seed)
            .Select(a => new SealedStoredRosterMember(a.PlayerId, a.PlayerId, a.SeatId, "profile-" + a.PlayerId,
                1, null, a.Kit, a.Team, "snapshot-" + a.PlayerId)).ToArray(), 1000);

    private static void VerifyMechanics(string id, string duration, int count)
    {
        var roster = Roster(count, "variant-" + duration + count);
        var beforeRoster = JsonSerializer.Serialize(roster, Json);
        var pack = SealedScenarioCatalog.Resolve(id);
        var game = SealedScenario.Create(roster, duration, pack);
        var original = SealedScenario.Create(roster, duration);
        var scene = game.Scenario!;
        Check(scene.ScenarioId == id && scene.Presentation!.Id == id, "selected pack reaches the checkpoint");
        Check(JsonSerializer.Serialize(scene.Clues, Json) == JsonSerializer.Serialize(original.Scenario!.Clues, Json),
            "presentation never changes evidence targets, candidate recipes, release times or source provenance");
        Check(JsonSerializer.Serialize(game.Checkpoint().Players, Json) == JsonSerializer.Serialize(original.Checkpoint().Players, Json),
            "scenario selection cannot change player placement, kits, powers or state");
        Check(JsonSerializer.Serialize(roster, Json) == beforeRoster, "frozen character/assignment roster remains untouched");
        Check(scene.Clues.Length == SealedCaseRules.Conspirators(count) * 3, "fixed evidence budget per roster");
        foreach (var chain in scene.Clues.GroupBy(c => c.Chain))
        {
            var clues = chain.ToArray();
            var intersection = clues[0].Candidates.Intersect(clues[1].Candidates).Intersect(clues[2].Candidates).ToArray();
            Check(intersection.Length == 1 && roster.Members.Single(m => m.PlayerId == intersection[0]).Team == SealedSecretTeam.Conspirator,
                "all three sourced fragments identify an actual scenario conspirator");
            for (var a = 0; a < 3; a++) for (var b = a + 1; b < 3; b++)
            { Check(clues[a].Candidates.Intersect(clues[b].Candidates).Count() >= 2, "two fragments do not reveal a unique target"); }
        }
        pack.Locations["ARCHIV"] = "unrelated mutable caller";
        Check(scene.Presentation!.Locations["ARCHIV"] != pack.Locations["ARCHIV"], "game takes its own pack snapshot");
        var checkpoint = JsonSerializer.Deserialize<SealedGameCheckpoint>(JsonSerializer.Serialize(game.Checkpoint(), Json), Json)!;
        Check(JsonSerializer.Serialize(SealedGame.Restore(checkpoint).Checkpoint(), Json) == JsonSerializer.Serialize(checkpoint, Json),
            "full variant checkpoint round-trips across serialization and restore");
    }

    private static void VerifyPersistence(string id)
    {
        var path = Path.Combine(Path.GetTempPath(), "sealed-variants-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            var store = new SealedCaseStore(path);
            var created = store.Create("group", "owner", 1, 8, "first", "create-variant", scenarioId: id);
            using var setup = JsonDocument.Parse(created.SetupJson);
            var pack = SealedScenarioCatalog.FromSetup(setup.RootElement);
            Check(pack.Id == id && setup.RootElement.GetProperty("scenarioVersion").GetString() == pack.Version, "lobby records selected ID/version");
            Check(JsonSerializer.Serialize(pack, Json) == setup.RootElement.GetProperty("scenarioSnapshot").GetRawText(), "lobby freezes complete authorial presentation");
            Check(new SealedCaseStore(path).Read(created.Id).SetupJson == created.SetupJson, "lobby survives reopening");
            Check(store.Create("group", "owner", 99, 8, "first", "create-variant", scenarioId: id).SetupJson == created.SetupJson,
                "creation retry returns original snapshot");
            var otherId = SealedScenarioCatalog.Summaries().First(s => s.Id != id).Id;
            Reject(() => store.Create("group", "owner", 99, 8, "first", "create-variant", scenarioId: otherId), "idempotency_key_reused");
            var authorized = false;
            Reject(() => store.Create("group", "owner", 2, 8, "first", "bad-scenario", () =>
            { authorized = true; throw new Exception("Unknown scenario reached authorization."); }, "unknown"), "sealed_case_scenario_unknown");
            Check(!authorized, "unknown ID is rejected before write-side effects");

            var game = SealedScenario.Create(Roster(8, "persist-" + id), "first", pack);
            var world = store.InitializeWorld(created.Id, "group", created.Revision, 2, game);
            using var version = JsonDocument.Parse(world.ContentVersionJson);
            Check(version.RootElement.GetProperty("scenarioId").GetString() == id
                && version.RootElement.GetProperty("scenarioVersion").GetString() == pack.Version, "world content version identifies selected snapshot");
            var reopened = new SealedCaseStore(path);
            var restored = reopened.ReadWorld(created.Id, "group");
            Check(JsonSerializer.Serialize(restored.Checkpoint.Scenario!.Presentation, Json) == JsonSerializer.Serialize(pack, Json),
                "world keeps selected presentation after reopening");
            var orders = game.Players.Keys.Select(player => new SealedAction("hold-" + player, player, 1, SealedIntent.Hold)).ToArray();
            reopened.ResolveWorldChapter(created.Id, "group", 1, world.Revision, 3, orders);
            Check(new SealedCaseStore(path).ReadWorld(created.Id, "group").LastChapter == 1, "nondefault content version resolves after restart");
            using var connection = new SqliteConnection("Data Source=" + path); connection.Open();
            using var query = connection.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM sealed_cases";
            Check((long)query.ExecuteScalar()! == 1, "rejected selection/replay cannot create another case");
        }
        finally { Delete(path); }
    }

    private static void VerifyLegacyReplay()
    {
        var path = Path.Combine(Path.GetTempPath(), "sealed-legacy-variant-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            var store = new SealedCaseStore(path);
            var created = store.Create("group", "owner", 1, 8, "first", "legacy-create");
            using (var connection = new SqliteConnection("Data Source=" + path))
            {
                connection.Open(); using var update = connection.CreateCommand();
                update.CommandText = "UPDATE sealed_cases SET setup_json=$setup WHERE id=$id";
                update.Parameters.AddWithValue("$id", created.Id);
                update.Parameters.AddWithValue("$setup", JsonSerializer.Serialize(new { createdBy = "owner", expectedPlayers = 8, variant = "first" }, Json));
                update.ExecuteNonQuery();
            }
            var replay = store.Create("group", "owner", 2, 8, "first", "legacy-create", scenarioId: SealedScenarioCatalog.DefaultId);
            using var setup = JsonDocument.Parse(replay.SetupJson);
            Check(!setup.RootElement.TryGetProperty("scenarioId", out _), "legacy replay does not rewrite historical setup");
            Check(SealedScenarioCatalog.FromSetup(setup.RootElement).Id == SealedScenarioCatalog.DefaultId, "legacy missing selection resolves original pack");
        }
        finally { Delete(path); }
    }

    private static void Delete(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" }) { if (File.Exists(path + suffix)) { File.Delete(path + suffix); } }
    }
    private static void Reject(Action action, string code)
    {
        try { action(); throw new InvalidOperationException("Expected " + code); }
        catch (SealedCasePersistenceException error) when (error.Code == code) { }
    }
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}

internal static partial class SealedCaseHttpTests
{
    internal static async Task ScenarioVariantsAsync()
    {
        await using var fixture = await Fixture.StartAsync(requestsPerMinute: 4000);
        using (var anonymous = fixture.Client())
        { Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/app/v1/sealed-cases/scenarios")).StatusCode); }
        var clients = new List<HttpClient>();
        try
        {
            for (var index = 1; index <= 8; index++)
            {
                var client = fixture.Client(); await fixture.LoginAsync(client, "p" + index); await fixture.CsrfAsync(client); clients.Add(client);
            }
            var catalog = await clients[0].GetFromJsonAsync<JsonElement>("/app/v1/sealed-cases/scenarios");
            Equal(SealedScenarioCatalog.DefaultId, catalog.GetProperty("defaultScenarioId").GetString());
            Equal(3, catalog.GetProperty("scenarios").GetArrayLength());
            Check(!catalog.GetRawText().Contains("candidate", StringComparison.OrdinalIgnoreCase)
                && !catalog.GetRawText().Contains("chapterMessages", StringComparison.Ordinal), "catalog omits recipes and future narrative beats");
            var invalid = await clients[0].PostAsJsonAsync("/app/v1/sealed-cases",
                new { operationId = "scenario-invalid", expectedPlayers = 8, variant = "first", scenarioId = "unknown" });
            Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Check((await invalid.Content.ReadAsStringAsync()).Contains("sealed_case_scenario_unknown", StringComparison.Ordinal), "stable unknown scenario error");
            var indexOfCase = 0;
            foreach (var scenario in SealedScenarioCatalog.Summaries().Where(s => s.Id != SealedScenarioCatalog.DefaultId))
            {
                indexOfCase++;
                var created = await clients[0].PostAsJsonAsync("/app/v1/sealed-cases", new
                {
                    operationId = "scenario-create-" + indexOfCase, expectedPlayers = 8, variant = "first", scenarioId = scenario.Id,
                    scenarioSnapshot = new { title = "SPOOFED-PACK" }, ownTeam = "Conspirator", serverSeed = "SPOOFED-SEED"
                });
                Equal(HttpStatusCode.Created, created.StatusCode);
                var receipt = await created.Content.ReadFromJsonAsync<JsonElement>();
                Equal(scenario.Title, receipt.GetProperty("scenario").GetProperty("title").GetString());
                Check(!receipt.GetRawText().Contains("SPOOFED", StringComparison.Ordinal), "client cannot provide pack content, assignment or seed");
                var id = receipt.GetProperty("id").GetString()!; var prefix = "/app/v1/sealed-cases/" + id;
                var lobby = await clients[0].GetFromJsonAsync<JsonElement>(prefix + "/play");
                Equal(scenario.Id, lobby.GetProperty("scenario").GetProperty("id").GetString());
                Equal(JsonValueKind.Null, lobby.GetProperty("ownTeam").ValueKind);
                Equal(JsonValueKind.Null, lobby.GetProperty("ownObjective").ValueKind);
                Check(!lobby.TryGetProperty("presentation", out _) && !lobby.TryGetProperty("scenarioSnapshot", out _), "frozen content stays server-only");
                for (var index = 0; index < 8; index++)
                {
                    var ready = await clients[index].PostAsJsonAsync(prefix + "/lobby", new
                    { operationId = "variant-ready-" + index, expectedRevision = index + 1, ready = true, consent = true, scenarioConsent = true });
                    Equal(HttpStatusCode.OK, ready.StatusCode);
                }
                fixture.FreezeClock();
                Equal(HttpStatusCode.OK, (await clients[0].PostAsJsonAsync(prefix + "/start",
                    new { operationId = "variant-start", expectedRevision = 9 })).StatusCode);
                var play = (await clients[0].GetFromJsonAsync<SealedPlayView>(prefix + "/play"))!;
                var pack = SealedScenarioCatalog.Resolve(scenario.Id);
                Equal(pack.Locations["ARCHIV"], play.Locations["ARCHIV"]);
                Equal(play.OwnTeam == "Guardian" ? pack.GuardianObjective : pack.ConspiratorObjective, play.OwnObjective);
                Check(play.Notes.Contains(pack.AcceptedPast), "selected prelude reaches only own legitimate notes");
                var publicContext = SealedCaseAi.ForPlayer(SealedAiRole.PublicNarrator, play);
                Equal(scenario.Id, publicContext.Projection.GetProperty("scenario").GetProperty("id").GetString());
                Check(!publicContext.Projection.TryGetProperty("ownTeam", out _) && !publicContext.Projection.TryGetProperty("ownObjective", out _)
                    && !publicContext.Projection.TryGetProperty("teamMembers", out _), "public AI receives no private assignment or goal");
                var finalPlanning = play with { Phase = "planning", Chapter = 3, Chapters = 3 };
                Equal(pack.ChapterMessages[5], SealedCaseAi.Factual(finalPlanning, true).Text);
                foreach (var lockedPhase in new[] { "quiet", "paused" })
                {
                    var locked = finalPlanning with { Phase = lockedPhase };
                    var factual = SealedCaseAi.Factual(locked, true);
                    Check(factual.Text.Contains("potvrdit nelze", StringComparison.Ordinal)
                        && !factual.Text.Contains("Potvrď poslední akci", StringComparison.Ordinal),
                        "closed/paused chapter does not invite an unavailable action");
                    var context = SealedCaseAi.ForPlayer(SealedAiRole.PublicNarrator, locked);
                    Equal(factual.Text, SealedCaseAi.Fallback(context).Text);
                }
                var aiRequest = SealedCaseAiPlayers.BuildRequest(play);
                var aiJson = JsonSerializer.SerializeToElement(aiRequest.Input);
                Check(!aiJson.GetRawText().Contains("SPOOFED", StringComparison.Ordinal), "AI sees trusted scenario metadata only");
                Equal(scenario.Id, aiJson.GetProperty("scenario").GetProperty("Id").GetString());
                Check((await clients[0].GetFromJsonAsync<JsonElement>("/app/v1/sealed-cases/rewards?scenarioMetadata=true"))
                    .GetProperty("items").EnumerateArray().All(i => i.GetProperty("caseId").GetString() != id), "unreleased scenario has no reward");
                var schedule = SealedCaseRules.Schedule("first", 8);
                fixture.AdvanceSeconds(schedule.IntroductionSeconds + schedule.Chapters * (schedule.PlanningSeconds + schedule.QuietSeconds) + schedule.FinaleSeconds);
                var finished = (await clients[0].GetFromJsonAsync<SealedPlayView>(prefix + "/play"))!;
                Equal("released", finished.Finale!.Phase);
                Check(finished.Finale.Winner == scenario.GuardianLabel || finished.Finale.Winner == scenario.ConspiratorLabel, "computed result uses selected faction labels");
                Equal(finished.Finale.Winner == scenario.GuardianLabel ? pack.FinaleGuardian : pack.FinaleConspirator, finished.Finale.Message);
                var rewards = await clients[0].GetFromJsonAsync<JsonElement>("/app/v1/sealed-cases/rewards?scenarioMetadata=true");
                var reward = rewards.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("caseId").GetString() == id);
                Check(HasExactProperties(reward, "caseId", "releasedAt", "team", "confirmedPlans", "successfulPlans", "scenario"), "opt-in reward has exact bounded shape");
                var metadata = reward.GetProperty("scenario");
                Check(HasExactProperties(metadata, "id", "title", "teamLabel"), "reward exposes no complete pack or other-player data");
                Equal(scenario.Id, metadata.GetProperty("id").GetString()); Equal(scenario.Title, metadata.GetProperty("title").GetString());
                Equal(play.OwnTeam == "Guardian" ? scenario.GuardianLabel : scenario.ConspiratorLabel, metadata.GetProperty("teamLabel").GetString());
                Equal(0, (await clients[0].GetFromJsonAsync<JsonElement>("/app/v1/sealed-cases/rewards")).GetProperty("items").GetArrayLength());
                var reopened = new SealedCaseStore(Path.Combine(fixture.DataRoot, "sealed-case.sqlite"));
                using var saved = JsonDocument.Parse(reopened.Read(id).SetupJson);
                Equal(scenario.Id, SealedScenarioCatalog.FromSetup(saved.RootElement).Id);
            }
            Console.WriteLine("PASS HTTPS scenario selection, immutable consent context, untrusted request fields, public AI boundary and optional private reward metadata.");
        }
        finally { foreach (var client in clients) { client.Dispose(); } }
    }
}
