using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Voices;

namespace Kufr.Backend.Features.Wands;

internal sealed record WorkshopPromptSnapshot(long Revision, string DialoguePrompt, string LivePrompt, string ReasoningEffort);
internal sealed record WorkshopSettingsChange(long ExpectedRevision, string DialoguePrompt, string LivePrompt, string ReasoningEffort);
internal sealed class WorkshopSettingsException(string code, int status) : Exception(code)
{
    internal string Code { get; } = code;
    internal int Status { get; } = status;
}

internal sealed class WorkshopSettingsStore
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly string? path;
    private readonly WorkshopPromptSnapshot defaults;

    public WorkshopSettingsStore(IConfiguration config, AccountConfiguration accounts)
    {
        defaults = new(0, WorkshopPrompts.Dialogue, MobileWorkshopLiveRuntime.DefaultInstructions,
            config["Wands:ReasoningEffort"] ?? "none");
        if (!Valid(defaults.DialoguePrompt, defaults.LivePrompt, defaults.ReasoningEffort))
            throw new WorkshopSettingsException("workshop_settings_invalid", 503);
        path = accounts.Enabled ? Path.Combine(Path.GetFullPath(accounts.DataRoot), "workshop-settings.json") : null;
    }

    internal static bool ValidReasoning(string? value) => value is "none" or "low" or "medium" or "high";
    private static bool Valid(string? dialogue, string? live, string? effort) =>
        !string.IsNullOrWhiteSpace(dialogue) && dialogue.Length <= 12000
        && !string.IsNullOrWhiteSpace(live) && live.Length <= 12000 && ValidReasoning(effort);

    internal WorkshopPromptSnapshot Snapshot() => path is null ? defaults : Locked(Read);
    internal static object View(WorkshopPromptSnapshot snapshot) => new
    { snapshot.Revision, snapshot.DialoguePrompt, snapshot.LivePrompt, snapshot.ReasoningEffort, speechDirections = WorkshopPrompts.SpeechDirections };

    internal WorkshopPromptSnapshot Save(WorkshopSettingsChange change)
    {
        if (change.ExpectedRevision < 0 || !Valid(change.DialoguePrompt, change.LivePrompt, change.ReasoningEffort))
            throw new WorkshopSettingsException("workshop_settings_invalid", 400);
        if (path is null) throw new WorkshopSettingsException("workshop_settings_unavailable", 503);
        return Locked(() =>
        {
            var current = Read();
            if (current.Revision != change.ExpectedRevision || current.Revision == long.MaxValue)
                throw new WorkshopSettingsException("workshop_settings_conflict", 409);
            var next = new WorkshopPromptSnapshot(current.Revision + 1, change.DialoguePrompt, change.LivePrompt, change.ReasoningEffort);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(file, next, Json);
                    file.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
                return next;
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        });
    }

    private T Locked<T>(Func<T> action)
    {
        lock (Gates.GetOrAdd(path!, _ => new object()))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path!)!);
                using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return action();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw new WorkshopSettingsException("workshop_settings_unavailable", 503); }
        }
    }

    private WorkshopPromptSnapshot Read()
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path!).Length > 256 * 1024) throw new InvalidDataException();
            using var document = JsonDocument.Parse(File.ReadAllText(path!));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 4
                || !root.TryGetProperty("revision", out _) || !root.TryGetProperty("dialoguePrompt", out _)
                || !root.TryGetProperty("livePrompt", out _) || !root.TryGetProperty("reasoningEffort", out _)) throw new InvalidDataException();
            var saved = root.Deserialize<WorkshopPromptSnapshot>(Json);
            if (saved is null || saved.Revision < 0 || !Valid(saved.DialoguePrompt, saved.LivePrompt, saved.ReasoningEffort))
                throw new InvalidDataException();
            return saved;
        }
        catch (FileNotFoundException) { return defaults; }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException)
        { throw new WorkshopSettingsException("workshop_settings_corrupt", 503); }
    }
}

internal static class WorkshopSettingsEndpoints
{
    internal static void MapWorkshopSettings(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<AccountConfiguration>().Enabled) return;
        var group = app.MapGroup("/app/v1/admin/workshop-settings").RequireAuthorization().RequireRateLimiting("account");
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var http = invocation.HttpContext;
            http.Response.Headers.CacheControl = "private, no-store";
            var denied = await ActorVoiceSettingsEndpoints.AuthorizeAsync(http);
            if (denied is not null) return denied;
            try { return await next(invocation); }
            catch (WorkshopSettingsException error) { return ApiProblem.Create(http, error.Status, error.Code); }
        });
        group.MapGet("", (WorkshopSettingsStore settings) => Results.Ok(WorkshopSettingsStore.View(settings.Snapshot())));
        group.MapPut("", (WorkshopSettingsChange change, WorkshopSettingsStore settings) => Results.Ok(WorkshopSettingsStore.View(settings.Save(change))));
    }
}
