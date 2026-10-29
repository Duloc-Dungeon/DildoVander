using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kufr.Backend.Features.GptLive;

// Frozen with the prompt at session creation, never taken from guest input.
internal sealed record GptLiveVoiceOptions(string? CustomVoiceId = null, string FallbackVoice = "vesper",
    string ConfigurationStatus = "built_in")
{
    internal string? Description { get; init; }
    internal static readonly string[] BuiltInVoices = ["vesper", "cedar", "marin", "coral", "quartz", "ripple",
        "willow", "stone", "gleam", "meridian", "bossa", "tempo", "beacon", "delta", "cinder", "nuri",
        "juni", "flitz", "brise", "sillage", "nira", "harema", "shida", "noeul"];

    internal object SessionVoice => CustomVoiceId is { } id ? new { id } : FallbackVoice;
    internal GptLiveVoiceOptions UseFallback() => this with
    {
        CustomVoiceId = null, Description = null, ConfigurationStatus = "custom_unavailable"
    };
    internal static bool ValidId(string? id) => id is { Length: >= 7 and <= 128 }
        && Regex.IsMatch(id, "\\Avoice_[A-Za-z0-9_-]+\\z", RegexOptions.CultureInvariant);

    internal static GptLiveVoiceOptions Read(IConfiguration section)
    {
        var fallback = section["FallbackVoice"] ?? "vesper";
        if (!BuiltInVoices.Contains(fallback, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Unsupported GPT-Live fallback voice.");
        }
        var id = section["CustomVoiceId"];
        var description = section["Description"];
        var status = "built_in";
        if (string.IsNullOrWhiteSpace(id) && section["ConfigurationPath"] is { Length: > 0 } path)
        {
            try
            {
                if (!Path.IsPathFullyQualified(path) || new FileInfo(path).Length > 8_192)
                {
                    return new(null, fallback, "invalid_voice_configuration");
                }
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                id = document.RootElement.GetProperty("customVoiceId").GetString();
                if (document.RootElement.TryGetProperty("prompt", out var prompt))
                {
                    description = prompt.GetString();
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                or InvalidOperationException or KeyNotFoundException or ArgumentException)
            {
                return new(null, fallback, "voice_configuration_unavailable");
            }
        }
        if (!string.IsNullOrWhiteSpace(id))
        {
            status = ValidId(id) ? "custom" : "invalid_custom_voice";
        }
        return new(ValidId(id) ? id : null, fallback, status)
        {
            Description = ValidId(id) && !string.IsNullOrWhiteSpace(description) && description.Length <= 4096
                && !description.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t'))
                ? description : null
        };
    }

    // Only an explicit voice rejection before session.started permits a new Live
    // session with the built-in voice. Auth, capacity and generic failures surface.
    internal bool CanRetryStartup(JsonElement value)
    {
        if (CustomVoiceId is null || !value.TryGetProperty("error", out var error)
            || error.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        var code = error.TryGetProperty("code", out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString() : null;
        var param = error.TryGetProperty("param", out field) && field.ValueKind == JsonValueKind.String
            ? field.GetString() : null;
        return code is "voice_not_found" or "invalid_voice" or "custom_voice_unavailable" or "voice_access_denied"
            || (param is "audio.output.voice" or "session.audio.output.voice"
                && code is "not_found" or "invalid_value" or "permission_denied");
    }
}
