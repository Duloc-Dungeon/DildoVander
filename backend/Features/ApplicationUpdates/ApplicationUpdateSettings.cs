using System.Globalization;
using Microsoft.Extensions.Options;

namespace Kufr.Backend.Features.ApplicationUpdates;

internal sealed class ApplicationUpdateSettings
{
    public ApplicationPlatformUpdateSettings Ios { get; set; } = new()
    {
        UpdateUrl = "https://dildovander.de/ios/",
    };

    public ApplicationPlatformUpdateSettings Android { get; set; } = new();

    internal bool IsValid(out string error)
    {
        if (!Ios.IsValid(maximumComponents: 3, maximumComponent: int.MaxValue, out error))
        {
            error = $"ApplicationUpdates:Ios {error}";
            return false;
        }

        if (!Android.IsValid(maximumComponents: 1, maximumComponent: 2_100_000_000, out error))
        {
            error = $"ApplicationUpdates:Android {error}";
            return false;
        }

        if ((ApplicationPlatformUpdateSettings.HasNonZeroBuild(Android.MinimumBuild)
                || ApplicationPlatformUpdateSettings.HasNonZeroBuild(Android.LatestBuild))
            && string.IsNullOrEmpty(Android.UpdateUrl))
        {
            error = "ApplicationUpdates:Android:UpdateUrl is required when a build policy is configured.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

internal sealed class ApplicationPlatformUpdateSettings
{
    public string MinimumBuild { get; set; } = "0";

    public string LatestBuild { get; set; } = "0";

    public string UpdateUrl { get; set; } = string.Empty;

    internal bool IsValid(int maximumComponents, int maximumComponent, out string error)
    {
        if (!TryParseBuild(MinimumBuild, maximumComponents, maximumComponent, out var minimum)
            || !TryParseBuild(LatestBuild, maximumComponents, maximumComponent, out var latest))
        {
            error = $"build values must contain one to {maximumComponents} unsigned numeric component(s) "
                + $"within the allowed {maximumComponent} maximum.";
            return false;
        }

        if (CompareBuilds(latest, minimum) < 0)
        {
            error = "LatestBuild must be greater than or equal to MinimumBuild.";
            return false;
        }

        if (UpdateUrl is null)
        {
            error = "UpdateUrl is required.";
            return false;
        }

        if (maximumComponents == 1 && UpdateUrl.Length == 0
            && minimum.All(component => component == 0)
            && latest.All(component => component == 0))
        {
            error = string.Empty;
            return true;
        }

        if (!Uri.TryCreate(UpdateUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.UserInfo.Length > 0
            || !uri.IsWellFormedOriginalString())
        {
            error = "UpdateUrl must be an absolute HTTPS URL without embedded credentials.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    internal static bool HasNonZeroBuild(string value) =>
        TryParseBuild(value, maximumComponents: 1, maximumComponent: 2_100_000_000, out var components)
        && components.Any(component => component != 0);

    private static bool TryParseBuild(
        string? value,
        int maximumComponents,
        int maximumComponent,
        out int[] components)
    {
        components = [];
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var parts = value.Split('.');
        if (parts.Length == 0 || parts.Length > maximumComponents)
        {
            return false;
        }

        components = new int[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index].Length is < 1 or > 10
                || parts[index].Any(character => character is < '0' or > '9')
                || !int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var component)
                || component < 0
                || component > maximumComponent)
            {
                components = [];
                return false;
            }

            components[index] = component;
        }

        return true;
    }

    private static int CompareBuilds(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        var componentCount = Math.Max(left.Count, right.Count);
        for (var index = 0; index < componentCount; index++)
        {
            var comparison = (index < left.Count ? left[index] : 0)
                .CompareTo(index < right.Count ? right[index] : 0);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }
}

internal static class ApplicationUpdateSettingsRegistration
{
    internal static IServiceCollection AddApplicationUpdateSettings(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<ApplicationUpdateSettings>()
            .Bind(configuration.GetSection("ApplicationUpdates"))
            .Validate(settings => settings.IsValid(out _), "ApplicationUpdates configuration is invalid.")
            .ValidateOnStart();

        return services;
    }
}
