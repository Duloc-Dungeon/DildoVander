using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Wands;

namespace Kufr.Backend.Features.Identity;

internal sealed record LocalTryHubExperimentSnapshot(long Revision, string Provider, string DialoguePrompt, string LivePrompt);
internal sealed record LocalTryHubExperimentUpdate(long ExpectedRevision, string Provider, string DialoguePrompt, string LivePrompt);

// Developer-authored experiments are separate from guest input and production
// defaults. Each admission copies this immutable snapshot before a call starts.
internal sealed class LocalTryHubExperimentStore
{
    private readonly object gate = new();
    private readonly string? path;
    private LocalTryHubExperimentSnapshot settings;
    private string? loadError;
    private readonly bool speechEngineAvailable;
    internal LocalTryHubExperimentSnapshot Defaults { get; }

    public LocalTryHubExperimentStore(IConfiguration configuration, IHostEnvironment environment)
    {
        speechEngineAvailable = !string.IsNullOrWhiteSpace(configuration["Wands:Voice:SpeechEnginePublicOrigin"])
            && !string.IsNullOrWhiteSpace(configuration["ELEVENLABS_API_KEY"]);
        Defaults = new(0, configuration["Wands:Voice:Provider"] ?? "speech-engine",
            OpenAiMobileWandQuestionProvider.DefaultInstructions, MobileWorkshopLiveRuntime.DefaultInstructions);
        settings = Defaults;
        if (!environment.IsDevelopment() || !configuration.GetValue<bool>("LocalTryHub:Enabled")) { return; }
        _ = LocalTryHubConfiguration.Read(configuration, AccountConfiguration.Read(configuration));
        path = Path.Combine(Path.GetFullPath(configuration["LocalTryHub:DataRoot"]!), "experiments.json");
        if (!File.Exists(path)) { return; }
        try
        {
            if (new FileInfo(path).Length > 256 * 1024) { throw new InvalidDataException(); }
            var saved = JsonSerializer.Deserialize<LocalTryHubExperimentSnapshot>(File.ReadAllText(path));
            if (saved is null || saved.Revision < 0 || !Valid(saved.Provider, saved.DialoguePrompt, saved.LivePrompt))
            { throw new InvalidDataException(); }
            settings = saved;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            // Preserve the invalid file for inspection; never silently overwrite it.
            loadError = "local_try_hub_settings_unreadable";
        }
    }

    internal LocalTryHubExperimentSnapshot? Snapshot()
    {
        lock (gate) { return path is null ? null : settings; }
    }

    internal object View()
    {
        lock (gate) { return new { settings, defaults = new { Defaults.Provider, Defaults.DialoguePrompt,
            Defaults.LivePrompt, speechDirections = WorkshopPrompts.SpeechDirections, speechEngineAvailable,
            speechEngineUnavailableReason = speechEngineAvailable ? null
                : "Speech Engine potřebuje veřejný callback. Spusť hub se zapnutým tunelem pro Speech Engine." }, loadError }; }
    }

    internal IResult Save(HttpContext context, LocalTryHubExperimentUpdate change)
    {
        lock (gate)
        {
            if (path is null) { return ApiProblem.Create(context, 404, "local_try_hub_unavailable"); }
            if (change.ExpectedRevision != settings.Revision || settings.Revision == long.MaxValue)
            { return ApiProblem.Create(context, 409, "local_try_hub_settings_conflict"); }
            if (!Valid(change.Provider, change.DialoguePrompt, change.LivePrompt))
            { return ApiProblem.Create(context, 400, "local_try_hub_settings_invalid"); }
            var next = new LocalTryHubExperimentSnapshot(settings.Revision + 1,
                change.Provider, change.DialoguePrompt, change.LivePrompt);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(temporary, JsonSerializer.Serialize(next));
                File.Move(temporary, path, overwrite: true);
                settings = next;
                loadError = null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { return ApiProblem.Create(context, 503, "local_try_hub_settings_unavailable"); }
            finally
            {
                try { if (File.Exists(temporary)) { File.Delete(temporary); } }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            return Results.Ok(View());
        }
    }

    private static bool Valid(string? provider, string? dialogue, string? live) => provider is "elevenlabs" or "gpt-live" or "speech-engine"
        && !string.IsNullOrWhiteSpace(dialogue) && dialogue.Length <= 12000
        && !string.IsNullOrWhiteSpace(live) && live.Length <= 12000;
}
