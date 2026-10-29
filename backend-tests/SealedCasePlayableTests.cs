using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.SealedCase;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

internal static partial class SealedCaseHttpTests
{
    internal static async Task PlayableCasesAsync()
    {
        EvidenceGraphAndScenarioActions();
        PassiveDefenseTheftAndOversight();
        await PlayCaseAsync("first", 8);
        await PlayCaseAsync("full", 30);
        Console.WriteLine("PASS playable HTTPS tutorial/full games, private plans/messages, scenario graph, original roster, final votes/reconstruction.");
    }

    private static SealedStoredRoster SyntheticRoster(int count, string seed)
    {
        var assignments = SealedCaseRosterAssignments.Assign(Enumerable.Range(1, count).Select(i => "player-" + i).ToArray(), seed);
        return new("synthetic-case", seed, assignments.Select(a => new SealedStoredRosterMember(a.PlayerId, a.PlayerId, a.SeatId,
            "profile-" + a.PlayerId, 1, null, a.Kit, a.Team, "snapshot-" + a.PlayerId)).ToArray(), 1000);
    }

    private static void EvidenceGraphAndScenarioActions()
    {
        for (var count = 8; count <= 30; count++)
        {
            var roster = SyntheticRoster(count, "graph-" + count); var game = SealedScenario.Create(roster, "full");
            Equal(5, game.Players.Values.Select(p => p.Location).Distinct().Count());
            Equal(SealedCaseRules.Conspirators(count) * 3, game.Scenario!.Clues.Length);
            foreach (var chain in game.Scenario.Clues.GroupBy(c => c.Chain))
            {
                var facts = chain.ToArray(); var intersection = facts[0].Candidates.Intersect(facts[1].Candidates).Intersect(facts[2].Candidates).ToArray();
                Equal(1, intersection.Length); Check(roster.Members.Single(m => m.PlayerId == intersection[0]).Team == SealedSecretTeam.Conspirator, "triple identifies an actual conspirator");
                for (var a = 0; a < 3; a++) for (var b = a + 1; b < 3; b++)
                    Check(facts[a].Candidates.Intersect(facts[b].Candidates).Count() >= 2, "a pair never identifies the target alone");
            }
        }
        var original = SealedScenario.Create(SyntheticRoster(8, "scene-actions"), "first");
        foreach (var player in original.Players.Values) { player.Location = "ARCHIV"; }
        original.Resolve(1, original.Players.Keys.Select(id => new SealedAction("inspect-" + id, id, 1, SealedIntent.Investigate)).ToArray());
        var scene = original.Scenario!;
        Equal(2, scene.KnownClues.Values.SelectMany(ids => ids).Distinct().Count());
        Check(scene.KnownClues.Values.All(ids => ids.Length <= 1), "one action obtains at most one fact");
        Equal(JsonSerializer.Serialize(original.Checkpoint()), JsonSerializer.Serialize(SealedGame.Restore(original.Checkpoint()).Checkpoint()));
        var item = scene.Items.Values.First(); var owner = item.OwnerId; var thief = original.Players.Keys.First(id => id != owner);
        var copied = SealedGame.Restore(original.Checkpoint());
        copied.Resolve(2, [new("transfer", owner, 2, SealedIntent.Transfer, thief, ResourceId: item.Id)]);
        Equal(thief, copied.Scenario!.Items[item.Id].OwnerId); Equal(8, copied.Scenario.Items.Count);
        var sleeper = copied.Players.Keys.First(id => id != thief); copied.Players[sleeper].State = SealedPlayerState.Unconscious;
        copied.Players[sleeper].UnconsciousUntilEnd = 4;
        copied.Resolve(3, [new("dream", sleeper, 3, SealedIntent.Dream)]);
        Check(copied.Scenario!.Experiences!.GetValueOrDefault(sleeper, []).Any(e => e.Confidence == "UNCERTAIN"), "dream is an uncertain own fragment");
    }

