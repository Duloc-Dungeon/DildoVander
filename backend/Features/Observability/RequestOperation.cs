namespace Kufr.Backend.Features.Observability;

internal static class RequestOperation
{
    // Fixed operation names complement the HTTP route. No URL segments or user
    // supplied action names become span names, and polling/PCM has no extra span.
    internal static string? Name(HttpContext context)
    {
        if (context.Request.Method is "GET" or "HEAD" or "OPTIONS" || !BackendTelemetry.ShouldTraceRequest(context)) { return null; }
        var path = context.Request.Path;
        if (path.StartsWithSegments("/app/v1/customer/character")) { return "character.api"; }
        if (path.StartsWithSegments("/app/v1/testing-sessions")) { return "testing.session.api"; }
        if (path.StartsWithSegments("/app/v1/persona-lab")) { return "persona_lab.api"; }
        if (path.StartsWithSegments("/app/v1/passkeys") || path.StartsWithSegments("/app/v1/session")) { return "account.authentication"; }
        if (path.StartsWithSegments("/app/v1")) { return "account.operation"; }
        if (path.StartsWithSegments("/v1/conversations")) { return "conversation.api"; }
        if (path.StartsWithSegments("/v1/show")) { return "show.operation"; }
        if (path.StartsWithSegments("/v1/keepsakes")) { return "keepsake.api"; }
        if (path.StartsWithSegments("/v1/twin")) { return "device.twin.api"; }
        if (path.StartsWithSegments("/v1/devices") || path.StartsWithSegments("/v1/operations")) { return "device.operation"; }
        return null;
    }
}
