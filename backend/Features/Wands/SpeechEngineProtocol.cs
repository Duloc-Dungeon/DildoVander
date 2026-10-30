using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace Kufr.Backend.Features.Wands;

// Mirrors ElevenLabs' documented upstream protocol and SDK verification, rather
// than treating an anonymous WebSocket or a browser-supplied transcript as ElevenLabs.
internal static class SpeechEngineProtocol
{
    internal static bool Verify(string? value, string? apiKey, long now)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || string.IsNullOrWhiteSpace(apiKey)) return false;
        try
        {
            var jwt = value.Trim();
            if (jwt.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) jwt = jwt[7..].Trim();
            var parts = jwt.Split('.');
            if (parts.Length != 3 || parts.Any(string.IsNullOrEmpty)) return false;
            using var header = JsonDocument.Parse(WebEncoders.Base64UrlDecode(parts[0]));
            if (header.RootElement.GetProperty("alg").GetString() != "HS256"
                || header.RootElement.TryGetProperty("crit", out _)) return false;
            var normalized = Regex.Replace(apiKey.Trim(), "_residency_[a-z0-9]+$", "");
            var key = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            var expected = HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));
            if (!CryptographicOperations.FixedTimeEquals(expected, WebEncoders.Base64UrlDecode(parts[2]))) return false;
            using var payload = JsonDocument.Parse(WebEncoders.Base64UrlDecode(parts[1]));
            var claims = payload.RootElement;
            return claims.GetProperty("iss").GetString() == "https://api.elevenlabs.io/convai/speech-engine"
                && claims.GetProperty("sub").GetString() == "convai_speech_engine_upstream"
                && claims.GetProperty("exp").TryGetInt64(out var exp) && exp >= now - 60
                && claims.GetProperty("iat").TryGetInt64(out var iat) && iat <= now + 60 && iat <= exp
                && (!claims.TryGetProperty("nbf", out var nbf) || nbf.TryGetInt64(out var start) && start <= now + 60);
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        { return false; }
    }

    internal static long EventId(string? value) => value is null ? 0
        : long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 0
            ? number : throw new InvalidDataException("Invalid speech event.");

    internal static object Response(long eventId, string content, bool final) =>
        new { type = "agent_response", event_id = eventId, content, is_final = final };
}
