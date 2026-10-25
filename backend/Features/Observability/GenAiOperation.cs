using System.Diagnostics;
using System.Text.Json;

namespace Kufr.Backend.Features.Observability;

internal enum GenAiPhase { Connecting, ProviderHandshake, Configuring, Ready, Input, Response, Reconnecting, Closing, Completed }
internal enum GenAiDisconnectCause { Disposed, VisualRevoked, Completed, Cancelled, Timeout, TransportError, ProtocolError }

// This boundary deliberately accepts metadata only. Never record provider bodies,
// exception messages, audio, prompts, transcripts, tool arguments or identifiers.
internal sealed class GenAiOperation : IDisposable
{
    private readonly Activity? activity;
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly bool streaming;
    private long chunks;
    private long bytes;
    private int firstOutput;
    private bool failed;
    private GenAiPhase? phase;
    private long phaseStarted;
    private bool ready;

    private GenAiOperation(string operation, string? model, bool streaming)
    {
        this.streaming = streaming;
        activity = BackendTelemetry.Source.StartActivity("gen_ai." + operation, ActivityKind.Client);
        activity?.SetTag("gen_ai.operation.name", operation switch
        {
            "responses" or "realtime" or "live" => "chat",
            "image_generation" or "image_edit" or "keepsake" => "generate_content",
            _ => operation
        });
        activity?.SetTag("gen_ai.provider.name", "openai");
        activity?.SetTag("gen_ai.request.model", SafeModel(model));
        activity?.SetTag("app.outcome", "completed");
    }

    internal static GenAiOperation Start(string operation, string? model = null, bool streaming = false) =>
        new(operation, model, streaming);

