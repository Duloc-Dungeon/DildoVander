using System.Text.Json;
using Kufr.Backend.Features.SealedCase;

internal static class SealedCaseProjectionTests
{
    internal static void Run()
    {
        Hidden_provenance_does_not_disclose_causality_or_execution();
        Explicit_result_disclosure_is_scoped_and_independent_of_provenance();
        Result_disclosure_authority_is_snapshotted();
        Live_experience_does_not_export_world_join_keys();
        Party_identity_and_role_are_checked_before_projection();
        Epilogue_release_does_not_change_unreleased_order_status();
        Private_narrator_receives_only_accepted_own_plans();
        Returned_audiences_cannot_mutate_later_projections();
        Publication_and_observation_are_not_serialized_as_private_delivery();
        Hidden_changes_do_not_change_live_frames_targets_counts_errors_or_notifications();
        Released_refs_and_private_chats_stay_scoped();
        Console.WriteLine("SealedCase projection isolation checks passed.");
    }

    private static void Hidden_provenance_does_not_disclose_causality_or_execution()
    {
        foreach (var role in new[] { SealedContextRole.Player, SealedContextRole.ChoiceGenerator, SealedContextRole.PrivateNarrator })
        {
            string? baseline = null;
            foreach (var ownEventPresent in new[] { true, false })
            foreach (var source in new string?[] { "own-world", "other-world", "missing-world", null })
            {
                var world = new SealedWorldLog();
                if (ownEventPresent) world.Append(new("own-world", 2, "A", "B", "STUN", "SUCCESS", new Dictionary<string,string>(), "hidden-roll"));
                world.Append(new("other-world", 2, "C", "B", "STUN", "SUCCESS", new Dictionary<string,string>(), "hidden-roll"));
                var service = new SealedContextService("case", "group", world,
                    [new("seen", "A", source, "U dveří vidíš záblesk.", "OBSERVED")], [],
                    [new("own-order", "A", "STUN B", "B", true, ["own-world"])],
                    [new("account", "A", [role])], new Dictionary<string,string[]> { ["A"] = ["B"] });
                var frame = service.Project("case", "group", "account", role);
                var json = JsonSerializer.Serialize(frame);
                baseline ??= json;
                Check(json == baseline, "whole live frame cannot reveal internal provenance or unacknowledged world execution: " + role);
            }
        }
    }

    private static void Explicit_result_disclosure_is_scoped_and_independent_of_provenance()
    {
        var exports = new List<JsonElement>();
        foreach (var role in new[] { SealedContextRole.Player, SealedContextRole.ChoiceGenerator, SealedContextRole.PrivateNarrator })
        {
            string? baseline = null;
            foreach (var source in new string?[] { "own-world", "other-world", null })
            foreach (var ownEventPresent in new[] { true, false })
            {
                var world = new SealedWorldLog();
                if (ownEventPresent) world.Append(new("own-world", 2, "A", "B", "STUN", "SUCCESS", new Dictionary<string,string>(), "secret"));
                SealedCovertOrder[] orders = [
                    new("authorized", "A", "STUN", "B", true, ["own-world"]) { AuthorizedResultRefs = ["seen", "other-observer", "missing", "seen"] },
                    new("unacknowledged", "A", "STUN", "B", true, ["own-world"]),
                    new("acknowledged", "A", "STUN", "B", true, ["own-world"]) { ExecutionAcknowledged = true },
                    new("invalid-result-refs", "A", "STUN", "B", true, ["own-world"]) { AuthorizedResultRefs = ["other-observer", "missing"] },
                    new("draft", "A", "STUN", "B", false, ["own-world"]) { AuthorizedResultRefs = ["seen"], ExecutionAcknowledged = true },
                    new("foreign", "B", "STUN", "A", true, ["own-world"]) { AuthorizedResultRefs = ["seen"], ExecutionAcknowledged = true }];
                var service = new SealedContextService("case", "group", world,
                    [new("seen", "A", source, "Vidíš výsledek svého pokusu.", "OBSERVED"),
                     new("unlinked", "A", "own-world", "Záblesk.", "OBSERVED"),
                     new("other-observer", "B", source, "Soukromý prožitek.", "OBSERVED")], [], orders,
                    [new("account", "A", [role])], new Dictionary<string,string[]>());
                var frame = service.Project("case", "group", "account", role);
                exports.Add(frame.ToSchema());
                var json = JsonSerializer.Serialize(frame);
                baseline ??= json;
                Check(json == baseline, "explicit causal disclosure cannot vary with hidden provenance/execution: " + role);
                var actions = frame.Context.CovertActions.ToDictionary(x => x.ActionRef);
                Check(actions["authorized"].PerceivedResultRefs.SequenceEqual(["seen"]) && actions["authorized"].ExecutionStatus == "RESOLVED", "explicit authorized result survives, foreign/missing/duplicate experiences do not");
                Check(actions["unacknowledged"].ExecutionStatus == "PLANNED" && actions["unacknowledged"].PerceivedResultRefs.Count == 0, "matching source alone grants nothing");
                Check(actions["acknowledged"].ExecutionStatus == "RESOLVED_RESULT_NOT_OBSERVED" && actions["acknowledged"].PerceivedResultRefs.Count == 0, "own execution receipt does not acknowledge an observed result");
                Check(actions["invalid-result-refs"].ExecutionStatus == "PLANNED", "invalid result grants cannot disclose execution");
                Check(!actions.ContainsKey("foreign"), "other actor's disclosure is not inherited");
                if (role != SealedContextRole.PrivateNarrator)
                    Check(actions["draft"].ExecutionStatus == "PLANNED" && actions["draft"].PerceivedResultRefs.Count == 0, "draft has no execution/result disclosure");
            }
        }
        var path = Environment.GetEnvironmentVariable("SEALED_DISCLOSURE_EXPORT");
        if (path is not null) File.WriteAllText(path, JsonSerializer.Serialize(exports));
    }

