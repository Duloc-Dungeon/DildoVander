using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Wands;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Voices;

internal sealed record ActorVoiceSnapshot(long Revision, string KufrVoiceId, string BaronVoiceId);
internal sealed record ActorVoiceChange(long ExpectedRevision, string KufrVoiceId, string BaronVoiceId);
internal sealed class ActorVoiceSettingsException(string code, int status) : Exception(code)
{
    internal string Code { get; } = code;
    internal int Status { get; } = status;
}

internal sealed class ActorVoiceSettings
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly string? path;
    private readonly ActorVoiceSnapshot defaults;

    public ActorVoiceSettings(IConfiguration configuration, AccountConfiguration accounts)
    {
        defaults = new(0, configuration["Wands:Voice:VoiceId"] ?? WorkshopPrompts.VoiceId,
            configuration["Game:BossVoice:VoiceId"] ?? WorkshopPrompts.VoiceId);
        if (!ValidVoiceId(defaults.KufrVoiceId) || !ValidVoiceId(defaults.BaronVoiceId))
        { throw new ActorVoiceSettingsException("actor_voice_configuration_invalid", 503); }
        path = accounts.Enabled ? Path.Combine(Path.GetFullPath(accounts.DataRoot), "actor-voices.json") : null;
    }

    internal static bool ValidVoiceId(string? value) => value is { Length: > 0 and <= 100 }
        && value.All(char.IsAsciiLetterOrDigit);

    internal ActorVoiceSnapshot Snapshot() => path is null ? defaults : Locked(Read);

    internal ActorVoiceSnapshot Save(ActorVoiceChange change)
    {
        if (!ValidVoiceId(change.KufrVoiceId) || !ValidVoiceId(change.BaronVoiceId) || change.ExpectedRevision < 0)
        { throw new ActorVoiceSettingsException("actor_voice_settings_invalid", 400); }
        if (path is null) { throw new ActorVoiceSettingsException("actor_voice_settings_unavailable", 503); }
        return Locked(() =>
        {
            var current = Read();
            if (change.ExpectedRevision != current.Revision || current.Revision == long.MaxValue)
            { throw new ActorVoiceSettingsException("actor_voice_settings_conflict", 409); }
            var next = new ActorVoiceSnapshot(current.Revision + 1, change.KufrVoiceId, change.BaronVoiceId);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
                    writer.Write(JsonSerializer.Serialize(next, Json));
                    writer.Flush();
                    output.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
                return next;
            }
            finally
            {
                try { if (File.Exists(temporary)) { File.Delete(temporary); } }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        });
    }

    private T Locked<T>(Func<T> action)
    {
        lock (Gates.GetOrAdd(path!, _ => new object()))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path!)!);
                // Also fence another server process sharing this durable root.
                using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return action();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw new ActorVoiceSettingsException("actor_voice_settings_unavailable", 503); }
        }
    }

    private ActorVoiceSnapshot Read()
    {
        try
        {
            using var input = new FileStream(path!, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 4096) { throw new InvalidDataException(); }
            using var document = JsonDocument.Parse(input);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3
                || !root.TryGetProperty("revision", out _) || !root.TryGetProperty("kufrVoiceId", out _)
                || !root.TryGetProperty("baronVoiceId", out _)) { throw new InvalidDataException(); }
            var value = root.Deserialize<ActorVoiceSnapshot>(Json);
            if (value is null || value.Revision < 0 || !ValidVoiceId(value.KufrVoiceId) || !ValidVoiceId(value.BaronVoiceId))
            { throw new InvalidDataException(); }
            return value;
        }
        catch (FileNotFoundException) { return defaults; }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException)
        { throw new ActorVoiceSettingsException("actor_voice_settings_corrupt", 503); }
    }
}

internal static class ActorVoiceSettingsEndpoints
{
    internal static void MapActorVoiceSettings(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<AccountConfiguration>().Enabled) { return; }
        var group = app.MapGroup("/app/v1/admin/actor-voices").RequireAuthorization().RequireRateLimiting("account");
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var http = invocation.HttpContext;
            http.Response.Headers.CacheControl = "private, no-store";
            var denied = await AuthorizeAsync(http);
            if (denied is not null) { return denied; }
            try { return await next(invocation); }
            catch (ActorVoiceSettingsException error) { return ApiProblem.Create(http, error.Status, error.Code); }
        });
        group.MapGet("", (ActorVoiceSettings settings) => Results.Ok(settings.Snapshot()));
        group.MapPut("", (ActorVoiceChange change, ActorVoiceSettings settings) => Results.Ok(settings.Save(change)));
    }

    internal static async Task<IResult?> AuthorizeAsync(HttpContext http)
    {
        var accountId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var sessionId = http.User.FindFirstValue(AccountIdentity.SessionClaim);
        if (accountId is null || sessionId is null || http.User.Identity?.IsAuthenticated != true)
        { return ApiProblem.Create(http, 401, "account_required"); }
        var database = http.RequestServices.GetRequiredService<AccountDatabase>();
        if (!await database.Users.AsNoTracking().AnyAsync(user => user.Id == accountId && !user.Disabled
                && !database.DeletionRequests.Any(request => request.UserId == user.Id), http.RequestAborted)
            || !await database.Sessions.AsNoTracking().AnyAsync(session => session.Id == sessionId
                && session.UserId == accountId && session.ExpiresAt > AccountEndpoints.Now(), http.RequestAborted))
        { return ApiProblem.Create(http, 401, "account_required"); }
        var admin = await (from membership in database.UserRoles join role in database.Roles on membership.RoleId equals role.Id
            where membership.UserId == accountId && (role.Name == AccountRoles.Owner || role.Name == AccountRoles.Administrator)
            select membership.UserId).AnyAsync(http.RequestAborted);
        if (!AccountRoles.IsAdmin(http.User) || !admin) { return ApiProblem.Create(http, 403, "account_admin_required"); }
        if (!HttpMethods.IsPut(http.Request.Method)) { return null; }
        var recent = await AccountAuthorization.RequireRecentAsync(http);
        if (recent is not null) { return recent; }
        if (http.Request.Headers["X-DV-Confirm"] != "yes") { return ApiProblem.Create(http, 409, "confirmation_required"); }
        var accounts = http.RequestServices.GetRequiredService<AccountConfiguration>();
        if (!http.Request.IsHttps || !accounts.AllowedOrigins.Contains(http.Request.Headers.Origin.ToString(), StringComparer.Ordinal))
        { return ApiProblem.Create(http, 403, "account_origin_required"); }
        try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
        catch (AntiforgeryValidationException) { return ApiProblem.Create(http, 400, "csrf_required"); }
        return null;
    }
}
