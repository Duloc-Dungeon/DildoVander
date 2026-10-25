# Honeycomb and OpenTelemetry tracing

The .NET backend supports **opt-in**, server-only OTLP/HTTP-protobuf tracing.
It is disabled by default (`Backend__Telemetry__Enabled=false`).
This uses OpenTelemetry .NET instrumentation for inbound HTTP and outbound
OpenAI HTTP requests plus custom AI/backend activities. Neither mobile app,
browser nor ESP32 gets an ingestion credential. All non-allowlisted span
attributes are removed before export: no prompts, transcripts, audio, images,
credentials, SQL parameters, customer identifiers, raw URLs, tool arguments,
exception messages or response bodies. Logs are not exported.

## Start locally

After changing NuGet dependencies regenerate **both** tracked lockfiles from
the repository root on .NET 10, then validate locked mode:

```powershell
dotnet restore backend-tests/BackendTests.csproj -p:SelfContained=true --force-evaluate
dotnet restore backend-tests/BackendTests.csproj -p:SelfContained=true --locked-mode
dotnet build backend-tests/BackendTests.csproj
dotnet run --project backend-tests/BackendTests.csproj -- --observability
```

Run an OTLP receiver / Collector locally on port 4318 and configure
the backend's **private runtime environment**:

```ini
Backend__Telemetry__Enabled=true
Backend__Telemetry__SampleRatio=1.0
OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=http://127.0.0.1:4318/v1/traces
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
```

Make an API request and a mocked AI operation; inspect received traces for
route/status/duration and safe metadata. The existing `X-Request-Id`
remains a server-generated UUID and SQLite diagnostic lookup key; the new
`X-Trace-Id` is the distinct W3C trace ID (if an activity is present).
Asynchronous work can be correlated by its operation UUID without pretending
separate background traces share a parent.

## First Honeycomb ingestion

The connected team has a `test` environment but has received no telemetry
yet. Never claim a dataset or trace URL until it is verified. Add only to
the existing private systemd `/etc/kufr-backend/backend.env`:

```ini
Backend__Telemetry__Enabled=true
Backend__Telemetry__SampleRatio=1.0
Backend__Telemetry__ServiceVersion=REPLACE_WITH_BUILD_SHA
OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=https://api.honeycomb.io/v1/traces
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_EXPORTER_OTLP_HEADERS=x-honeycomb-team=REPLACE_WITH_PRIVATE_KEY
```

For an EU Honeycomb account use its verified regional endpoint. Do not put
keys into CI, source, mobile builds, firmware, logs or public API responses.
Only restart the existing backend service after a private backup and approved
rollout. Confirm service health, trace waterfalls, dropped-data behavior and
the absence of sensitive data in Honeycomb. There is no paid-provider testing
without explicit authorization. Production defaults to 10% head sampling;
non-Production defaults to 100%. Rare errors can be lost with head sampling,
so use tail sampling if stronger rare-error capture is needed.

## Rollback

Set `Backend__Telemetry__Enabled=false` in the private service
environment and restart the backend. The API, SQLite diagnostics, persistent
data and firmware do not depend on Honeycomb availability.
