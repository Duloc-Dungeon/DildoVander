using Kufr.Backend.Features.Diagnostics;
using Microsoft.AspNetCore.Routing;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;

namespace Kufr.Backend.Features.Observability;

// A dedicated pipeline, deliberately absent from the application's ILogger providers.
// Only explicit projections can reach OTLP; no raw formatter, exception or scope is accepted.
internal sealed class SafeServerLogTelemetry : IDisposable
{
    private readonly ServiceProvider pipeline;
    private readonly ILogger logger;
    private readonly LoggerProvider provider;
    private readonly EndpointDataSource? endpoints;
    private readonly LogLevel minimum;

    private static readonly HashSet<string> Events = new(StringComparer.Ordinal)
    {
        "http_request", "account_sign_in_completed", "account_sign_in_failed", "account_sign_out_completed",
        "account_sign_out_failed", "account_session_checked", "dialog_failed", "dialog_journal_failed",
        "voice_failed", "voice_journal_failed", "testing_character_worker_failed", "character_worker_failed",
        "character_job_started", "character_job_completed", "character_job_discarded", "character_job_failed",
        "character_job_retry_scheduled", "testing_character_job_requested", "testing_character_job_started",
        "testing_character_job_completed", "testing_character_job_discarded", "testing_character_job_failed",
        "testing_character_job_retry_scheduled", "testing_character_correlation_failed", "testing_character_queue_delay",
        "testing_character_provider_completed", "realtime_started", "realtime_completed", "realtime_listening",
        "realtime_speech_stopped", "realtime_failed", "realtime_closed", "lab_operation_admitted",
        "lab_connection_started", "lab_provider_session", "lab_turn_finalizing", "lab_provider_failure",
        "lab_connection_interrupted", "lab_admission_validated", "lab_client_playback_failure",
        "lab_connection_established", "lab_context_prepared", "lab_context_trimmed", "lab_first_response",
        "lab_image_context", "lab_operation_released", "lab_provider_cost", "lab_provider_interrupt",
        "lab_provider_tokens", "lab_provider_turn", "lab_provider_usage", "lab_provider_usage_final",
        "lab_recovery_completed", "lab_recovery_started", "lab_request_cancelled", "lab_request_rejected",
        "lab_session_ended", "lab_tool_requested", "lab_tool_result", "lab_turn_persisted"
    };

    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
    {
        "anonymous", "authenticated", "unhandled_error", "client_disconnected", "character_job_interrupted",
        "character_provider_timeout", "character_provider_transport_error", "character_provider_invalid_response",
        "character_provider_output_limit", "character_provider_incomplete", "character_provider_refused",
        "character_provider_configuration_error", "character_provider_unavailable", "openai_consent_required", "character_not_found",
        "character_provider_error", "cancelled", "timeout", "provider_transport", "operation_failed",
        "unauthorized", "forbidden", "invalid_request", "not_found", "rate_limited", "input_revoked",
        "user_cancelled", "provider_error", "transport_error", "unexpected_close", "invalid_response",
        "preparing_context", "ready", "connected", "completed", "persisting_memory", "explicit_retry",
        "provider_ready", "oldest_turns_omitted", "received", "live", "realtime", "transcript",
        "client_delegation", "real_backend_virtual_hardware", "simulated_failure", "simulation_only",
        "validated_simulation", "observed", "live_seconds", "micro_usd", "realtime_cancelled",
        "live_local_generation_fence", "provider_invalid_error_event", "provider_unknown_parameter", "provider_immutable_field",
        "provider_capacity", "provider_invalid_delegation", "provider_rejected_command", "provider_server_error",
        "provider_moderation", "provider_error_unknown", "live_cancelled", "decision_provider_capacity",
        "decision_provider_auth", "decision_provider_server", "decision_provider_rejected", "decision_usage_unconfirmed",
        "live_socket_failed", "live_memory_storage_failed", "live_event_json_invalid", "live_event_format_invalid",
        "live_event_field_missing", "decision_invalid_evidence", "decision_incomplete", "decision_ambiguous",
        "decision_invalid_fields", "live_event_timing_invalid", "live_finalization_timeout", "live_usage_regressed",
        "live_event_unsupported", "live_context_correlation", "live_context_limit", "live_close_interrupted",
        "live_close_unexpected", "live_final_usage_missing", "live_decision_count", "live_audio_invalid",
        "character_recovery_unverified", "character_input_repair_limit", "live_data_invalid", "live_internal_error",
        "realtime_input_buffer_limit", "realtime_event_invalid", "realtime_input_transcription_conflict",
        "realtime_response_incomplete", "realtime_session_invalid", "realtime_output_invalid", "realtime_tool_invalid"
    };

