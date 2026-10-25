using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;
using Microsoft.Extensions.Configuration;

internal static class CaseVoiceAsideTests
{
    internal static async Task RunAsync()
    {
        var prompt = LivePromptSnapshot.Create("dv01", new GptLiveOptions());
        Assert(CaseVoiceAsideEligibility.IsEligible(true, true, prompt)
            && !CaseVoiceAsideEligibility.IsEligible(false, true, prompt)
            && !CaseVoiceAsideEligibility.IsEligible(true, false, prompt)
            && !CaseVoiceAsideEligibility.IsEligible(true, true, prompt with { Version = "legacy" }),
            "only a production DV01 opening can assign an aside; lab and follow-up turns are excluded");
        var asidePrompt = prompt.WithOpeningAside("one-time case aside");
        Assert(asidePrompt.Instructions.Contains("one-time case aside", StringComparison.Ordinal)
            && asidePrompt.Sha256 != prompt.Sha256, "scheduled opening aside belongs to the trusted prompt snapshot");
        var recoveryPrompt = asidePrompt.WithoutOpeningAside();
        Assert(!recoveryPrompt.Instructions.Contains("one-time case aside", StringComparison.Ordinal)
            && recoveryPrompt.Sha256 == prompt.Sha256 && recoveryPrompt.WithoutOpeningAside() == recoveryPrompt,
            "provider recovery strips the aside and cannot repeat it");

        var directory = Path.Combine(Path.GetTempPath(), $"kufr-case-asides-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:DatabasePath"] = Path.Combine(directory, "journal.sqlite"),
                ["Backend:Usage:SessionCapMicroUsd"] = "500000",
                ["Backend:Usage:DailyCapMicroUsd"] = "10000000",
                ["Backend:Usage:MonthlyCapMicroUsd"] = "10000000"
            }).Build();
            using var journal = new OperationJournal(configuration, TimeProvider.System);
            var memory = new EveningMemoryStore(configuration, journal);
            var secondProcessView = new EveningMemoryStore(configuration, journal);
            var conversations = new ConversationStore(TimeProvider.System, journal, eveningMemory: memory);
            const string admittedDevice = "case-aside-admitted";
            var admittedEvening = memory.GetStatus(admittedDevice).EveningId;
            var createKey = Guid.NewGuid();
            var createRequest = new CreateConversationRequest(admittedDevice, Guid.NewGuid(), 0,
                PartyWandCatalog.Version, "cs-CZ", new(true, false));
            var created = conversations.Create(admittedDevice, createKey, "aside-create"u8, createRequest);
            Assert(created.Status == 201 && created.Response is not null,
                "ordinary conversation admission should succeed without manually capturing a visit");
            var admittedId = created.Response!.ConversationId;
            Assert(memory.ReadCapturedShowEvening(admittedDevice, admittedId) == admittedEvening,
                "ordinary Create captures its evening before voice opening can assign an aside");
            var admittedAside = memory.AssignCaseVoiceAside(admittedDevice, admittedId, _ => 0);
            var replayed = conversations.Create(admittedDevice, createKey, "aside-create"u8, createRequest);
            Assert(replayed.Response?.ConversationId == admittedId && admittedAside?.Selected == true
                && secondProcessView.AssignCaseVoiceAside(admittedDevice, admittedId,
                    _ => throw new Exception("replayed creation rerolled")) == admittedAside,
                "replayed creation and a recreated store retain one captured session and assignment");
            const string device = "case-aside-device";
            var firstEvening = memory.GetStatus(device).EveningId;
            var rolls = 0;
            var selected = new List<int>();
            var denominators = new List<int>();
            for (var session = 1; session <= 20; session++)
            {
                var conversation = $"aside-session-{session}";
                Assert(memory.CaptureShowVisit(device, conversation) == firstEvening,
                    "show visit should capture the current evening");
                var result = memory.AssignCaseVoiceAside(device, conversation, denominator =>
                {
                    rolls++;
                    denominators.Add(denominator);
                    return denominator - 1;
                });
                Assert(result is not null && result.SessionNumber == session, "session receipt should be ordered");
                if (result!.Selected) { selected.Add(session); }
                Assert(result.Selected == (session is 10 or 20), "a no-hit sequence should schedule by the tenth session");
            }
            Assert(selected.SequenceEqual([10, 20]), "two asides should be guaranteed in every 20 no-hit sessions");
            Assert(denominators.Take(10).SequenceEqual(Enumerable.Range(1, 10).Reverse()),
                "misses should increase the selection hazard until a guaranteed selection");
            Assert(rolls == 20, "every eligible opening should draw exactly once");

            var duplicate = memory.AssignCaseVoiceAside(device, "aside-session-20", _ => throw new Exception("duplicate rerolled"));
            var afterRestart = secondProcessView.AssignCaseVoiceAside(device, "aside-session-20", _ => throw new Exception("restart rerolled"));
            Assert(duplicate == afterRestart && duplicate?.Selected == true,
                "the assignment receipt should remain stable across retries and store recreation");
            Assert(memory.CaseVoiceAsideCadence(device, firstEvening) == (0, 20, 2),
                "selected asides reset misses without losing the session count");
            Assert(memory.AssignCaseVoiceAside(device, "uncaptured-session") is null,
                "uncaptured conversations must not affect cadence");

            const string concurrentDevice = "case-aside-concurrent";
            var concurrentEvening = memory.GetStatus(concurrentDevice).EveningId;
            var stores = Enumerable.Range(0, 4).Select(_ => new EveningMemoryStore(configuration, journal)).ToArray();
            var assignments = Enumerable.Range(1, 16).Select(index =>
            {
                var conversation = $"parallel-{index}";
                memory.CaptureShowVisit(concurrentDevice, conversation);
                return Task.Run(() => stores[index % stores.Length].AssignCaseVoiceAside(
                    concurrentDevice, conversation, denominator => denominator - 1));
            });
            var concurrentResults = await Task.WhenAll(assignments);
            Assert(concurrentResults.All(result => result is not null)
                && concurrentResults.Select(result => result!.SessionNumber).Order().SequenceEqual(Enumerable.Range(1, 16)),
                "concurrent sessions should receive unique atomic cadence positions");
            Assert(memory.CaseVoiceAsideCadence(concurrentDevice, concurrentEvening).TotalSessions == 16,
                "concurrent writes should all be durable");
            memory.EndShowEvening(concurrentDevice, concurrentEvening);
            Assert(memory.CaseVoiceAsideCadence(concurrentDevice, concurrentEvening) == (0, 0, 0)
                && memory.AssignCaseVoiceAside(concurrentDevice, "parallel-1") is null,
                "closing an evening purges its cadence and prevents delayed replay from reassigning");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
