# ADR: OpenTelemetry Observability with Aspire Dashboard

**Status**: Accepted  
**Date**: 2026-03-10

## Context

A Unity build pipeline needs end-to-end visibility — from workflow trigger through validation, parallel platform builds, and report generation. Builds are long-running, involve external processes (Unity Editor in batch mode), and fan out across platforms. Without structured observability, diagnosing failures requires manually tailing logs across multiple processes and correlating timestamps by hand.

## Decision

Use OpenTelemetry (OTLP) for all three pillars — traces, metrics, and structured logs — with the .NET Aspire Dashboard as the local dev backend. Pipe Unity Editor build logs through `ILogger` so they inherit trace context. Capture warnings and errors into the output report for self-contained build diagnostics.

### Infrastructure

The Aspire Dashboard runs via `docker-compose.yml` alongside Temporal:

- **Dashboard UI**: `http://localhost:18888`
- **OTLP endpoint**: port `4317` (mapped to container port `18889`)

Set `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317` to enable. Without it, a warning is logged and only console output is emitted — no crash, no silent failure.

### Trace propagation across Temporal boundaries

Temporal's `TracingInterceptor` (from `Temporalio.Extensions.OpenTelemetry`) is registered on both the client and worker. This propagates W3C trace context across workflow → activity boundaries, so a single trace ID follows the entire pipeline run:

1. **Client** starts a workflow — creates the root span
2. **Workflow** schedules activities — child spans linked via Temporal headers
3. **Activities** (`ValidateUnityProjectAsync`, `ExecutePlatformBuildAsync`, `GenerateReportAsync`) — each gets a child span with activity-specific tags (`run.id`, `build.platform`, `build.exit_code`, `build.duration_ms`)

All spans land in the same trace in the Aspire Dashboard, giving a single view of the full pipeline execution including parallel platform builds.

### Custom metrics

Defined in `Infrastructure/Telemetry.cs`:

| Metric | Type | Description |
|--------|------|-------------|
| `pipeline.builds.total` | Counter | Total builds, tagged by `platform` and `status` |
| `pipeline.build.duration_ms` | Histogram | Build duration per platform |
| `pipeline.validations.total` | Counter | Total validations, tagged by `status` |

## What Is the Aspire Dashboard?

The .NET Aspire Dashboard is a standalone, self-hosted OpenTelemetry frontend — a single Docker container that accepts OTLP (gRPC) and renders traces, metrics, and structured logs in a browser UI. It requires no cloud account, no API keys, and no additional infrastructure.

Key properties:
- **Single container** — `mcr.microsoft.com/dotnet/aspire-dashboard:latest`
- **No authentication required for local dev** — `DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true`
- **OTLP-native** — speaks the same protocol as Grafana, Datadog, Jaeger, etc.
- **Structured log correlation** — logs emitted with an active `System.Diagnostics.Activity` span automatically appear under the correct trace

Ref: [.NET Aspire Dashboard — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/dashboard/standalone)

## Unity Build Log Piping

`TailUnityLogAsync` streams the Unity Editor log file in real time during builds. Each line is classified and forwarded through `ILogger`:

| Log line pattern | ILogger level | Rationale |
|-----------------|---------------|-----------|
| Contains `Error` or starts with `Crash` | `LogError` | Build failures, script compilation errors, crash reports |
| Contains `Warning` | `LogWarning` | Shader warnings, deprecation notices, asset import warnings |
| Everything else | `LogInformation` | Normal build progress |

Because these log calls happen inside an activity with an active `System.Diagnostics.Activity` span, every log line inherits the current trace and span IDs. This means Unity's raw build output (shader compilation warnings, script errors, asset import issues) appears in the Aspire Dashboard's structured logs view, correlated to the exact build activity span.

Each log line also triggers a Temporal heartbeat to keep long-running builds alive — the observability and liveness mechanisms share the same code path.

## Capturing Issues in the Output Report

