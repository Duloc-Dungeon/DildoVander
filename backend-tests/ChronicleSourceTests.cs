using Kufr.Backend.Features.Chronicle;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.Launch;
using Kufr.Backend.Features.Wands;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using SkiaSharp;

internal static class ChronicleSourceTests
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "chronicle-sources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            WandCompletion(root);
            ConversationMoments(root);
            EveningFreeze(root);
            CardAndBadge(root);
            ProseImagePrivacy();
            Console.WriteLine("Chronicle source/renderer checks passed; synthetic local data only.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void WandCompletion(string root)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Backend:DatabasePath"] = Path.Combine(root, "wands.sqlite") }).Build();
        var clock = new Clock();
        var store = new WandGenerationStore(config, clock);
        var chronicle = new ChronicleStore(root);
        var profile = Profile();
        store.Begin("owner", "wand-one", profile, 1, true);
        var job = store.Claim("owner", "wand-one", false)!;
        store.Complete(job, [Candidate("Saze v kapse")]);
        Check(chronicle.List("owner").Items.Count == 0, "Prepared candidates must not create a completed wand memory.");
        var wand = store.Select("owner", "wand-one", profile.ProfileId, profile.Revision, 1);
        var restarted = new WandGenerationStore(config, clock);
        Check(restarted.DrainWandChronicle(chronicle) == 1, "Committed selection survives source-store restart.");
        var view = chronicle.FindSource("owner", "wand", wand.WandId)!;
        Check(view is { Revealed: false } && view.Summary.Length == 0, "Undisclosed wand keeps its story hidden.");
        chronicle.Reveal("owner", view.Id);
        var revealed = chronicle.Read("owner", view.Id)!;
        Check(revealed.OccurredAt == clock.GetUtcNow().ToUnixTimeSeconds() && revealed.Participants.Single().WandName == wand.Name,
            "Chronicle retains the confirmed selection time and selected wand.");
        Check(revealed.Participants.Single().VisualReferenceVersion.Contains(wand.WandId), "Frozen visual provenance identifies the selected wand.");
        clock.Advance();
        store.Select("owner", "wand-one", profile.ProfileId, profile.Revision, 1);
        restarted.DrainWandChronicle(chronicle);
        Check(chronicle.List("owner").Items.Count == 1 && chronicle.Read("owner", view.Id)!.OccurredAt == revealed.OccurredAt,
            "Selection replay cannot duplicate or re-date the keepsake.");

        store.BeginWorkshop("owner", "upgrade", profile, 2, "group", 1);
        store.Begin("owner", "upgrade", profile, 1, true);
        var upgrade = store.Claim("owner", "upgrade", false)!;
        store.Complete(upgrade, [Candidate("Druhá směna")]);
        var upgraded = store.Select("owner", "upgrade", profile.ProfileId, profile.Revision, 1);
        store.DrainWandChronicle(chronicle);
        var upgradeView = chronicle.FindSource("owner", "wand", upgraded.WandId)!;
        chronicle.Reveal("owner", upgradeView.Id);
        var detail = chronicle.Read("owner", upgradeView.Id)!;
        Check(detail.Awards.Single().Id == "second_edition" && detail.Story.Any(text => text.Contains(wand.Name)),
            "A completed upgrade links its badge and the previous wand; unlocking alone does not.");

        chronicle.DeleteAccount("owner");
        store.DeleteAccount("owner");
        restarted.DrainWandChronicle(chronicle);
        Check(chronicle.FindSource("owner", "wand", wand.WandId) is null, "Source replay cannot restore erased account scenes.");
    }

    private static void EveningFreeze(string root)
    {
        var launch = new LaunchPolicyStore(root);
        var group = launch.CreateGroup("admin", new("Testovací večer", null), 1000);
        LaunchTestSchedule.Set(launch, group.Id, 1000, 1000);
        group = launch.ReadGroup(group.Id)!;
        var input = new ChronicleEventInput("evening", group.Id + ":guest", group.Id, 1234,
            "Závěr večera", "Potvrzené vzpomínky", [new("guest", "Host", "owner")], Story: ["Doložená událost"], Visibility: "public");
        ChronicleOutboxEnvelope envelope = new(input, ["guest"], new Dictionary<string, IReadOnlyList<ChronicleAward>>());
        Check(launch.FreezeEveningChronicle(group.Id, group.Revision, "admin", 1234, [envelope]) == 1234, "Explicit close journals personal memories.");
        Check(launch.FreezeEveningChronicle(group.Id, 0, "admin", 5000, []) == 1234, "Close replay retains the original cutoff.");
        var destination = new ChronicleStore(root);
        Check(launch.DrainEveningChronicle(destination) == 1 && launch.ReadChronicleClosure(group.Id) == 1234, "Evening delivery survives durable closure.");
        Check(launch.DrainEveningChronicle(destination) == 0, "Delivered closure does not issue twice.");
    }

    private static void ConversationMoments(string root)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Backend:DatabasePath"] = Path.Combine(root, "moments.sqlite") }).Build();
        var store = new WandGenerationStore(config, TimeProvider.System);
        var destination = new ChronicleStore(Path.Combine(root, "moments"));
        const string operationId = "same-client-operation";
        const string userMessage = "11111111111141118111111111111111";
        const string assistantMessage = "22222222222242228222222222222222";
        var sourceIds = new List<string>();
        foreach (var owner in new[] { "first-owner", "second-owner" })
        {
            var profile = Profile();
            store.BeginWorkshop(owner, operationId, profile, 1, "group", 1);
            store.AppendWorkshopMessage(owner, operationId, userMessage, "user", "Soukromá otázka.", "text");
            store.AppendWorkshopMessage(owner, operationId, assistantMessage, "assistant", "Vybraná odpověď pro " + owner, "text");
            store.FinishWorkshopConversation(owner, operationId, profile.ProfileId, profile.Revision);
            store.CompleteWorkshopWithoutArtwork(owner, operationId, "synthetic-fixture");
            sourceIds.Add(store.SaveWorkshopMoment(owner, operationId, assistantMessage, 1));
        }
        Check(sourceIds.Distinct().Count() == 2, "Client operation/message identifiers are scoped to their owner in moment hashes.");
        Check(store.DrainWandChronicle(destination) == 2, "Both account-scoped selected moments survive outbox delivery.");
        for (var index = 0; index < sourceIds.Count; index++)
        {
            var owner = index == 0 ? "first-owner" : "second-owner";
            var item = destination.FindSource(owner, "conversation", sourceIds[index])!;
            destination.Reveal(owner, item.Id);
            item = destination.Read(owner, item.Id)!;
            Check(item.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds()
                && item.OccurredAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "Conversation timestamps are normalized from stored milliseconds to event seconds.");
            Check(item.Story.Count == 1 && item.Story[0] == "Vybraná odpověď pro " + owner && !item.ShareAllowed,
                "Only the selected reply is preserved and private conversation cannot be shared.");
        }
    }

    private static void CardAndBadge(string root)
    {
        var input = new ChronicleEventInput("yolo", "test-fiasco", "group", 1, "Konvice na útěku",
            "Obě postavy skončily v sazích.", [new("a", "Kazimír", "participant"), new("b", "Berta", "participant")],
            Result: "fiasco", Story: ["Plán", "Komplikace", "Následek"], ShareText: "Konvice zůstala doma.");
        var prompt = ChronicleIllustrator.BuildPrompt(input);
        Check(prompt.Contains("fiasko") && prompt.Contains("Konvice") && !prompt.Contains("OPENAI_API_KEY"), "Image prose preserves the confirmed fiasco.");
        var fiascoWithParticipant = ChronicleIllustrator.BuildPrompt(input with { WinnerId = "a" });
        Check(fiascoWithParticipant.Contains("společné fiasko") && !fiascoWithParticipant.Contains("Potvrzený vítěz"),
            "a supplied participant identifier cannot turn the confirmed fiasco into a victory");
        var bytes = ChronicleIllustrator.RenderCard(input, null, true);
        using var card = SKBitmap.Decode(bytes);
        Check(card.Width == 1024 && card.Height == 1408, "Shareable card is an actual decoded PNG.");
        var badge = ChronicleBadgeRenderer.Render(new("image/png", bytes), "fiasco_pair");
        using var decoded = SKBitmap.Decode(badge);
        Check(decoded.Width == 384 && decoded.Height == 432 && decoded.GetPixel(0, 0).Alpha == 0,
            "Personal badge renders its scene in a transparent shared frame.");
    }

    private static void ProseImagePrivacy()
    {
        var visual = """
            {"name":"Raven","universe":"Original fiction","traits":["Stříbrný plášť"],
             "profileId":"private-profile-id","portraitReferenceBase64":"private-base64-bytes",
             "portraitSha256":"private-portrait-hash","sourceUrl":"https://private.example/source",
             "hiddenOutcome":"private-hidden-outcome","privatePlan":"private-plan",
             "wand":{"wandId":"private-wand-id","name":"Jiskra","material":"Jasan","core":"Uhlík",
                     "designDescription":"Štíhlá hůlka s měděnou rukojetí","accountId":"private-nested-account"}}
            """;
        var person = new ChronicleParticipant("private-account-id", "Kazimír", "team-b", "Raven", "Jiskra", visual,
            "private-visual-provenance");
        var input = new ChronicleEventInput("team", "private-source-id", "private-group-id", 1, "Útěk konvice",
            "Postava zastavila rozběhnutou konvici.", [person], Result: "b", WinnerId: person.AccountId,
            Story: ["Konvice převrátila stolek."], Decisions: [new(person.AccountId, "Zachytit konvici", "Měděná rukojeť zazářila", "private-decision-proof")],
            DecisiveChoice: "Zachytit konvici", VisualMotifs: ["Konvice a měděná rukojeť"]);
        var prompt = ChronicleIllustrator.BuildPrompt(input);
        Check(prompt.Contains("Vítězný tým: Kazimír", StringComparison.Ordinal)
            && prompt.Contains("Stříbrný plášť", StringComparison.Ordinal)
            && prompt.Contains("Štíhlá hůlka s měděnou rukojetí", StringComparison.Ordinal)
            && prompt.Contains("Měděná rukojeť zazářila", StringComparison.Ordinal)
            && !prompt.Contains('{') && !prompt.Contains('}') && !prompt.Contains("\"participants\"", StringComparison.Ordinal),
            "chronicle image prompt describes the confirmed scene, named winner and frozen visual facts as prose");
        foreach (var excluded in new[] { "private-account-id", "private-profile-id", "private-base64-bytes", "private-portrait-hash",
            "private-hidden-outcome", "private-plan", "private-wand-id", "private-nested-account", "private-visual-provenance",
            "private-source-id", "private-group-id", "private-decision-proof", "private.example" })
        {
            Check(!prompt.Contains(excluded, StringComparison.Ordinal), "chronicle prompt excludes identifiers, media bytes, source URLs and private hidden fields");
        }
    }

    private static CharacterConversationSnapshot Profile() => new("profile", 1, new("Kazimír", "Vlastní svět"),
        new("Kazimír", "Vlastní svět", "Fiktivní kominík", [], [], [], false, [], "fixture", "fixture", 1, 1, "synthetic"), 1);
    private static WandCandidateContent Candidate(string name) => new(name, "Jasan", "Uhlík", "Zvídavá",
        "Hůlka doprovází kominíka do práce.", "Štíhlá jasanová hůlka s měděnou rukojetí.", "Saze", "Déšť",
        new[] { "strike", "trick", "shield" }.Select(action => new WandSpell(action, "Kouzlo " + action, "Doložený herní účinek")).ToArray());
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1000);
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance() => now = now.AddHours(1);
    }
}
