using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using WebPush;

namespace Kufr.Backend.Features.Push;

internal sealed class PushSender : IPushSender, IDisposable
{
    private readonly PushConfiguration _configuration;
    private readonly TimeProvider _clock;
    private readonly HttpClient _http;
    private readonly WebPushClient _web;
    private GoogleCredential? _google;
    private string? _appleToken;
    private long _appleTokenAt;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public PushSender(PushConfiguration configuration, TimeProvider clock)
    {
        _configuration = configuration;
        _clock = clock;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = ConnectPublicAsync
        }) { Timeout = TimeSpan.FromSeconds(20) };
        _web = new WebPushClient(_http);
    }

    public async Task<PushSendResult> SendAsync(PushSubscription subscription, PushMessage message,
        CancellationToken cancellationToken)
    {
        if (!_configuration.Enabled(subscription.Registration.Platform)) { return PushSendResult.Rejected; }
        if (!PushValidation.Valid(subscription.Registration)) { return PushSendResult.InvalidSubscription; }
        return subscription.Registration.Platform switch
        {
            "web" => await SendWebAsync(subscription.Registration, message, cancellationToken),
            "android" => await SendAndroidAsync(subscription.Registration.Token!, message, cancellationToken),
            "ios" => await SendIosAsync(subscription.Registration.Token!, message, cancellationToken),
            _ => PushSendResult.Rejected
        };
    }

    private async Task<PushSendResult> SendWebAsync(PushRegistration subscription, PushMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            await _web.SendNotificationAsync(new WebPush.PushSubscription(subscription.Endpoint,
                    subscription.P256dh, subscription.Auth), JsonSerializer.Serialize(message, Json),
                new Dictionary<string, object>
                {
                    ["TTL"] = 600,
                    ["vapidDetails"] = new VapidDetails(_configuration["Web:Subject"],
                        _configuration.PublicKey, _configuration["Web:PrivateKey"])
                }, cancellationToken);
            return PushSendResult.Sent;
        }
        catch (WebPushException error)
        {
            return error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone
                ? PushSendResult.InvalidSubscription : Status(error.StatusCode);
        }
    }

    private async Task<PushSendResult> SendAndroidAsync(string token, PushMessage message, CancellationToken cancellationToken)
    {
        // Restrict credential parsing to service accounts; never accept arbitrary credential types.
        _google ??= GoogleCredential.FromServiceAccountCredential(
            CredentialFactory.FromFile<ServiceAccountCredential>(_configuration["Android:ServiceAccountPath"]!))
            .CreateScoped("https://www.googleapis.com/auth/firebase.messaging");
        var accessToken = await _google.UnderlyingCredential.GetAccessTokenForRequestAsync(null, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://fcm.googleapis.com/v1/projects/" + Uri.EscapeDataString(_configuration["Android:ProjectId"]!) + "/messages:send");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new
        {
            message = new
            {
                token, notification = new { title = message.Title, body = message.Body },
                data = new { url = message.Url, tag = message.Tag },
                android = new { ttl = "600s", notification = new { tag = message.Tag } }
            }
        });
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode) { return PushSendResult.Sent; }
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            if (error.Contains("\"UNREGISTERED\"", StringComparison.Ordinal))
            { return PushSendResult.InvalidSubscription; }
        }
        return Status(response.StatusCode);
    }

    private async Task<PushSendResult> SendIosAsync(string token, PushMessage message, CancellationToken cancellationToken)
    {
        var host = _configuration.Sandbox ? "api.sandbox.push.apple.com" : "api.push.apple.com";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/3/device/{token}")
        { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        request.Headers.Authorization = new AuthenticationHeaderValue("bearer", AppleToken());
        request.Headers.Add("apns-topic", _configuration["Ios:Topic"]);
        request.Headers.Add("apns-push-type", "alert");
        request.Headers.Add("apns-expiration", (_clock.GetUtcNow().ToUnixTimeSeconds() + 600).ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("apns-collapse-id", message.Tag);
        request.Content = JsonContent.Create(new
        {
            aps = new { alert = new { title = message.Title, body = message.Body }, sound = "default" },
            url = message.Url, tag = message.Tag
        });
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Gone) { return PushSendResult.InvalidSubscription; }
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            if (error.Contains("BadDeviceToken", StringComparison.Ordinal) || error.Contains("DeviceTokenNotForTopic", StringComparison.Ordinal))
            { return PushSendResult.InvalidSubscription; }
        }
        return Status(response.StatusCode);
    }

    private string AppleToken()
    {
        var now = _clock.GetUtcNow().ToUnixTimeSeconds();
        if (_appleToken is not null && now - _appleTokenAt < 3000) { return _appleToken; }
        var header = Base64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = _configuration["Ios:KeyId"] }));
        var claims = Base64(JsonSerializer.SerializeToUtf8Bytes(new { iss = _configuration["Ios:TeamId"], iat = now }));
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(_configuration["Ios:PrivateKeyPath"]!));
        var unsigned = header + "." + claims;
        var signature = key.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        _appleTokenAt = now;
        return _appleToken = unsigned + "." + Base64(signature);
    }

    private static string Base64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static PushSendResult Status(HttpStatusCode status) => (int)status switch
    {
        >= 200 and < 300 => PushSendResult.Sent,
        408 or 429 or >= 500 => PushSendResult.Retry,
        _ => PushSendResult.Rejected
    };

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(address => !PushValidation.PublicAddress(address)))
        { throw new HttpRequestException("Push provider address is not public."); }
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }

    public void Dispose() { _web.Dispose(); _http.Dispose(); }
}
