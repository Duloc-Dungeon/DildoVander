using System.Net;
using System.Net.Http.Headers;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Launch;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class RickrollMediaTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "rickroll-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "app"));
        var path = Path.Combine(root, "rickroll.mp4");
        var bytes = Enumerable.Range(0, 128).Select(value => (byte)value).ToArray();
        await File.WriteAllBytesAsync(path, bytes);
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = Path.Combine(root, "app") });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration["Rickroll:FilePath"] = path;
            builder.Services.AddSingleton(new AccountConfiguration(false, "", "", "", []));
            builder.Services.AddSingleton<RickrollMedia>();
            await using var app = builder.Build();
            app.MapRickrollMedia();
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var partial = new HttpRequestMessage(HttpMethod.Get, RickrollMedia.VideoPath);
            partial.Headers.Range = new RangeHeaderValue(10, 19);
            using var response = await client.SendAsync(partial);
            Check(response.StatusCode == HttpStatusCode.PartialContent, "video supports byte ranges");
            Check((await response.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes[10..20]), "range returns exact source bytes");
            Check(response.Content.Headers.ContentRange?.Length == bytes.Length, "range reports source length");
            Check(response.Content.Headers.ContentType?.MediaType == "video/mp4", "video has MP4 content type");
            Check(response.Headers.GetValues("X-Content-Type-Options").Single() == "nosniff", "video cannot be content-sniffed");
            using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, RickrollMedia.VideoPath));
            Check(head.StatusCode == HttpStatusCode.OK && head.Content.Headers.ContentLength == bytes.Length, "HEAD exposes length without media transfer");
            using var invalidRange = new HttpRequestMessage(HttpMethod.Get, RickrollMedia.VideoPath);
            invalidRange.Headers.Range = new RangeHeaderValue(500, 600);
            using var invalid = await client.SendAsync(invalidRange);
            Check(invalid.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable, "invalid ranges are rejected");
            File.Delete(path);
            using var missing = await client.GetAsync(RickrollMedia.VideoPath);
            Check(missing.StatusCode == HttpStatusCode.NotFound, "missing media never falls back to an arbitrary file");
            builder.Configuration["Rickroll:FilePath"] = Path.Combine(root, "app", "rickroll.mp4");
            try { _ = new RickrollMedia(builder.Configuration, builder.Environment); throw new Exception("source media was accepted"); }
            catch (InvalidOperationException) { }
            await app.StopAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("Rickroll media checks passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
