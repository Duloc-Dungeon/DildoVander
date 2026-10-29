using Kufr.Backend.Features.Identity;
using Microsoft.Net.Http.Headers;

namespace Kufr.Backend.Features.Launch;

// Only a fixed, non-confidential song is public. No request selects a file path.
internal sealed class RickrollMedia
{
    internal const string VideoPath = "/app/v1/launch/rickroll.mp4";
    private readonly string? filePath;

    public RickrollMedia(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var configured = configuration["Rickroll:FilePath"];
        if (string.IsNullOrWhiteSpace(configured)) { return; }
        if (!Path.IsPathFullyQualified(configured) || !configured.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        { throw new InvalidOperationException("Rickroll:FilePath must be an absolute MP4 path outside source and release directories."); }
        filePath = Path.GetFullPath(configured);
        foreach (var root in new[] { environment.ContentRootPath, environment.WebRootPath, AppContext.BaseDirectory })
        {
            if (!string.IsNullOrEmpty(root) && IsWithin(filePath, root))
            { throw new InvalidOperationException("Rickroll media must remain outside source and release directories."); }
        }
        for (var directory = Directory.GetParent(filePath); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, ".git")) || Directory.Exists(Path.Combine(directory.FullName, ".git")))
            { throw new InvalidOperationException("Rickroll media must remain outside Git checkouts."); }
        }
    }

    public bool Available => filePath is not null && File.Exists(filePath) && new FileInfo(filePath).Length > 0;

    internal IResult Stream(HttpContext context)
    {
        if (!Available) { return Results.NotFound(); }
        var file = new FileInfo(filePath!);
        context.Response.Headers.CacheControl = "public, max-age=3600";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.File(filePath!, "video/mp4", lastModified: file.LastWriteTimeUtc,
            entityTag: new EntityTagHeaderValue($"\"{file.Length:x}-{file.LastWriteTimeUtc.Ticks:x}\""), enableRangeProcessing: true);
    }

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}

internal static class RickrollMediaEndpoints
{
    internal static void MapRickrollMedia(this WebApplication app)
    {
        app.MapMethods(RickrollMedia.VideoPath, ["GET", "HEAD"], (HttpContext context, RickrollMedia media) => media.Stream(context));
        if (!app.Services.GetRequiredService<AccountConfiguration>().Enabled) { return; }
        app.MapGet("/app/v1/launch/rickroll", (RickrollMedia media, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { available = media.Available, url = media.Available ? RickrollMedia.VideoPath : null });
        }).RequireAuthorization().RequireRateLimiting("account");
    }
}