    public SafeServerLogTelemetry(IConfiguration configuration, IHostEnvironment environment,
        EndpointDataSource? endpoints = null, Action<OtlpExporterOptions>? configureExporter = null)
    {
        this.endpoints = endpoints;
        minimum = configuration["Backend:Telemetry:Logs:MinimumLevel"]?.ToLowerInvariant() switch
        {
            null => environment.IsProduction() ? LogLevel.Warning : LogLevel.Information,
            "information" or "info" => LogLevel.Information,
            "warning" => LogLevel.Warning,
            "error" => LogLevel.Error,
            _ => throw new InvalidOperationException("Telemetry log minimum level must be Information, Warning or Error.")
        };
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(minimum));
        services.AddOpenTelemetry().WithLogging(builder =>
        {
            builder.SetResourceBuilder(BackendTelemetry.CreateResource(configuration, environment));
            builder.AddOtlpExporter((exporter, processor) =>
            {
                BackendTelemetry.ConfigureExporter(exporter, configuration, "LOGS");
                configureExporter?.Invoke(exporter);
                processor.ExportProcessorType = ExportProcessorType.Batch;
                processor.BatchExportProcessorOptions = new BatchExportLogRecordProcessorOptions
                {
                    MaxQueueSize = 2048,
                    MaxExportBatchSize = 256,
                    ScheduledDelayMilliseconds = 5000,
                    ExporterTimeoutMilliseconds = 5000
                };
            });
        }, options =>
        {
            options.IncludeFormattedMessage = false;
            options.IncludeScopes = false;
        });
        pipeline = services.BuildServiceProvider();
        provider = pipeline.GetRequiredService<LoggerProvider>();
        logger = pipeline.GetRequiredService<ILoggerFactory>().CreateLogger("Kufr.Backend.Diagnostics");
    }

    internal void Write(ServerLogEntry entry)
    {
        var level = entry.Level switch { "error" => LogLevel.Error, "warning" => LogLevel.Warning, _ => LogLevel.Information };
        if (level < minimum) { return; }
        try
        {
            var fields = new List<KeyValuePair<string, object?>>
            {
                new("{OriginalFormat}", "diagnostic_event"),
                new("app.event.name", entry.EventCode.Length <= 80 && Events.Contains(entry.EventCode) ? entry.EventCode : "diagnostic_event")
            };
            AddId(fields, "app.request_id", entry.TraceId);
            AddId(fields, "app.operation_id", entry.OperationId);
            AddId(fields, "app.conversation_id", entry.ConversationId);
            if (entry.Route is { Length: <= 200 } route && endpoints?.Endpoints.OfType<RouteEndpoint>()
                .Any(endpoint => endpoint.RoutePattern.RawText?.Replace("{streamId:guid}", "{streamId}", StringComparison.Ordinal) == route) == true)
            { fields.Add(new("http.route", route)); }
            if (entry.Method is "GET" or "POST" or "PUT" or "DELETE" or "PATCH" or "OPTIONS" or "HEAD" or "OTHER")
            { fields.Add(new("http.request.method", entry.Method)); }
            if (entry.Status is >= 100 and <= 599) { fields.Add(new("http.response.status_code", entry.Status)); }
            if (entry.DurationMs is >= 0 and <= 86400000 && double.IsFinite(entry.DurationMs.Value))
            { fields.Add(new("app.duration_ms", entry.DurationMs.Value)); }
            if (entry.Attempt is >= 0 and <= 1000) { fields.Add(new("app.retry_attempt", entry.Attempt)); }
            if (entry.Code is { Length: <= 64 } code && (Codes.Contains(code) || IsProviderStatus(code)))
            { fields.Add(new("app.failure_code", code)); }
            // No exception and a constant body, even when callers pass unexpected metadata.
            logger.Log(level, default, fields, null, static (_, _) => "diagnostic_event");
        }
        catch (Exception)
        {
            // Export is best effort. Never recurse into diagnostics or interrupt a request.
        }
    }

    internal bool IsEnabled(LogLevel level) => level >= minimum && level < LogLevel.None;

    internal void WriteApplication(LogLevel level, string category, EventId eventId, string? errorType,
        IEnumerable<KeyValuePair<string, object?>> correlation)
    {
        if (!IsEnabled(level)) { return; }
        try
        {
            var fields = new List<KeyValuePair<string, object?>>
            {
                new("{OriginalFormat}", "application_log"), new("app.event.name", "application_log"),
                new("app.log.category", category), new("app.log.event_id", eventId.Id)
            };
            if (errorType is not null) { fields.Add(new("error.type", errorType)); }
            foreach (var pair in correlation) { AddId(fields, pair.Key, pair.Value as string); }
            logger.Log(level, new EventId(eventId.Id), fields, null, static (_, _) => "application_log");
        }
        catch (Exception) { /* Never let diagnostics change application behavior. */ }
    }

    private static bool IsProviderStatus(string code) => code.Length == 15 && code.StartsWith("openai_http_", StringComparison.Ordinal)
        && int.TryParse(code.AsSpan(12), out var status) && status is >= 400 and <= 599;

    private static void AddId(List<KeyValuePair<string, object?>> fields, string name, string? value)
    {
        if (Guid.TryParse(value, out var id)) { fields.Add(new(name, id.ToString())); }
    }

    internal bool ForceFlush(int timeoutMilliseconds = 5000) => provider.ForceFlush(timeoutMilliseconds);
    public void Dispose() => pipeline.Dispose();
}