Warnings and errors detected during log tailing are not just logged — they are also collected into a `ConcurrentBag<PipelineIssue>` and included in the build report.

| Field | Type | Description |
|-------|------|-------------|
| `PipelineIssue.Severity` | `IssueSeverity` enum | `Warning` or `Error` |
| `PipelineIssue.Message` | `string` | The raw Unity log line |
| `PipelineIssue.Source` | `string` | Platform name (e.g. `android`, `ios`) |

Each `BuildArtifactResult` carries its own `Issues` list (per-platform), and the top-level `PipelineRunSummary.Issues` aggregates all issues across platforms. This makes reports self-contained — you can identify problems without accessing the Aspire Dashboard or Temporal UI.

## Alternatives Considered

| Option | Verdict | Reason |
|--------|---------|--------|
| **Jaeger** | Viable | Excellent trace UI, but traces-only — no metrics or structured logs in one place. Would need Prometheus + Loki alongside it. |
| **Seq** | Viable | Strong structured log search, but commercial license for production. Aspire Dashboard is free and covers all three pillars. |
| **Grafana stack (Tempo + Loki + Prometheus)** | Deferred | Production-grade, but heavy for local dev — three containers + Grafana. Can layer on top later since we export standard OTLP. |
| **Console logging only** | Rejected | No correlation across workflow/activity boundaries. Cannot search or filter without `grep`. Loses trace context entirely. |

## Implementation

**Files modified**:
- `docker-compose.yml` — added `aspire-dashboard` service with OTLP port mapping
- `src/BuildPipeline.Orchestrator/Infrastructure/Telemetry.cs` — `ActivitySource`, `Meter`, and counter/histogram definitions
- `src/BuildPipeline.Orchestrator/Program.cs` — OpenTelemetry SDK registration (tracing, metrics, structured logging) with OTLP exporter, conditional on `OTEL_EXPORTER_OTLP_ENDPOINT`
- `src/BuildPipeline.Client/Program.cs` — `TracingInterceptor` on Temporal client, tracing SDK with OTLP exporter
- `src/BuildPipeline.Orchestrator/Activities/PipelineActivities.cs` — `TailUnityLogAsync` collects `PipelineIssue` entries alongside log classification; issues threaded through `RunUnityProcessAsync` → `ExecutePlatformBuildAsync` → `BuildArtifactResult`
- `src/BuildPipeline.Orchestrator/Activities/Models.cs` — `IssueSeverity` enum, `PipelineIssue` record, `Issues` field on `BuildArtifactResult` and `PipelineRunSummary`
- `src/BuildPipeline.Orchestrator/Workflows/PipelineWorkflow.cs` — aggregates per-platform issues into `PipelineRunSummary.Issues`

## Risks

| Risk | Likelihood | Mitigation |
|------|-----------|------------|
| OTLP endpoint unavailable | Low | Graceful degradation — warning logged, console output continues. No crash or data loss. |
| High-volume Unity logs cause memory pressure during issue collection | Low | `ConcurrentBag<PipelineIssue>` only stores warning/error lines, not all output. Typical builds produce tens, not thousands, of issues. |
| Log line classification misidentifies severity | Low | Simple substring matching may over-match (e.g., a file path containing "error"). Acceptable for surfacing — not used for pass/fail decisions. |
| Aspire Dashboard container not started | None | `OTEL_EXPORTER_OTLP_ENDPOINT` must be explicitly set. Without it, telemetry is disabled with a clear warning. |

## Expected Impact

- **Single-pane debugging** — one trace view in Aspire shows the full pipeline: validation → parallel builds → report, with Unity logs inline
- **Self-contained reports** — build output JSON includes all warnings and errors without external tool access
- **Zero-config local dev** — `docker-compose up` starts Aspire alongside Temporal; `OTEL_EXPORTER_OTLP_ENDPOINT` is the only knob
- **Production-portable** — swap Aspire for any OTLP backend by changing one environment variable
