using System.Text.Json;
using Kufr.Backend.Features.Character;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Realtime;
using Microsoft.Extensions.Configuration;

internal static class GptLiveVoiceTests
{
    internal static void Run()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:GptLive:Character:Enabled"] = "false",
            ["Backend:GptLive:Voice:CustomVoiceId"] = "voice_wandmaster_fixture"
        }).Build();
        var options = GptLiveOptions.Read(configuration);
        Require(options.Character.Enabled && options.Character.FinaleProfile == FinaleProfile.Blocked,
            "legacy disabled flag cannot disable DV01 or unlock the finale");
        var snapshot = LivePromptSnapshot.Create("party-original", options);
        Require(snapshot.PersonalityId == PersonalityCatalog.DefaultId && snapshot.Version == "dv01-adult-v1",
            "every executed Live prompt is the canonical DV01 identity");
        var request = new RealtimeTurnRequest(Guid.NewGuid(), Guid.NewGuid(), 0, 63, 1,
            PartyWandCatalog.Version, true, "", null, "coral", 1);
        var turn = new RealtimeTurnContext(request, true, "", []) { LivePrompt = snapshot };
        using var started = JsonDocument.Parse(JsonSerializer.Serialize(GptLiveProtocol.Start(turn)));
        var voice = started.RootElement.GetProperty("session").GetProperty("audio").GetProperty("output").GetProperty("voice");
        Require(voice.GetProperty("id").GetString() == "voice_wandmaster_fixture",
            "custom voice is an ID object in the actual Live startup payload");
        configuration["Backend:GptLive:Voice:CustomVoiceId"] = "voice_replacement_fixture";
        using var frozen = JsonDocument.Parse(JsonSerializer.Serialize(GptLiveProtocol.Start(turn)));
        Require(frozen.RootElement.GetProperty("session").GetProperty("audio").GetProperty("output")
            .GetProperty("voice").GetProperty("id").GetString() == "voice_wandmaster_fixture",
            "configuration changes cannot change an active frozen voice");
        foreach (var id in new[] { "", "bad-id", "voice_", "voice_\nunsafe" })
        {
            configuration["Backend:GptLive:Voice:CustomVoiceId"] = id;
            var fallback = GptLiveOptions.Read(configuration).Voice;
            Require(fallback.CustomVoiceId is null && (string)fallback.SessionVoice == "vesper",
                "missing and invalid voice IDs use the supported built-in voice");
        }
        var statePath = Path.Combine(Path.GetTempPath(), "workshop-voice-" + Guid.NewGuid().ToString("N") + ".json");
        const string description = "Original expressive adult Czech baritone with a lightly gravelly texture.";
        try
        {
            File.WriteAllText(statePath, JsonSerializer.Serialize(new
            {
                customVoiceId = "voice_workshop_fixture", prompt = description
            }));
            var section = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConfigurationPath"] = statePath
            }).Build();
            var savedVoice = GptLiveVoiceOptions.Read(section);
            Require(savedVoice.CustomVoiceId == "voice_workshop_fixture" && savedVoice.Description == description,
                "provisioned voice state restores both the ID and its original acoustic description");
            Require(savedVoice.UseFallback().Description is null,
                "a built-in fallback does not retain the rejected custom voice description");
            section["CustomVoiceId"] = "voice_override_fixture";
            section["Description"] = new string('x', 4097);
            Require(GptLiveVoiceOptions.Read(section) is { CustomVoiceId: "voice_override_fixture", Description: null },
                "explicit voice override ignores the saved voice description and bounds operator-provided text");
            section["CustomVoiceId"] = "invalid";
            section["Description"] = description;
            Require(GptLiveVoiceOptions.Read(section).Description is null,
                "invalid custom IDs cannot attach an acoustic description to a built-in voice");
        }
        finally
        {
            File.Delete(statePath);
        }
        using var rejected = JsonDocument.Parse("""{"error":{"code":"voice_not_found"}}""");
        using var capacity = JsonDocument.Parse("""{"error":{"code":"rate_limit_exceeded"}}""");
        Require(options.Voice.CanRetryStartup(rejected.RootElement)
            && !options.Voice.UseFallback().CanRetryStartup(rejected.RootElement)
            && !options.Voice.CanRetryStartup(capacity.RootElement),
            "only explicit custom voice rejection permits one startup retry, always on Live");
        configuration["Backend:GptLive:AdultLanguage"] = "false";
        configuration["Backend:GptLive:Character:HostTone"] = "Chaos";
        var restricted = GptLiveOptions.Read(configuration);
        Require(restricted.Character.HostTone == CharacterTone.Dry
            && LivePromptSnapshot.Create(PersonalityCatalog.DefaultId, restricted).Instructions.Contains("No swearing"),
            "legacy adult-language withdrawal remains a conservative ceiling on DV01");
        var generated = new[] { new PartyWandCatalog.WandEntry(1, "Fialový blesk", "fiction", [], "fixture") };
        Require(!AutonomousLiveDialogue.LimitedTerminalReady("Tvá hůlka je Fialový blesk.", generated)
            && AutonomousLiveDialogue.LimitedTerminalReady("Výběr mám. Počkej na mechaniku.", generated),
            "generated wand names cannot bypass Blocked or Limited publication checks");
        var named = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Backend:GptLive:Character:FinaleProfile"] = "Named" }).Build();
        try { GptLiveOptions.Read(named); throw new Exception("Named finale was incorrectly enabled."); }
        catch (InvalidOperationException error) when (error.Message.Contains("named finale unavailable")) { }
        Console.WriteLine("GPT-Live voice configuration tests passed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new Exception(message); }
    }
}
