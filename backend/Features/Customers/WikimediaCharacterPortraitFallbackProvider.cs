using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace Kufr.Backend.Features.Customers;

internal sealed record CharacterPortraitSource(string Url, string Title, string Author, string License, string LicenseUrl);
internal sealed record CharacterPortraitFallback(byte[] Image, CharacterPortraitSource? Source);

internal interface ICharacterPortraitFallbackProvider
{
    Task<CharacterPortraitFallback?> FindAsync(CharacterPortraitJob job, CancellationToken token);
}

internal sealed class WikimediaCharacterPortraitFallbackProvider : ICharacterPortraitFallbackProvider, IDisposable
{
    private const string Api = "https://commons.wikimedia.org/w/api.php";
    private const int MaxJsonBytes = 1024 * 1024;
    private const int MaxImageBytes = 8 * 1024 * 1024;
    private readonly HttpClient http;
    private readonly bool ownsClient;

    public WikimediaCharacterPortraitFallbackProvider()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectCallback = ConnectPublicAsync
        };
        http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        SetUserAgent(http);
        ownsClient = true;
    }

    internal WikimediaCharacterPortraitFallbackProvider(HttpClient http)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        SetUserAgent(http);
    }

    private static void SetUserAgent(HttpClient client)
    {
        if (client.DefaultRequestHeaders.UserAgent.Count > 0) { return; }
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "WandNightStand/1.0 (Wikimedia Commons character portrait fallback; https://github.com/Blato58/DildoVander)");
    }

    public void Dispose()
    {
        if (ownsClient) { http.Dispose(); }
    }

    public async Task<CharacterPortraitFallback?> FindAsync(CharacterPortraitJob job, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(job);
        var name = Normalize(job.Enrichment.RecognizedName);
        var world = Normalize(job.Enrichment.World);
        if (job.Enrichment.NeedsClarification || !ValidTerm(name) || !ValidTerm(world)) { return null; }
        var rawVersion = job.Input.Version;
        var version = Normalize(job.Input.Version ?? "");
        if (!string.IsNullOrEmpty(rawVersion) && !ValidTerm(version)) { return null; }
        var query = $"intitle:\"{name}\" \"{world}\" portrait filetype:bitmap";
        var candidates = new List<Candidate>();
        var variant = ValidTerm(version) && !name.Contains(version, StringComparison.OrdinalIgnoreCase) ? " \"" + version + "\"" : "";
        query += variant;
        foreach (var search in new[] { query, $"intitle:\"{name}\" \"{world}\" cosplay{variant} filetype:bitmap" })
        {
            var results = await SearchAsync(search, token);
            if (results is null) { break; }
            foreach (var candidate in results)
            {
                if (IsRelevant(candidate, name, world, version)) { candidates.Add(candidate); }
            }
        }
        var ranked = candidates.DistinctBy(candidate => candidate.Title, StringComparer.Ordinal)
            .OrderByDescending(candidate => candidate.Rank).ThenBy(candidate => candidate.Title, StringComparer.Ordinal).Take(2);
        foreach (var candidate in ranked)
        {
            var found = await DownloadAsync(candidate, token);
            if (found is not null) { return found; }
        }
        return null;
    }

    private async Task<List<Candidate>?> SearchAsync(string query, CancellationToken token)
    {
        var uri = new Uri(Api + "?action=query&generator=search&gsrnamespace=6&gsrlimit=8&gsrsearch=" + Uri.EscapeDataString(query)
            + "&prop=imageinfo&iiprop=url%7Cmime%7Cthumbmime%7Csize%7Cextmetadata&iiurlwidth=768&format=json&formatversion=2&iiextmetadatafilter=Artist%7CImageDescription%7CLicense%7CLicenseShortName%7CLicenseUrl%7CCategories");
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests || !response.IsSuccessStatusCode) { return null; }
            var bytes = await ReadBoundedAsync(response.Content, MaxJsonBytes, token);
            using var document = JsonDocument.Parse(bytes);
            var output = new List<Candidate>();
            if (!document.RootElement.TryGetProperty("query", out var queryElement)
                || !queryElement.TryGetProperty("pages", out var pages)) { return output; }
            foreach (var page in pages.EnumerateArray())
            {
                if (!page.TryGetProperty("imageinfo", out var infos) || infos.GetArrayLength() == 0) { continue; }
                var info = infos[0];
                var ext = info.TryGetProperty("extmetadata", out var metadata) ? metadata : default;
                var author = Metadata(ext, "Artist");
                var license = Metadata(ext, "LicenseShortName");
                if (license.Length == 0) { license = Metadata(ext, "License"); }
                var licenseUrl = Metadata(ext, "LicenseUrl");
                var description = Metadata(ext, "ImageDescription");
                var categories = Metadata(ext, "Categories");
                var context = Plain(description + " " + categories, 1200);
                var title = Plain(page.TryGetProperty("title", out var t) ? t.GetString() : null, 300);
                var imageUrl = info.TryGetProperty("thumburl", out var thumb) ? thumb.GetString()
                    : info.TryGetProperty("url", out var original) ? original.GetString() : null;
                var mime = info.TryGetProperty("thumbmime", out var tm) ? tm.GetString()
                    : info.TryGetProperty("mime", out var m) ? m.GetString() : null;
                if (title.Length == 0 || imageUrl is null || !ValidImageUrl(imageUrl)
                    || (mime is not ("image/jpeg" or "image/png" or "image/webp"))
                    || !AllowedLicense(license) || author.Length == 0) { continue; }
                var sourceUrl = CommonsPage(title);
                if (!ValidSourceLicenseUrl(licenseUrl)) { licenseUrl = sourceUrl; }
                output.Add(new(title, imageUrl, mime!, author, license, licenseUrl, sourceUrl, context, RankTitle(title)));
            }
            return output;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is HttpRequestException or JsonException or IOException or InvalidDataException or UriFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<CharacterPortraitFallback?> DownloadAsync(Candidate candidate, CancellationToken token)
    {
        if (!ValidImageUrl(candidate.ImageUrl)) { return null; }
        try
        {
            using var response = await http.GetAsync(candidate.ImageUrl, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxImageBytes) { return null; }
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, candidate.Mime, StringComparison.OrdinalIgnoreCase)) { return null; }
            var bytes = await ReadBoundedAsync(response.Content, MaxImageBytes, token);
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec is null || codec.Info.Width < 1 || codec.Info.Height < 1
                || codec.Info.Width > 4096 || codec.Info.Height > 4096
                || (long)codec.Info.Width * codec.Info.Height > 12_000_000
                || codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp)) { return null; }
            using var bitmap = SKBitmap.Decode(codec);
            if (bitmap is null || bitmap.Width < 1 || bitmap.Height < 1) { return null; }
            var cropWidth = bitmap.Width;
            var cropHeight = bitmap.Height;
            if ((long)cropWidth * 3 > (long)cropHeight * 2)
            {
                cropWidth = Math.Max(1, (int)((long)cropHeight * 2 / 3));
            }
            else if ((long)cropWidth * 3 < (long)cropHeight * 2)
            {
                cropHeight = Math.Max(1, (int)((long)cropWidth * 3 / 2));
            }
            var factor = Math.Min(1d, Math.Min(768d / cropWidth, 1152d / cropHeight));
            var width = Math.Max(1, (int)Math.Round(cropWidth * factor));
            var height = Math.Max(1, (int)Math.Round(cropHeight * factor));
            var left = Math.Max(0, (bitmap.Width - cropWidth) / 2);
            var top = 0;
            using var image = SKImage.FromBitmap(bitmap);
            using var subset = image.Subset(new SKRectI(left, top, left + cropWidth, top + cropHeight));
            if (subset is null) { return null; }
            using var scaledBitmap = new SKBitmap(new SKImageInfo(width, height));
            using (var canvas = new SKCanvas(scaledBitmap))
            {
                canvas.DrawImage(subset, new SKRect(0, 0, width, height),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                canvas.Flush();
            }
            using var scaled = SKImage.FromBitmap(scaledBitmap);
            using var encoded = scaled.Encode(SKEncodedImageFormat.Webp, 86);
            if (encoded is null || encoded.Size > MaxImageBytes) { return null; }
            var source = new CharacterPortraitSource(candidate.SourceUrl, Plain(candidate.Title, 300),
                Plain(candidate.Author, 300), Plain(candidate.License, 80), candidate.LicenseUrl);
            return CharacterPortraitSourcePolicy.IsValid(source) ? new(encoded.ToArray(), source) : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsRelevant(Candidate candidate, string name, string world, string version)
    {
        var title = candidate.Title.Replace('_', ' ');
        if (!title.Contains(name, StringComparison.OrdinalIgnoreCase)) { return false; }
        if (ValidTerm(version) && !name.Contains(version, StringComparison.OrdinalIgnoreCase)
            && !title.Contains(version, StringComparison.OrdinalIgnoreCase)) { return false; }
        if (!title.Contains(world, StringComparison.OrdinalIgnoreCase)
            && !candidate.Context.Contains(world, StringComparison.OrdinalIgnoreCase)) { return false; }
        var lower = title.ToLowerInvariant();
        string[] exclude = ["logo", "poster", "book", "boxes", "soldier", "premiere", "lego", "mural", "statue", "figure", "unidentified", "group", "siblings", "wizards"];
        if (exclude.Any(marker => lower.Contains(marker, StringComparison.Ordinal))) { return false; }
        return RankTitle(title) > 0;
    }

    private static int RankTitle(string title)
    {
        var lower = title.Replace('_', ' ').ToLowerInvariant();
        if (Regex.IsMatch(lower, @"\bas\s+[^,()]+", RegexOptions.CultureInvariant)) { return 3; }
        if (lower.Contains("cosplay", StringComparison.Ordinal)) { return 2; }
        return lower.Contains("portrait", StringComparison.Ordinal) || lower.Contains("illustration", StringComparison.Ordinal) ? 1 : 0;
    }

    private static bool AllowedLicense(string value) => Regex.IsMatch(value,
        @"\A(?:CC0|Public domain|Public Domain|CC BY(?:-SA)? (?:1\.0|2\.0|2\.5|3\.0|4\.0))\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream(Math.Min(limit, (int)(content.Headers.ContentLength ?? 0)));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0) { break; }
            if (output.Length + read > limit) { throw new InvalidDataException("Response exceeded the configured limit."); }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string Metadata(JsonElement metadata, string key)
    {
        if (metadata.ValueKind != JsonValueKind.Object || !metadata.TryGetProperty(key, out var entry)
            || !entry.TryGetProperty("value", out var value)) { return ""; }
        return Plain(value.GetString(), key == "LicenseShortName" || key == "License" ? 80 : 300);
    }

    private static string Plain(string? value, int max)
    {
        var decoded = WebUtility.HtmlDecode(Regex.Replace(value ?? "", "<[^>]*>", " "));
        var clean = Regex.Replace(decoded, @"[\p{Cc}\s]+", " ", RegexOptions.CultureInvariant).Trim();
        return clean.Length <= max ? clean : clean[..max].Trim();
    }

    private static string Normalize(string value) => Regex.Replace(value.Trim().Normalize(NormalizationForm.FormKC), @"\s+", " ");
    private static bool ValidTerm(string value) => value.Length is > 0 and <= 100
        && value.All(character => char.IsLetterOrDigit(character) || character is ' ' or '.' or ',' or '\'' or '’' or '-' or '_');
    private static string CommonsPage(string title)
    {
        var fileTitle = title.StartsWith("File:", StringComparison.Ordinal) ? title[5..] : title;
        return "https://commons.wikimedia.org/wiki/File:" + Uri.EscapeDataString(fileTitle.Replace(' ', '_'));
    }
    private static bool ValidSourceLicenseUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.Host == "creativecommons.org" && string.IsNullOrEmpty(uri.UserInfo)
        && uri.Port == 443 && uri.AbsolutePath.StartsWith("/licenses/", StringComparison.Ordinal)
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
        && !value.Any(char.IsWhiteSpace) && !value.Contains('<') && !value.Contains('>');
    private static bool ValidImageUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && (uri.Host.Equals("upload.wikimedia.org", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("thumb.wikimedia.org", StringComparison.OrdinalIgnoreCase))
        && uri.Port == 443 && string.IsNullOrEmpty(uri.UserInfo)
        && uri.AbsolutePath.StartsWith("/wikipedia/commons/", StringComparison.Ordinal);

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) { address = address.MapToIPv4(); }
        if (IPAddress.IsLoopback(address)) { return false; }
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
                || (b[0] == 100 && b[1] is >= 64 and <= 127) || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && (b[1] == 0 || b[1] == 168))
                || (b[0] == 192 && b[1] == 0) || (b[0] == 192 && b[1] == 2)
                || (b[0] == 192 && b[1] == 88 && b[2] == 99)
                || (b[0] == 198 && b[1] is 18 or 19 or 51) || (b[0] == 203 && b[1] == 0 && b[2] == 113));
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6) { return false; }
        var bytes = address.GetAddressBytes();
        return (bytes[0] & 0xe0) == 0x20 && address.IsIPv6LinkLocal == false && address.IsIPv6Multicast == false
            && !(bytes[0] == 0x20 && bytes[1] == 0x02)
            && !(bytes[0] == 0xfc || bytes[0] == 0xfd || (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80))
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8)
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00)
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x02)
            && !(bytes.Take(15).All(value => value == 0) && bytes[15] <= 1);
    }

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
        var publicAddresses = addresses.Where(IsPublicAddress).ToArray();
        if (publicAddresses.Length == 0) { throw new HttpRequestException("Host did not resolve to a public address."); }
        Exception? lastError = null;
        foreach (var address in publicAddresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException) { socket.Dispose(); throw; }
            catch (Exception error) when (error is SocketException or IOException)
            {
                socket.Dispose();
                lastError = error;
            }
        }
        throw new HttpRequestException("Could not connect to a public host address.", lastError);
    }

    private sealed record Candidate(string Title, string ImageUrl, string Mime, string Author, string License,
        string LicenseUrl, string SourceUrl, string Context, int Rank);
}

