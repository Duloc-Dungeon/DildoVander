using System.Net;
using System.Security.Cryptography;

namespace Kufr.Backend.Features.Push;

internal sealed record PushRegistration(string Platform, string? Token = null, string? Endpoint = null,
    string? P256dh = null, string? Auth = null);
internal sealed record PushMessage(string Title, string Body, string Url, string Tag);
internal sealed record PushSubscription(string Id, string UserId, string SessionId, PushRegistration Registration);
internal sealed record PushDelivery(long Id, PushSubscription Subscription, string EventKey,
    PushMessage Message, int Attempts, long ExpiresAt);
internal enum PushSendResult { Sent, InvalidSubscription, Retry, Rejected }
internal interface IPushSender
{
    Task<PushSendResult> SendAsync(PushSubscription subscription, PushMessage message, CancellationToken cancellationToken);
}

internal sealed class PushConfiguration(IConfiguration configuration)
{
    internal string? PublicKey => configuration["Push:Web:PublicKey"];
    internal string? this[string key] => configuration["Push:" + key];
    internal bool Sandbox => configuration.GetValue<bool>("Push:Ios:Sandbox");
    internal bool Enabled(string platform) => configuration.GetValue<bool>("Push:Enabled") && platform switch
    {
        "web" => Present("Web:PublicKey", "Web:PrivateKey", "Web:Subject"),
        "android" => Present("Android:ProjectId", "Android:ServiceAccountPath"),
        "ios" => Present("Ios:TeamId", "Ios:KeyId", "Ios:PrivateKeyPath", "Ios:Topic"),
        _ => false
    };
    private bool Present(params string[] keys) => keys.All(key => !string.IsNullOrWhiteSpace(this[key]));
}

internal static class PushValidation
{
    internal static bool Valid(PushRegistration registration)
    {
        if (registration.Platform == "web")
        {
            if (!AllowedEndpoint(registration.Endpoint) || !Decode(registration.P256dh, 65, out var key)
                || key[0] != 4 || !Decode(registration.Auth, 16, out _)) { return false; }
            try
            {
                using var curve = ECDiffieHellman.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = key[1..33], Y = key[33..65] }
                });
                return true;
            }
            catch (CryptographicException) { return false; }
        }
        var token = registration.Token;
        if (registration.Platform == "ios")
        { return token is { Length: >= 2 and <= 512 } && token.Length % 2 == 0 && token.All(Uri.IsHexDigit); }
        return registration.Platform == "android" && token is { Length: >= 20 and <= 4096 }
            && token.All(character => char.IsAsciiLetterOrDigit(character) || character is ':' or '_' or '-');
    }

    internal static bool AllowedEndpoint(string? endpoint)
    {
        if (endpoint is null || endpoint.Length > 4096 || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0 || uri.HostNameType != UriHostNameType.Dns) { return false; }
        var host = uri.IdnHost;
        return host == "fcm.googleapis.com" || host == "updates.push.services.mozilla.com"
            || host == "web.push.apple.com" || host.EndsWith(".push.apple.com", StringComparison.Ordinal)
            || host.EndsWith(".notify.windows.com", StringComparison.Ordinal);
    }

    private static bool Decode(string? value, int length, out byte[] bytes)
    {
        bytes = [];
        if (value is null || value.Length > 100 || value.Any(character =>
            !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')) { return false; }
        try
        {
            bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/')
                .PadRight((value.Length + 3) / 4 * 4, '='));
            return bytes.Length == length;
        }
        catch (FormatException) { return false; }
    }

    internal static bool PublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) { address = address.MapToIPv4(); }
        if (IPAddress.IsLoopback(address)) { return false; }
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        { return (bytes[0] & 0xe0) == 0x20; } // Only globally routed IPv6 unicast.
        return bytes[0] is not (0 or 10 or 127) && bytes[0] < 224
            && !(bytes[0] == 169 && bytes[1] == 254)
            && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            && !(bytes[0] == 192 && bytes[1] == 168)
            && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
    }
}