    private static void Result_disclosure_authority_is_snapshotted()
    {
        string[] refs = ["seen"];
        var service = new SealedContextService("case", "group", new(),
            [new("seen", "A", null, "Vidíš výsledek svého pokusu.", "OBSERVED")], [],
            [new("action", "A", "STUN", "B", true, []) { AuthorizedResultRefs = refs }],
            [new("account", "A", [SealedContextRole.Player])], new Dictionary<string,string[]>());
        var first = service.Project("case", "group", "account", SealedContextRole.Player);
        refs[0] = "missing";
        ((string[])first.Context.CovertActions.Single().PerceivedResultRefs)[0] = "changed-by-client";
        var second = service.Project("case", "group", "account", SealedContextRole.Player);
        Check(second.Context.CovertActions.Single().PerceivedResultRefs.SequenceEqual(["seen"]), "source or returned-list mutation cannot alter captured result disclosure authority");
    }

    private static void Live_experience_does_not_export_world_join_keys()
    {
        var log = new SealedWorldLog();
        log.Append(new("hidden-source", 2, "B", "A", "STUN", "SUCCESS", new Dictionary<string,string>(), "secret-die"));
        var view = SealedProjection.ForPlayer("A", log, [new("seen", "A", "hidden-source", "Záblesk.", "OBSERVED")], [], [], false);
        Check(!JsonSerializer.Serialize(view).Contains("hidden-source", StringComparison.Ordinal), "live experience must not export hidden world source ids");
    }
    private static void Party_identity_and_role_are_checked_before_projection()
    {
        // API is internal: memberships and role grants are supplied by server authority,
        // never by a client DTO. A host remains a competitor, not a moderator.
        var service = Service();
        var player = service.Project("case", "group", "account-A", SealedContextRole.Player);
        Check(player.Context.PlayerId == "A", "account maps to its server-bound player");
        foreach (var request in new[] {
            ("other", "group", "account-A", SealedContextRole.Player),
            ("case", "other", "account-A", SealedContextRole.Player),
            ("case", "group", "A", SealedContextRole.Player),
            ("case", "group", "account-B", SealedContextRole.PrivateNarrator),
            ("case", "group", "account-A", SealedContextRole.Engine),
            ("case", "group", "account-A", SealedContextRole.Moderator),
            ("case", "group", "account-A", SealedContextRole.Epilogue),
            ("case", "group", "account-A", (SealedContextRole)999) })
        {
            try { service.Project(request.Item1, request.Item2, request.Item3, request.Item4); throw new InvalidOperationException("unexpected access"); }
            catch (SealedCasePersistenceException e) { Check(e.Status == 404 && e.Code == "case_not_found", "uniform denial"); }
        }
        Check(service.Project("case", "group", "narrator", SealedContextRole.PublicNarrator).Context.CovertActions.Count == 0, "public narrator gets no orders");
        Check(service.Project("case", "group", "moderator", SealedContextRole.Moderator).Context.WorldTruth.Access == "ENGINE_ONLY", "explicit noncompeting moderator");
    }