internal static class CharacterPortraitSourcePolicy
{
    internal static bool IsValid(CharacterPortraitSource? source)
    {
        if (source is null || string.IsNullOrEmpty(source.Url) || string.IsNullOrEmpty(source.Title)
            || string.IsNullOrEmpty(source.Author) || string.IsNullOrEmpty(source.License)
            || string.IsNullOrEmpty(source.LicenseUrl) || !Plain(source.Title, 300) || !Plain(source.Author, 300) || !Plain(source.License, 80)
            || source.Title.Length > 300 || source.Author.Length > 300 || source.License.Length > 80
            || source.Url.Length > 2048 || source.LicenseUrl.Length > 2048
            || !source.Url.StartsWith("https://commons.wikimedia.org/wiki/File:", StringComparison.Ordinal)
            || !Uri.TryCreate(source.Url, UriKind.Absolute, out var page) || page.Host != "commons.wikimedia.org"
            || page.Scheme != "https" || page.Port != 443 || !string.IsNullOrEmpty(page.UserInfo)
            || !string.IsNullOrEmpty(page.Query) || !string.IsNullOrEmpty(page.Fragment)
            || source.Url.Any(char.IsWhiteSpace) || source.Url.Contains('<') || source.Url.Contains('>')
            || !AllowedLicense(source.License)) { return false; }
        var validLicenseUrl = Uri.TryCreate(source.LicenseUrl, UriKind.Absolute, out var license)
            && license.Scheme == "https" && license.Port == 443 && string.IsNullOrEmpty(license.UserInfo)
            && string.IsNullOrEmpty(license.Query) && string.IsNullOrEmpty(license.Fragment)
            && !source.LicenseUrl.Any(char.IsWhiteSpace) && !source.LicenseUrl.Contains('<') && !source.LicenseUrl.Contains('>')
            && ((license.Host == "creativecommons.org" && license.AbsolutePath.StartsWith("/licenses/", StringComparison.Ordinal))
                || (license.Host == "commons.wikimedia.org" && license.AbsolutePath.StartsWith("/wiki/File:", StringComparison.Ordinal)));
        return validLicenseUrl;
    }

    private static bool Plain(string value, int limit) => value is not null && value.Length is > 0 && value.Length <= limit
        && !value.Any(char.IsControl) && !value.Contains('<') && !value.Contains('>');
    private static bool AllowedLicense(string value) => Regex.IsMatch(value,
        @"\A(?:CC0|Public domain|Public Domain|CC BY(?:-SA)? (?:1\.0|2\.0|2\.5|3\.0|4\.0))\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
