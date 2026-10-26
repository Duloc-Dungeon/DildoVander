using System.Text.RegularExpressions;

namespace Kufr.Backend.Features.Identity;

// Android's WebAuthn origin identifies the signing certificate, independently
// of the HTTPS Origin header used by native HTTP and antiforgery checks.
internal sealed record AndroidAppAssociation(string PackageName, string[] Certificates)
{
    public string[] PasskeyOrigins => Certificates.Select(certificate =>
        "android:apk-key-hash:" + Convert.ToBase64String(Convert.FromHexString(certificate.Replace(":", "")))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_')).ToArray();

    public static AndroidAppAssociation? Read(IConfiguration configuration)
    {
        var packageName = configuration["ApplicationIdentity:AndroidPackageName"];
        var certificates = configuration.GetSection("ApplicationIdentity:AndroidCertificateSha256").Get<string[]>() ?? [];
        if (packageName is null || !Regex.IsMatch(packageName, @"\A[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*)+\z")
            || certificates.Length is 0 or > 8
            || certificates.Any(value => value is null || !Regex.IsMatch(value,
                @"\A(?:[A-Fa-f0-9]{64}|(?:[A-Fa-f0-9]{2}:){31}[A-Fa-f0-9]{2})\z")))
        {
            return null;
        }
        var normalized = certificates.Select(value => string.Join(":", Convert.FromHexString(value.Replace(":", ""))
            .Select(octet => octet.ToString("X2", System.Globalization.CultureInfo.InvariantCulture))))
            .Distinct(StringComparer.Ordinal).ToArray();
        return new(packageName, normalized);
    }

    public object AssetLinks() => new[]
    {
        new
        {
            relation = new[] { "delegate_permission/common.get_login_creds", "delegate_permission/common.handle_all_urls" },
            target = new { @namespace = "android_app", package_name = PackageName, sha256_cert_fingerprints = Certificates }
        }
    };
}
