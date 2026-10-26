using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kufr.Backend.Features.Identity;
using Microsoft.Extensions.Configuration;

internal static partial class AccountIdentityTests
{
    public static async Task RunAndroidAssociationAsync()
    {
        await using var fixture = await Fixture.StartAsync();
        using var client = fixture.Client();
        const string path = "/.well-known/assetlinks.json";
        var configuration = fixture.App.Configuration;
        Check((await client.GetAsync(path)).StatusCode == HttpStatusCode.NotFound,
            "Android association is opt-in and unavailable without a configured signing identity");
        configuration["ApplicationIdentity:AndroidPackageName"] = "de.dildovander.companion";
        configuration["ApplicationIdentity:AndroidCertificateSha256:0"] = new string('a', 64);
        using var response = await client.GetAsync(path);
        Check(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == "application/json",
            "Android association is public JSON without a session or CSRF token");
        var association = await response.Content.ReadFromJsonAsync<JsonElement>();
        var target = association[0].GetProperty("target");
        Check(target.GetProperty("package_name").GetString() == "de.dildovander.companion"
            && target.GetProperty("sha256_cert_fingerprints")[0].GetString() == string.Join(":", Enumerable.Repeat("AA", 32)),
            "Android association publishes only the explicit package and normalized certificate");
        var native = AndroidAppAssociation.Read(configuration)!;
        Check(native.PasskeyOrigins.Single() == "android:apk-key-hash:" + Convert.ToBase64String(Enumerable.Repeat((byte)0xaa, 32).ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            "Native passkey origin uses the exact unpadded base64url SHA-256 certificate");
        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, path));
        Check(head.StatusCode == HttpStatusCode.OK && (await head.Content.ReadAsByteArrayAsync()).Length == 0,
            "Android association supports HEAD without a body");
        configuration["ApplicationIdentity:AndroidCertificateSha256:1"] = "wildcard";
        Check((await client.GetAsync(path)).StatusCode == HttpStatusCode.NotFound && AndroidAppAssociation.Read(configuration) is null,
            "One malformed certificate rejects the whole association and native origin allowlist");
        configuration["ApplicationIdentity:AndroidCertificateSha256:1"] = null;
        configuration["ApplicationIdentity:AndroidPackageName"] = "de.*";
        Check(AndroidAppAssociation.Read(configuration) is null, "Android package wildcards are rejected");

        await NativePasskeyOriginAsync();
    }

    private static async Task NativePasskeyOriginAsync()
    {
        await using var fixture = await Fixture.StartAsync(configureConfiguration: configuration =>
        {
            configuration["ApplicationIdentity:AndroidPackageName"] = "de.dildovander.companion";
            configuration["ApplicationIdentity:AndroidCertificateSha256:0"] = new string('a', 64);
        });
        using var client = fixture.Client();
        var nativeOrigin = AndroidAppAssociation.Read(fixture.App.Configuration)!.PasskeyOrigins.Single();
        using var nativeKey = new VirtualPasskey(nativeOrigin);
        await CsrfAsync(client);
        await EnrollAsync(client, nativeKey, Fixture.Bootstrap, "Android owner");
        await CsrfAsync(client);
        Check((await client.DeleteAsync("/app/v1/session")).IsSuccessStatusCode, "Android passkey enrollment issues a working account session");
        await CsrfAsync(client);
        await SignInAsync(client, nativeKey);

        using var wrongOrigin = fixture.Client();
        await CsrfAsync(wrongOrigin);
        using var optionsResponse = await wrongOrigin.PostAsJsonAsync("/app/v1/passkeys/request-options", new { });
        var options = await JsonAsync(optionsResponse);
        using var denied = await wrongOrigin.PostAsJsonAsync("/app/v1/passkeys/sign-in", new { credential = nativeKey.Assert(options, "android:apk-key-hash:" + new string('A', 43)) });
        Check(!denied.IsSuccessStatusCode, "An unconfigured Android certificate origin cannot authenticate");
    }
}