    private static void Epilogue_release_does_not_change_unreleased_order_status()
    {
        var log = new SealedWorldLog();
        log.Append(new("unreleased", 2, "A", "B", "STUN", "SUCCESS", new Dictionary<string,string>(), "secret"));
        var service = new SealedContextService("case", "group", log, [], [],
            [new("own", "A", "STUN", "B", true, ["unreleased"]), new("own", "A", "HOLD", null, true, [])],
            [new("account", "A", [SealedContextRole.Player, SealedContextRole.Epilogue])], new Dictionary<string,string[]>(), true, []);
        var live = service.Project("case", "group", "account", SealedContextRole.Player);
        var ending = service.Project("case", "group", "account", SealedContextRole.Epilogue);
        Check(ending.Context.CovertActions.Select(x => x.ExecutionStatus).SequenceEqual(live.Context.CovertActions.Select(x => x.ExecutionStatus)), "unreleased execution status is not disclosed by finale, even for reused command IDs");
        Check(!ending.ToSchema().ToString().Contains("unreleased", StringComparison.Ordinal), "unreleased references stay private");
    }

    private static void Private_narrator_receives_only_accepted_own_plans()
    {
        var service = new SealedContextService("case", "group", new(), [], [],
            [new("draft", "A", "secret-draft", "B", false, []), new("accepted", "A", "HOLD", null, true, [])],
            [new("account", "A", [SealedContextRole.Player, SealedContextRole.PrivateNarrator])], new Dictionary<string,string[]>());
        Check(service.Project("case", "group", "account", SealedContextRole.Player).Context.CovertActions.Count == 2, "player retains draft");
        var narrator = service.Project("case", "group", "account", SealedContextRole.PrivateNarrator);
        Check(narrator.Context.CovertActions.Count == 1 && narrator.Context.CovertActions.Single().ActionRef == "accepted", "narrator gets accepted own plans only");
    }

    private static void Returned_audiences_cannot_mutate_later_projections()
    {
        var service = new SealedContextService("case", "group", new(), [],
            [new("own", "A", "Výpověď.", SealedAudience.Players(["B"]), ["B"], false)], [],
            [new("account", "A", [SealedContextRole.Player])], new Dictionary<string,string[]>());
        var first = service.Project("case", "group", "account", SealedContextRole.Player);
        ((string[])first.Context.Presentations.Single().Recipients)[0] = "C";
        ((string[])first.Context.Presentations.Single().DeliveryReceipts)[0] = "C";
        var second = service.Project("case", "group", "account", SealedContextRole.Player);
        Check(second.Context.Presentations.Single().Recipients.Single() == "B" && second.Context.Presentations.Single().DeliveryReceipts.Single() == "B", "returned audience mutation must not rewrite canonical presentation");
    }

    private static void Publication_and_observation_are_not_serialized_as_private_delivery()
    {
        var service = new SealedContextService("case", "group", new(), [],
            [new("published", "A", "Jdu spát.", SealedAudience.Public, [], true),
             new("pose", "A", "Vypadá to jako spánek.", SealedAudience.Players(["B"]), ["B"], false, SealedPresentationKind.PhysicalPerformance)], [],
            [new("account", "A", [SealedContextRole.Player])], new Dictionary<string,string[]>());
        var schema = service.Project("case", "group", "account", SealedContextRole.Player).ToSchema();
        var presentations = schema.GetProperty("PRESENTATION").EnumerateArray().ToArray();
        Check(presentations.Single(x => x.GetProperty("id").GetString() == "published").GetProperty("delivery_receipts")
            .EnumerateArray().Any(x => x.GetProperty("status").GetString() == "PUBLISHED"), "publication must not look like an unsent draft");
        Check(presentations.Single(x => x.GetProperty("id").GetString() == "pose").GetProperty("delivery_receipts")[0].GetProperty("status").GetString() == "OBSERVED", "physical observation is not message delivery");
    }

