using Microsoft.Extensions.Options;

namespace Kufr.Backend.Features.ApplicationUpdates;

internal static class ApplicationUpdateEndpoints
{
    internal static void MapApplicationUpdateEndpoints(this WebApplication app)
    {
        app.MapGet("/app/v1/application-version", (HttpContext context, IOptions<ApplicationUpdateSettings> options) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var settings = options.Value;

            return Results.Ok(new ApplicationVersionResponse(
                new ApplicationVersionPlatform(settings.Ios.MinimumBuild, settings.Ios.LatestBuild, settings.Ios.UpdateUrl),
                new ApplicationVersionPlatform(settings.Android.MinimumBuild, settings.Android.LatestBuild, settings.Android.UpdateUrl)));
        }).AllowAnonymous();
    }
}

internal sealed record ApplicationVersionResponse(
    ApplicationVersionPlatform Ios,
    ApplicationVersionPlatform Android);

internal sealed record ApplicationVersionPlatform(
    string MinimumBuild,
    string LatestBuild,
    string UpdateUrl);
