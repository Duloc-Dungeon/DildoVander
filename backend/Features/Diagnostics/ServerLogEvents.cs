using System.Diagnostics;
using Microsoft.AspNetCore.Routing;
using Kufr.Backend.Features.Observability;

namespace Kufr.Backend.Features.Diagnostics;

internal static class ServerLogEvents
{
    internal static async Task RequestAsync(HttpContext context, RequestDelegate next, ServerLogStore logs)
    {
        context.TraceIdentifier = Guid.NewGuid().ToString();
        context.Response.Headers["X-Request-Id"] = context.TraceIdentifier;
        var activity = Activity.Current;
        if (activity?.IdFormat == ActivityIdFormat.W3C)
        {
            context.Response.Headers["X-Trace-Id"] = activity.TraceId.ToHexString();
            activity.SetTag("app.request_id", context.TraceIdentifier);
        }
        var start = Stopwatch.GetTimestamp();
        using var business = RequestOperation.Name(context) is { } name ? BackendTelemetry.StartOperation(name) : null;
        string? failure = null;
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        { failure = "client_disconnected"; throw; }
        catch (Exception) { failure = "unhandled_error"; throw; }
        finally
        {
            if (failure is not null)
            {
                activity?.SetTag("error.type", failure);
                activity?.SetTag("app.outcome", failure);
                if (failure != "client_disconnected") { activity?.SetStatus(ActivityStatusCode.Error); }
            }
            else
            {
                activity?.SetTag("app.outcome", context.Response.StatusCode >= 400 ? "rejected" : "completed");
            }
            activity?.SetTag("app.operation_id", Operation(context));
            activity?.SetTag("app.conversation_id", Conversation(context));
            var route = Route(context);
            var status = failure == "unhandled_error" ? 500
                : context.RequestAborted.IsCancellationRequested ? 499 : context.Response.StatusCode;
            business?.SetTag("app.request_id", context.TraceIdentifier);
            business?.SetTag("app.operation_id", Operation(context));
            business?.SetTag("app.conversation_id", Conversation(context));
            business?.SetTag("app.outcome", failure ?? (status >= 400 ? "rejected" : "completed"));
            business?.SetTag("app.failure_code", context.Items["diagnosticCode"] as string);
            business?.SetTag("app.phase", status is 401 or 403 ? "authorization" : status == 400 ? "validation" : "execution");
            business?.SetTag("http.response.status_code", status);
            if (status >= 500) { business?.SetStatus(ActivityStatusCode.Error); }
            // Polling/PCM uploads and the log reader itself would overwhelm the
            // useful history. Their failures remain visible.
            var sessionCheck = route == "/app/v1/session" && context.Request.Method == "GET";
            var quiet = context.Request.Method == "GET" && !sessionCheck || route.EndsWith("/{streamId}/audio", StringComparison.Ordinal);
            var eventCode = (route, context.Request.Method) switch
            {
                ("/app/v1/passkeys/sign-in", "POST") => status < 400 ? "account_sign_in_completed" : "account_sign_in_failed",
                ("/app/v1/session", "DELETE") => status < 400 ? "account_sign_out_completed" : "account_sign_out_failed",
                ("/app/v1/session", "GET") => "account_session_checked",
                _ => "http_request"
            };
            if (status >= 400 || !quiet)
                logs.Write(status >= 500 ? "error" : status >= 400 ? "warning" : "info",
                    eventCode, context.TraceIdentifier, route, Method(context), status,
                    Math.Round(Stopwatch.GetElapsedTime(start).TotalMilliseconds, 1),
                    Operation(context), Conversation(context), failure ?? context.Items["diagnosticCode"] as string
                        ?? (sessionCheck ? context.User.Identity?.IsAuthenticated == true ? "authenticated" : "anonymous" : null));
        }
    }

    internal static void Stream(HttpContext context, object frame)
    {
        // Read only the named scalar properties of our own stream envelopes.
        // Never inspect delta, text, result, correlation or provider exceptions.
        var type = frame.GetType();
        var name = type.GetProperty("type")?.GetValue(frame) as string;
        if (name is not ("started" or "completed" or "listening" or "speech_stopped" or "failed" or "closed")) return;
        var operation = Guid.TryParse(type.GetProperty("operationId")?.GetValue(frame)?.ToString(), out var id) ? id.ToString() : null;
        var code = type.GetProperty("code")?.GetValue(frame) as string;
        context.RequestServices.GetService<ServerLogStore>()?.Write(name == "failed" ? "warning" : "info",
            "realtime_" + name, context.TraceIdentifier, Route(context), Method(context),
            operationId: operation, conversationId: Conversation(context), code: code);
    }

    private static string Route(HttpContext context) =>
        ((context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(unmatched)")
            .Replace("{streamId:guid}", "{streamId}", StringComparison.Ordinal);
    private static string Method(HttpContext context) => context.Request.Method is "GET" or "POST" or "PUT" or "DELETE" or "OPTIONS"
        ? context.Request.Method : "OTHER";
    private static string? Identity(HttpContext context) => Guid.TryParse(context.Request.RouteValues["id"]?.ToString(), out var id) ? id.ToString() : null;
    private static string? Operation(HttpContext context) => Guid.TryParse(context.Items["diagnosticOperationId"]?.ToString(), out var id)
        ? id.ToString() : context.Request.Path.StartsWithSegments("/v1/operations") ? Identity(context) : null;
    private static string? Conversation(HttpContext context) => context.Request.Path.StartsWithSegments("/v1/conversations") ? Identity(context) : null;
}