    private static void Hidden_changes_do_not_change_live_frames_targets_counts_errors_or_notifications()
    {
        var exports = new List<JsonElement>();
        var baselines = new Dictionary<SealedContextRole,string>();
        for (var mutation = 0; mutation < 32; mutation++)
        {
            var world = new SealedWorldLog();
            world.Append(new("secret-own-event-" + mutation, 2, "A", "B", "STUN", mutation % 2 == 0 ? "SUCCESS" : "DEFENDED", new Dictionary<string,string> { ["team"] = "secret-team-" + mutation }, "secret-roll-" + mutation));
            for (var i = 0; i < mutation; i++) world.Append(new("secret-event-" + i, 2, "B", "C", "LETHAL", "SUCCESS", new Dictionary<string,string>(), "secret-dice"));
            SealedExperience[] experience = [
                new("own-seen", "A", "secret-own-event-" + mutation, "Vidíš záblesk.", "OBSERVED", 3, "SAW", "APPEARS_SLEEPING"),
                new("received", "A", null, "Jdu spát.", "REPORTED", 2, "RECEIVED_MESSAGE"),
                new("secret-experience", "B", null, "secret-memory-" + mutation, "OBSERVED")];
            var presentations = new List<SealedPresentation> {
                new("public", "B", "Opona zůstává zavřená.", SealedAudience.Public, ["A"], true),
                new("received-message", "B", "Jdu spát.", SealedAudience.Players(["A", "secret-recipient-" + mutation]), ["A"], false),
                new("own-message", "A", "Čekám.", SealedAudience.Players(["B"]), [], false) };
            for (var i = 0; i < mutation; i++) presentations.Add(new("secret-message-" + i, "B", "secret-chat", SealedAudience.Players(["C"]), ["C"], false));
            var orders = new List<SealedCovertOrder> { new("own-order", "A", "STUN", "B", true, ["secret-own-event-" + mutation]) };
            for (var i = 0; i < mutation; i++) orders.Add(new("secret-order-" + i, "B", "LETHAL", "A", true, ["secret-event-" + i]));
            var service = new SealedContextService("case", "group", world, experience, presentations, orders,
                [new("account", "A", [SealedContextRole.Player, SealedContextRole.PrivateNarrator, SealedContextRole.ChoiceGenerator]),
                 new("public-service", null, [SealedContextRole.PublicNarrator])],
                new Dictionary<string,string[]> { ["A"] = ["B"] });
            foreach (var role in new[] { SealedContextRole.Player, SealedContextRole.PrivateNarrator, SealedContextRole.ChoiceGenerator, SealedContextRole.PublicNarrator })
            {
                var frame = service.Project("case", "group", role == SealedContextRole.PublicNarrator ? "public-service" : "account", role);
                var json = JsonSerializer.Serialize(frame);
                Check(!json.Contains("secret-", StringComparison.Ordinal), "no secret sentinel in any live/AI frame");
                if (mutation == 0) { baselines[role] = json; exports.Add(frame.ToSchema()); }
                else Check(baselines[role] == json, "hidden mutation must not change frame shape, content, counts, targets or notifications: " + role);
                Check(frame.Context.WorldTruth.EventRefs.Count == 0, "no truth refs");
                if (role is SealedContextRole.Player or SealedContextRole.ChoiceGenerator) Check(frame.AllowedTargetIds.SequenceEqual(["B"]), "known target persists regardless of hidden state");
                if (role == SealedContextRole.PublicNarrator) Check(frame.Context.Experience.Count == 0 && frame.Context.Presentations.Count == 1, "public only");
            }
        }
        var path = Environment.GetEnvironmentVariable("SEALED_CONTEXT_EXPORT");
        if (path is not null) File.WriteAllText(path, JsonSerializer.Serialize(exports));
    }

    private static void Released_refs_and_private_chats_stay_scoped()
    {
        var world = new SealedWorldLog();
        world.Append(new("released-own", 2, "A", "B", "STUN", "SUCCESS", new Dictionary<string,string>(), "never-export-roll"));
        world.Append(new("unrelated", 2, "B", "C", "LETHAL", "SUCCESS", new Dictionary<string,string>(), "secret"));
        var service = new SealedContextService("case", "group", world,
            [new("seen", "A", "released-own", "Záblesk.", "OBSERVED")],
            [new("others-chat", "B", "never-export-chat", SealedAudience.Players(["C"]), ["C"], false)],
            [new("order", "A", "STUN", "B", true, ["released-own"])],
            [new("account", "A", [SealedContextRole.Epilogue])], new Dictionary<string,string[]>(), true, ["released-own", "unrelated"]);
        var frame = service.Project("case", "group", "account", SealedContextRole.Epilogue);
        Check(frame.Context.WorldTruth.EventRefs.SequenceEqual(["released-own"]), "approved references also restricted to player involvement");
        Check(frame.Context.Experience.Single().SourceEventId == "released-own", "approved experience provenance available at finale");
        Check(frame.Context.CovertActions.Single().WorldEventRefs.SequenceEqual(["released-own"]), "approved own action references");
        Check(!frame.ToSchema().ToString().Contains("never-export", StringComparison.Ordinal), "no private chat or rolls at finale");
    }

    private static SealedContextService Service() => new("case", "group", new SealedWorldLog(), [], [], [],
        [new("account-A", "A", [SealedContextRole.Player, SealedContextRole.PrivateNarrator, SealedContextRole.ChoiceGenerator, SealedContextRole.Moderator]),
         new("account-B", "B", [SealedContextRole.Player]),
         new("narrator", null, [SealedContextRole.PublicNarrator]),
         new("moderator", null, [SealedContextRole.Moderator])],
        new Dictionary<string,string[]> { ["A"] = ["B"] });

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("SealedCase projection: " + message); }
}
