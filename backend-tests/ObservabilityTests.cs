using System.Diagnostics;
using Kufr.Backend.Features.Observability;
using Microsoft.AspNetCore.Http;

internal static class ObservabilityTests
{
    internal static void Run()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BackendTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        using var span = BackendTelemetry.StartOpenAi("gpt-6.1-sol");
        Require(span is not null, "span created for listener");
        span.SetTag("gen_ai.input.messages", "private");
        span.SetTag("gen_ai.tool.call.arguments", "private");
        span.SetTag("url.full", "https://api.openai.com/?key=private");
        span.SetTag("db.statement", "SELECT private");
        span.SetTag("http.request.header.authorization", "Bearer private");
        span.SetTag("app.outcome", "completed");
        span.SetStatus(ActivityStatusCode.Error, "private");
        new SafeSpanProcessor().OnEnd(span);
        Require(span.GetTagItem("gen_ai.input.messages") is null, "prompt stripped");
        Require(span.GetTagItem("gen_ai.tool.call.arguments") is null, "tool input stripped");
        Require(span.GetTagItem("url.full") is null, "URL stripped");
        Require(span.GetTagItem("db.statement") is null, "SQL stripped");
        Require(span.GetTagItem("http.request.header.authorization") is null, "credential stripped");
        Require(span.StatusDescription is null, "error description stripped");
        Require((string?)span.GetTagItem("gen_ai.request.model") == "gpt-6.1-sol", "safe model retained");
        Require((string?)span.GetTagItem("app.outcome") == "completed", "outcome retained");

        var context = new DefaultHttpContext();
        context.Request.Path = "/healthz";
        Require(!BackendTelemetry.ShouldTraceRequest(context), "health excluded");
        context.Request.Path = "/v1/conversations/abc/gpt-live/xyz/audio";
        context.Request.Method = "POST";
        Require(!BackendTelemetry.ShouldTraceRequest(context), "PCM excluded");
        context.Request.Method = "GET";
        Require(BackendTelemetry.ShouldTraceRequest(context), "other requests traced");
        context.Request.Path = "/v1/conversations";
        Require(BackendTelemetry.ShouldTraceRequest(context), "normal operations traced");
        Console.WriteLine("Observability tests passed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