    internal void SetPhase(GenAiPhase value)
    {
        if (activity is null) { return; }
        if (phase == value) { return; }
        if (phase is GenAiPhase.ProviderHandshake or GenAiPhase.Configuring)
        {
            var tag = phase == GenAiPhase.ProviderHandshake ? "app.stream.handshake_ms" : "app.stream.configuration_ms";
            activity.SetTag(tag, Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds);
        }
        phase = value;
        phaseStarted = Stopwatch.GetTimestamp();
        activity.SetTag("app.phase", value switch
        {
            GenAiPhase.Connecting => "connecting",
            GenAiPhase.ProviderHandshake => "provider_handshake",
            GenAiPhase.Configuring => "configuring",
            GenAiPhase.Ready => "ready",
            GenAiPhase.Input => "input",
            GenAiPhase.Response => "response",
            GenAiPhase.Reconnecting => "reconnecting",
            GenAiPhase.Closing => "closing",
            GenAiPhase.Completed => "completed",
            _ => null
        });
        if (value == GenAiPhase.Ready && !ready)
        {
            ready = true;
            activity.SetTag("app.stream.ready_ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    internal void SetAttempt(int attempt)
    {
        if (attempt > 0) { activity?.SetTag("app.attempt", attempt); }
    }

    internal void Disconnect(GenAiDisconnectCause cause) => activity?.SetTag("app.stream.disconnect_cause", cause switch
    {
        GenAiDisconnectCause.Disposed => "disposed",
        GenAiDisconnectCause.VisualRevoked => "visual_revoked",
        GenAiDisconnectCause.Completed => "completed",
        GenAiDisconnectCause.Cancelled => "cancelled",
        GenAiDisconnectCause.Timeout => "timeout",
        GenAiDisconnectCause.TransportError => "transport_error",
        GenAiDisconnectCause.ProtocolError => "protocol_error",
        _ => null
    });

    internal static async Task<T> RunAsync<T>(string operation, string? model,
        Func<GenAiOperation, Task<T>> action, CancellationToken token)
    {
        using var telemetry = Start(operation, model);
        try { return await action(telemetry); }
        catch (Exception error) { telemetry.Fail(error, token); throw; }
    }

    internal void SetUsage(long? input, long? output)
    {
        if (input >= 0) { activity?.SetTag("gen_ai.usage.input_tokens", input.Value); }
        if (output >= 0) { activity?.SetTag("gen_ai.usage.output_tokens", output.Value); }
    }

    internal void SetBillableSeconds(double seconds)
    {
        if (double.IsFinite(seconds) && seconds >= 0)
        { activity?.SetTag("app.ai.billable_seconds", seconds); }
    }

    internal void SetHttpStatus(int status)
    {
        if (status is >= 100 and <= 599) { activity?.SetTag("http.response.status_code", status); }
    }

    internal void ReadUsage(JsonElement response)
    {
        if (activity is null) { return; }
        if (response.ValueKind != JsonValueKind.Object) { return; }
        if (response.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
        { activity?.SetTag("gen_ai.response.model", SafeModel(model.GetString())); }
        if (response.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            SetUsage(ReadCount(usage, "input_tokens"), ReadCount(usage, "output_tokens"));
        }
    }

    internal void ReadUsage(BinaryData response)
    {
        if (activity is null) { return; }
        // Image responses contain large base64 fields. Scan past those values
        // without constructing another DOM or allocating any image strings.
        try
        {
            var reader = new Utf8JsonReader(response.ToMemory().Span);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) { return; }
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isUsage = reader.ValueTextEquals("usage");
                var isModel = reader.ValueTextEquals("model");
                if (!reader.Read()) { return; }
                if (isModel && reader.TokenType == JsonTokenType.String)
                { activity.SetTag("gen_ai.response.model", SafeModel(reader.GetString())); }
                else if (isUsage && reader.TokenType == JsonTokenType.StartObject)
                {
                    using var usage = JsonDocument.ParseValue(ref reader);
                    SetUsage(ReadCount(usage.RootElement, "input_tokens"), ReadCount(usage.RootElement, "output_tokens"));
                }
                else { reader.Skip(); }
            }
        }
        catch (JsonException) { /* Optional metadata never changes provider behavior. */ }
    }

    internal void OutputChunk(int count = 0)
    {
        if (activity is null) { return; }
        if (Interlocked.Exchange(ref firstOutput, 1) == 0)
        { activity?.SetTag("app.stream.first_output_ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
        Interlocked.Increment(ref chunks);
        if (count > 0) { Interlocked.Add(ref bytes, count); }
    }

    internal void Tool(string name)
    {
        if (name is "propose_wand" or "end_conversation")
        {
            activity?.SetTag("app.tool.name", name);
            activity?.SetTag("app.tool.outcome", "validated");
        }
    }

    internal void Fail(Exception error, CancellationToken token, bool cancelled = false)
    {
        // Preserve the original transport classification if a feature maps it
        // to its own safe domain exception before unwinding this boundary.
        if (failed) { return; }
        failed = true;
        var outcome = cancelled ? "cancelled" : error is OperationCanceledException cancellation
            ? token.IsCancellationRequested || token.CanBeCanceled && cancellation.CancellationToken == token ? "cancelled" : "timeout"
            : error is TimeoutException ? "timeout" : "error";
        activity?.SetTag("app.outcome", outcome);
        activity?.SetTag("error.type", error.GetType().FullName);
        if (streaming || error is System.Net.WebSockets.WebSocketException)
        {
            if (outcome == "cancelled") { Disconnect(GenAiDisconnectCause.Cancelled); }
            else if (outcome == "timeout") { Disconnect(GenAiDisconnectCause.Timeout); }
            else if (error is System.Net.WebSockets.WebSocketException) { Disconnect(GenAiDisconnectCause.TransportError); }
            else if (error is InvalidDataException or JsonException) { Disconnect(GenAiDisconnectCause.ProtocolError); }
        }
        if (error is System.ClientModel.ClientResultException providerError)
        { SetHttpStatus(providerError.Status); }
        if (outcome != "cancelled") { activity?.SetStatus(ActivityStatusCode.Error); }
    }

    public void Dispose()
    {
        if (activity is null) { return; }
        if (streaming)
        {
            if (!failed) { SetPhase(GenAiPhase.Completed); }
            activity?.SetTag("app.stream.output_chunks", Interlocked.Read(ref chunks));
            activity?.SetTag("app.stream.output_bytes", Interlocked.Read(ref bytes));
            activity?.SetTag("app.stream.duration_ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        activity?.Dispose();
    }

    private static long? ReadCount(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var count) && count >= 0 ? count : null;

    private static string? SafeModel(string? model) =>
        model is { Length: > 0 and <= 100 }
        && (model.StartsWith("gpt-", StringComparison.Ordinal) || model.StartsWith("o1", StringComparison.Ordinal)
            || model.StartsWith("o3", StringComparison.Ordinal) || model.StartsWith("o4", StringComparison.Ordinal)
            || model.StartsWith("text-embedding-", StringComparison.Ordinal))
        && model.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_')
            ? model : null;
}
