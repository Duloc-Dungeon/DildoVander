using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kufr.Backend.Features.Customers;

// Reviewed generated portraits are packaged with the application, so a provider
// outage does not turn a known character into an unrelated generic avatar.
internal sealed class CharacterPortraitFallbackProvider : ICharacterPortraitFallbackProvider, IDisposable
{
    private readonly WikimediaCharacterPortraitFallbackProvider sourced = new();
    private static readonly Lazy<Entry[]> Catalog = new(ReadCatalog);
    internal sealed record Entry(string Id, string Name, string[] Aliases, string World, string Sha256, bool Reviewed);

    public async Task<CharacterPortraitFallback?> FindAsync(CharacterPortraitJob job, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var generated = FindGenerated(job);
        return generated is not null ? new(generated, null) : await sourced.FindAsync(job, token);
    }

    internal static byte[]? FindGenerated(CharacterPortraitJob job)
    {
        // Never silently substitute the default portrayal for an explicitly
        // requested age, costume or alternate version.
        if (!string.IsNullOrWhiteSpace(job.Input.Version)) { return null; }
        var name = Normalize(job.Input.Name);
        var world = Normalize(job.Input.World);
        if (world is not ("harrypotter" or "wizardingworld" or "bradavice")) { return null; }
        var entries = Catalog.Value.Where(entry => entry.Reviewed && Normalize(entry.World) == "harrypotter"
            && (Normalize(entry.Name) == name || entry.Aliases.Any(alias => Normalize(alias) == name))).ToArray();
        if (entries.Length == 0) { return null; }
        // Stable per profile/revision, varied across visitors, including the three
        // separately painted Dementors. No identity goes to an external provider.
        var selection = SHA256.HashData(Encoding.UTF8.GetBytes(job.ProfileId + ":" + job.Revision));
        var selected = entries[(int)(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(selection) % (uint)entries.Length)];
        using var stream = typeof(CharacterPortraitFallbackProvider).Assembly.GetManifestResourceStream(
            "Kufr.Backend.CharacterPortraits." + selected.Id + ".webp");
        if (stream is null || stream.Length is 0 or > 8 * 1024 * 1024) { return null; }
        using var output = new MemoryStream();
        stream.CopyTo(output);
        var bytes = output.ToArray();
        return Convert.ToHexString(SHA256.HashData(bytes)).Equals(selected.Sha256, StringComparison.OrdinalIgnoreCase) ? bytes : null;
    }

    private static Entry[] ReadCatalog()
    {
        using var stream = typeof(CharacterPortraitFallbackProvider).Assembly.GetManifestResourceStream("Kufr.Backend.CharacterPortraits.catalog.json");
        return stream is null ? [] : JsonSerializer.Deserialize<Entry[]>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }

    private static string Normalize(string value) => new(value.Normalize(NormalizationForm.FormD)
        .Where(character => char.IsLetterOrDigit(character) && CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
        .Select(char.ToLowerInvariant).ToArray());

    public void Dispose() => sourced.Dispose();
}