    private static void PassiveDefenseTheftAndOversight()
    {
        SealedGame Scene(string location, Func<string, string, int> dice)
        {
            var basis = SealedScenario.Create(SyntheticRoster(8, "mechanics-regression"), "first");
            foreach (var player in basis.Players.Values) { player.Location = location; }
            return new SealedGame(basis.Players.Values, "mechanics-regression", dice: dice) { Scenario = basis.Scenario!.Copy() };
        }
        foreach (var location in new[] { "ARCHIV", "SALON" })
        {
            var game = Scene(location, (_, _) => 3); var ids = game.Players.Keys.ToArray();
            var result = game.Resolve(1, [new("a", ids[0], 1, SealedIntent.Stun, ids[1], Method: "IMPACT_SEAL"),
                new("b", ids[1], 1, SealedIntent.Hold, DefensePolicy: SealedDefensePolicy.TakeCover, ShelteredPosition: true)]);
            Equal(location == "ARCHIV" ? 1 : 0, result.Single(e => e.ActorId == ids[0]).Modifiers!.Cover);
        }
        var theft = Scene("ARCHIV", (_, side) => side == "attack" ? 6 : 1); var actors = theft.Players.Keys.ToArray();
        var item = theft.Scenario!.Items.Values.First(); var thieves = actors.Where(id => id != item.OwnerId).Take(2).ToArray();
        var stolen = theft.Resolve(1, [new("theft-1", thieves[0], 1, SealedIntent.Steal, ResourceId: item.Id),
            new("theft-2", thieves[1], 1, SealedIntent.Steal, ResourceId: item.Id),
            new("transfer", item.OwnerId, 1, SealedIntent.Transfer, thieves[0], ResourceId: item.Id)]);
        Equal(1, stolen.Count(e => e.Intent == SealedIntent.Steal && e.Result == SealedResult.Success));
        Equal(stolen.Single(e => e.Intent == SealedIntent.Steal && e.Result == SealedResult.Success).ActorId, theft.Scenario.Items[item.Id].OwnerId);
        Equal(8, theft.Scenario.Items.Count);
        var oversight = Scene("ARCHIV", (_, side) => side == "attack" ? 6 : 1);
        var overseer = oversight.Players.Keys.First(); var target = oversight.Players.Keys.First(id => id != overseer);
        oversight.Scenario!.Oversight![1] = overseer;
        oversight.Resolve(1, [new("overseen-stun", overseer, 1, SealedIntent.Stun, target, Method: "IMPACT_SEAL")]);
        Check(oversight.Scenario.Experiences!.Values.SelectMany(e => e).Any(e => e.Id.Contains(":oversight:", StringComparison.Ordinal)), "oversight creates a sourced witness trace");
        var retreat = Scene("ARCHIV", (_, side) => side == "attack" ? 1 : 6); var retreatIds = retreat.Players.Keys.ToArray();
        retreat.Resolve(1, [new("attack", retreatIds[0], 1, SealedIntent.Stun, retreatIds[1], Method: "IMPACT_SEAL"),
            new("withdraw", retreatIds[1], 1, SealedIntent.Hold, DefensePolicy: SealedDefensePolicy.Withdraw)]);
        Check(retreat.Players[retreatIds[1]].Location != "ARCHIV", "withdraw can use only the unused adjacent move after contacts");
        Console.WriteLine("PASS sourced oversight, reachable cover, passive withdrawal and shared item contest.");
    }

