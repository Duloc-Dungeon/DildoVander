using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Kufr.Backend.Features.Observability;

// General ILogger input is untrusted. Retain technical classification and UUID
// correlation only, without invoking formatters, serializing state or walking exceptions.
internal sealed class SafeApplicationLogProvider(SafeServerLogTelemetry telemetry) : ILoggerProvider, ISupportExternalScope
{
    private readonly SafeServerLogTelemetry sink = telemetry;
    private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();
    private static readonly HashSet<string> Categories = typeof(BackendTelemetry).Assembly.GetTypes()
        .Select(type => type.FullName).OfType<string>().ToHashSet(StringComparer.Ordinal);

    public ILogger CreateLogger(string categoryName) => new SafeLogger(this,
        Categories.Contains(categoryName) ? categoryName
            : categoryName.StartsWith("Microsoft.", StringComparison.Ordinal) ? "framework" : "application");
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;
    public void Dispose() { }

    private sealed class SafeLogger(SafeApplicationLogProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner.scopes.Push(state);
        // Framework request/transport info can run per PCM upload or packet.
        public bool IsEnabled(LogLevel level) => owner.sink.IsEnabled(level)
            && (category != "framework" || level >= LogLevel.Warning);

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) { return; }
            try
            {
                // Known structured failures are already exported through ServerLogStore.
                if (level >= LogLevel.Warning && AlreadyCaptured(category, state)) { return; }
                var correlation = new Dictionary<string, object?>();
                owner.scopes.ForEachScope(static (scope, fields) => AddCorrelation(scope, fields), correlation);
                AddCorrelation(state, correlation);
                owner.sink.WriteApplication(level, category, eventId, Classify(exception), correlation);
            }
            catch (Exception) { /* No arbitrary scope/state may break application logging. */ }
        }
    }

    private static void AddCorrelation(object? state, Dictionary<string, object?> fields)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> values) { return; }
        foreach (var pair in values.Take(32))
        {
            var name = pair.Key switch
            {
                "RequestId" or "TraceId" or "app.request_id" => "app.request_id",
                "OperationId" or "app.operation_id" => "app.operation_id",
                "ConversationId" or "app.conversation_id" => "app.conversation_id",
                _ => null
            };
            // Framework TraceId scopes can contain 32-hex W3C IDs. Require UUID
            // separators so they cannot overwrite the distinct request UUID.
            if (name is not null && pair.Value is string text && Guid.TryParseExact(text, "D", out var id))
            { fields[name] = id.ToString(); }
            else if (name is not null && pair.Value is Guid guid) { fields[name] = guid.ToString(); }
        }
    }

    private static bool AlreadyCaptured<TState>(string category, TState state)
    {
        if (category is not ("Kufr.Backend.Features.Conversations.ConversationStore"
            or "Kufr.Backend.Features.TestingSessions.TestingCharacterEnrichmentWorker"
            or "Kufr.Backend.Features.Customers.CharacterEnrichmentWorker")
            || state is not IEnumerable<KeyValuePair<string, object?>> values) { return false; }
        return values.Take(32).Any(pair => pair.Key == "{OriginalFormat}" && pair.Value is string template && template is
            "Text dialog failed for operation {OperationId}." or "Could not record failure for operation {OperationId}."
            or "Voice provider failed for operation {OperationId}." or "Could not record voice failure for operation {OperationId}."
            or "Testing character enrichment worker failed ({ErrorType})." or "Character worker operation failed ({ErrorType}).");
    }

    private static string? Classify(Exception? error) => error switch
    {
        null => null,
        OperationCanceledException => "cancelled",
        TimeoutException => "timeout",
        HttpRequestException => "provider_transport",
        JsonException => "invalid_json",
        SqliteException => "database_error",
        IOException => "io_error",
        UnauthorizedAccessException => "access_denied",
        ArgumentException => "argument_error",
        InvalidOperationException => "invalid_operation",
        _ => "application_error"
    };
}