    private static async Task PlayCaseAsync(string variant, int count)
    {
        await using var fixture = await Fixture.StartAsync(requestsPerMinute: 4000);
        var clients = new List<HttpClient>();
        try
        {
            for (var index = 1; index <= count; index++)
            {
                var client = fixture.Client(); await fixture.LoginAsync(client, "p" + index); await fixture.CsrfAsync(client); clients.Add(client);
            }
            var created = await clients[0].PostAsJsonAsync("/app/v1/sealed-cases", new { operationId = "playable-create", expectedPlayers = count, variant });
            created.EnsureSuccessStatusCode(); var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            var prefix = "/app/v1/sealed-cases/" + id;
            for (var index = 0; index < count; index++)
            {
                Equal(HttpStatusCode.OK, (await clients[index].PostAsJsonAsync(prefix + "/lobby", new
                { operationId = "play-ready-" + index, expectedRevision = index + 1, ready = true, consent = true, scenarioConsent = true })).StatusCode);
            }
            fixture.FreezeClock();
            var start = await clients[0].PostAsJsonAsync(prefix + "/start", new { operationId = "play-start", expectedRevision = count + 1 });
            Equal(HttpStatusCode.OK, start.StatusCode);
            var profiles = fixture.Services.GetRequiredService<CharacterProfileStore>();
            var ownProfile = (await profiles.ReadAsync("p1"))!;
            await profiles.WriteLabelAsync("p1", new(ownProfile.ProfileId, "U okna"));
            var labeledView = await clients[0].GetFromJsonAsync<JsonElement>(prefix + "/play");
            Check(labeledView.GetProperty("players").EnumerateArray().Any(player =>
                player.GetProperty("name").GetString() == ownProfile.UserInput.Name + " · U okna"),
                "display label added after roster freeze appears in the live case view");
            await profiles.WriteLabelAsync("p1", new(ownProfile.ProfileId, null, "U okna"));
            var introduction = await clients[0].GetFromJsonAsync<JsonElement>(prefix + "/play");
            Check(introduction.GetProperty("players").EnumerateArray().All(player =>
                !player.GetProperty("name").GetString()!.Contains("U okna", StringComparison.Ordinal)),
                "clearing a label updates the case view without replacing its frozen character");
            Equal("introduction", introduction.GetProperty("phase").GetString());
            Check(!introduction.TryGetProperty("revision", out _), "no global case revision in play projection");
            var schedule = SealedCaseRules.Schedule(variant, count);
            fixture.AdvanceSeconds(schedule.IntroductionSeconds);
            for (var chapter = 1; chapter <= schedule.Chapters; chapter++)
            {
                for (var index = 0; index < count; index++)
                {
                    var view = await clients[index].GetFromJsonAsync<JsonElement>(prefix + "/play");
                    if (variant == "first" && chapter == 1 && index == 0)
                    { await AiBoundariesAsync(view.Deserialize<SealedPlayView>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!); }
                    Equal(chapter, view.GetProperty("chapter").GetInt32()); Equal("planning", view.GetProperty("phase").GetString());
                    Equal(3, view.GetProperty("choices").GetArrayLength());
                    Check(!view.TryGetProperty("serverSeed", out _) && !view.TryGetProperty("presentation", out _)
                        && HasExactProperties(view.GetProperty("scenario"), "id", "version", "title", "summary", "universe", "tone",
                            "guardianLabel", "conspiratorLabel", "echoLabel", "echoExplanation", "extremeActionLabel", "extremeConsequence"),
                        "scenario exposes only an allowlisted presentation, never the secret graph/seed or frozen pack");
                    var choice = view.GetProperty("choices")[0].GetProperty("plan");
                    if (chapter == 2 && index == 0)
                    { choice = JsonSerializer.SerializeToElement(new SealedPlanConfirmation(chapter, 0, true, SealedIntent.Stun, "p2", Method: "IMPACT_SEAL"), new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
                    foreach (var outcome in view.GetProperty("ownOutcomes").EnumerateArray())
                    {
                        Check(outcome.GetProperty("rolls").ValueKind == JsonValueKind.Null && outcome.GetProperty("modifiers").ValueKind == JsonValueKind.Null
                            && outcome.GetProperty("reason").ValueKind == JsonValueKind.Null && !outcome.GetProperty("traceCreated").GetBoolean(), "pre-finale own outcome cannot reveal hidden contest details");
                    }
                    var operationId = $"play-plan-{chapter}-{index}";
                    var confirmed = await clients[index].PostAsJsonAsync(prefix + "/actions", new { operationId, plan = choice });
                    Equal(HttpStatusCode.OK, confirmed.StatusCode);
                    var ack = await confirmed.Content.ReadFromJsonAsync<JsonElement>(); Check(!ack.TryGetProperty("revision", out _), "private action acknowledgment omits case revision");
                    var replay = await clients[index].PostAsJsonAsync(prefix + "/actions", new { operationId, plan = choice });
                    Equal(HttpStatusCode.OK, replay.StatusCode); Equal(true, (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("replayed").GetBoolean());
                }
                var privateMessage = "private-only-" + chapter;
                Equal(HttpStatusCode.OK, (await clients[0].PostAsJsonAsync(prefix + "/statements", new { operationId = "message-" + chapter, body = privateMessage, recipients = new[] { "p2" }, @public = false })).StatusCode);
                var outsider = await clients[2].GetStringAsync(prefix + "/play"); Check(!outsider.Contains(privateMessage, StringComparison.Ordinal), "third party cannot read a private message");
                var recipient = await clients[1].GetFromJsonAsync<JsonElement>(prefix + "/play");
                Equal(0, recipient.GetProperty("statements").EnumerateArray().Last().GetProperty("recipients").GetArrayLength());
                if (variant == "first" && chapter == 1)
                {
                    for (var flood = 0; flood < 101; flood++)
                    {
                        Equal(HttpStatusCode.OK, (await clients[new[] { 2, 4, 5, 6, 7 }[flood % 5]].PostAsJsonAsync(prefix + "/statements", new
                        { operationId = "private-flood-" + flood, body = "foreign-private-" + flood, recipients = new[] { "p4" }, @public = false })).StatusCode);
                    }
                    var preserved = await clients[0].GetStringAsync(prefix + "/play");
                    Check(preserved.Contains(privateMessage, StringComparison.Ordinal) && !preserved.Contains("foreign-private-", StringComparison.Ordinal), "foreign private flood cannot evict authorized conversation");
                }
                var context = await clients[0].GetStringAsync(prefix + "/context"); Check(!context.Contains("server_seed", StringComparison.OrdinalIgnoreCase), "context does not expose private truth");
                fixture.AdvanceSeconds(schedule.PlanningSeconds);
                var quiet = await clients[0].GetFromJsonAsync<JsonElement>(prefix + "/play"); Equal("quiet", quiet.GetProperty("phase").GetString());
                Equal(HttpStatusCode.Conflict, (await clients[0].PostAsJsonAsync(prefix + "/actions", new
                { operationId = "late-plan-" + chapter, plan = new SealedPlanConfirmation(chapter, 1, true, SealedIntent.Hold) })).StatusCode);
                fixture.AdvanceSeconds(schedule.QuietSeconds);
            }
            var final = await clients[0].GetFromJsonAsync<JsonElement>(prefix + "/play");
            Equal("finale", final.GetProperty("phase").GetString());
            var unreleasedRewards = await clients[0].GetFromJsonAsync<JsonElement>("/app/v1/sealed-cases/rewards");
            Equal(0, unreleasedRewards.GetProperty("items").GetArrayLength());
            Check(final.GetProperty("finale").GetProperty("conspirators").ValueKind == JsonValueKind.Null, "truth remains withheld before final ballot closes");
            await Task.WhenAll(clients.Select(async client =>
            {
                var simultaneous = await client.GetAsync(prefix + "/play");
                var failureCode = simultaneous.IsSuccessStatusCode ? "" : await FinalViewFailureCodeAsync(simultaneous);
                Check(simultaneous.IsSuccessStatusCode, "concurrent final view HTTP " + (int)simultaneous.StatusCode + " " + failureCode);
                var response = await client.PostAsJsonAsync(prefix + "/ballots", new { operationId = "final-abstain", round = 1, nominees = Array.Empty<string>() });
                Check(response.IsSuccessStatusCode, "concurrent ballot HTTP " + (int)response.StatusCode + ": " + await response.Content.ReadAsStringAsync());
            }));
            fixture.AdvanceSeconds(schedule.FinaleSeconds * 2 / 3);
            var runoff = await clients[0].GetFromJsonAsync<JsonElement>(prefix + "/play"); Equal("runoff", runoff.GetProperty("finale").GetProperty("phase").GetString());
            foreach (var client in clients)
            {
                Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(prefix + "/ballots", new { operationId = "runoff-abstain", round = 2, nominees = Array.Empty<string>() })).StatusCode);
            }
            fixture.AdvanceSeconds(schedule.FinaleSeconds - schedule.FinaleSeconds * 2 / 3);
            var released = await clients[0].GetFromJsonAsync<JsonElement>(prefix + "/play");
            Equal("released", released.GetProperty("finale").GetProperty("phase").GetString());
            Equal(schedule.Chapters * count, released.GetProperty("finale").GetProperty("reconstruction").GetArrayLength());
            Equal(SealedCaseRules.Conspirators(count), released.GetProperty("finale").GetProperty("conspirators").GetArrayLength());
            var rewards = await clients[0].GetFromJsonAsync<JsonElement>("/app/v1/sealed-cases/rewards");
            Check(HasExactProperties(rewards, "schemaVersion", "items")
                && rewards.GetProperty("schemaVersion").GetInt32() == 1
                && rewards.GetProperty("items").GetArrayLength() == 1,
                "personal reward collection has a stable, bounded schema and includes the released case");
            var reward = rewards.GetProperty("items")[0];
            var ownOutcomeRows = released.GetProperty("ownOutcomes").EnumerateArray().ToArray();
            var ownPlayerId = ownOutcomeRows[0].GetProperty("actor").GetString()!;
            var confirmedPlanChapters = ownOutcomeRows.Select(outcome => outcome.GetProperty("chapter").GetInt32()).ToHashSet();
            var expectedSuccessfulPlans = released.GetProperty("finale").GetProperty("reconstruction")
                .EnumerateArray()
                .Where(outcome => outcome.GetProperty("actor").GetString() == ownPlayerId
                    && outcome.GetProperty("result").GetString() == nameof(SealedResult.Success)
                    && confirmedPlanChapters.Contains(outcome.GetProperty("chapter").GetInt32()))
                .Select(outcome => outcome.GetProperty("chapter").GetInt32())
                .Distinct()
                .Count();
            Check(HasExactProperties(reward, "caseId", "releasedAt", "team", "confirmedPlans", "successfulPlans")
                && reward.GetProperty("caseId").GetString() == id
                && DateTimeOffset.TryParse(reward.GetProperty("releasedAt").GetString(), out _)
                && reward.GetProperty("team").GetString() is "Guardian" or "Conspirator"
                && reward.GetProperty("confirmedPlans").GetInt32() == confirmedPlanChapters.Count
                && reward.GetProperty("successfulPlans").GetInt32() == expectedSuccessfulPlans,
                "reward counts derive from this participant's confirmed plans and released reconstruction");
            Check(!rewards.GetRawText().Contains("reconstruction", StringComparison.OrdinalIgnoreCase)
                && !rewards.GetRawText().Contains("p2", StringComparison.Ordinal),
                "personal reward projection omits reconstructed actions and other player identities");
            var reloadedRewards = await clients[0].GetFromJsonAsync<JsonElement>("/test/reopened-sealed-case-rewards");
            Equal(rewards.GetRawText(), reloadedRewards.GetRawText());
            if (variant == "first")
            {
                fixture.AddUnreleasedRewardCandidates(id, 101);
                var olderReleasedReward = await clients[0].GetFromJsonAsync<JsonElement>("/app/v1/sealed-cases/rewards");
                Check(olderReleasedReward.GetProperty("items").GetArrayLength() == 1
                    && olderReleasedReward.GetProperty("items")[0].GetProperty("caseId").GetString() == id,
                    "more than 100 newer unreleased cases cannot hide a completed keepsake");
            }
            using (var bystander = fixture.Client())
            {
                await fixture.LoginAsync(bystander, "p31");
                var bystanderRewards = await bystander.GetFromJsonAsync<JsonElement>("/app/v1/sealed-cases/rewards");
                Equal(0, bystanderRewards.GetProperty("items").GetArrayLength());
            }
            using (var foreign = fixture.Client())
            {
                await fixture.LoginAsync(foreign, "foreign");
                var foreignRewards = await foreign.GetFromJsonAsync<JsonElement>("/app/v1/sealed-cases/rewards");
                Equal(0, foreignRewards.GetProperty("items").GetArrayLength());
            }
            Check(!released.GetRawText().Contains("private-only-", StringComparison.Ordinal) || released.GetProperty("statements").GetArrayLength() > 0, "private chat is not reconstruction data");
            Check(!released.GetProperty("finale").GetRawText().Contains("private-only-", StringComparison.Ordinal), "no automatic private chat release");
            Console.WriteLine($"PASS {variant}: {count} HTTPS clients, {schedule.Chapters} chapters, runoff and final reconstruction");
        }
        finally { foreach (var client in clients) { client.Dispose(); } }
    }

    private static async Task<string> FinalViewFailureCodeAsync(HttpResponseMessage response)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            var code = problem.ValueKind == JsonValueKind.Object && problem.TryGetProperty("code", out var field) && field.ValueKind == JsonValueKind.String
                ? field.GetString() : null;
            return code is { Length: > 0 and <= 80 } && code.All(character => character is >= 'a' and <= 'z' or '_')
                ? code : "unknown_error_code";
        }
        catch (JsonException) { return "non_json_error"; }
    }
}
