# Architecture Analysis

## Structure Overview

| Project | Role |
|---------|------|
| **BuildPipeline.Orchestrator** | Temporal worker host, workflow definitions, activities, config |
| **BuildPipeline.Client** | CLI to trigger workflow runs |
| **BuildPipeline.Orchestrator.Tests** | xUnit test project (stub only) |

## Strengths

1. **Separation of concerns** — Clean split between orchestration (`Workflows/`), work execution (`Activities/`), configuration (`Config/`), and infrastructure (`Infrastructure/`). The `IPipelineActivities` interface abstracts activities from implementation, enabling testability.

2. **Immutable data models** — `Models.cs` uses C# `record` types throughout (`PipelineWorkflowInput`, `ProjectMetadata`, `PlatformBuildInput`, etc.), idiomatic for Temporal payloads and enforcing immutability.

3. **DI and hosting** — `Program.cs` uses `Microsoft.Extensions.Hosting` properly, with `TemporalWorkerHost` registered as an `IHostedService`. Activities are injected via the interface, making them mockable.

4. **Config from environment** — `PipelineConfig` loads all settings from environment variables with sensible defaults, following 12-factor principles.

## Issues

### 1. Client depends on Orchestrator project directly

`BuildPipeline.Client.csproj` has a `ProjectReference` to the Orchestrator. The client ships with all worker code, activities, and their dependencies. In production, the client only needs the workflow type signature and models. A shared contracts/models library would decouple these.

### 2. `TemporalWorkerHost.StopAsync` doesn't dispose properly

`StopAsync` sets fields to null but doesn't cancel the running `ExecuteAsync` task or dispose the `TemporalClient`. The worker's `ExecuteAsync` is fire-and-forgotten (`_ = _worker.ExecuteAsync(...)`) with no handle retained, so there's no graceful shutdown path. In-flight activities could be orphaned.

### 3. No cancellation propagation

The `CancellationToken` from `StartAsync` is forwarded to `ExecuteAsync`, but since the task isn't awaited, worker exceptions (e.g., connection loss) are silently swallowed. A `BackgroundService` pattern or storing/awaiting the task in `StopAsync` would be safer.

### 4. Config path resolution is fragile

`PipelineConfig` computes the Unity project path with five levels of `..` from the assembly output directory. This breaks if the build output structure changes (e.g., single-file publish, different output path). `Assembly.Location` also returns empty for single-file deployments.

### 5. Workflow and activities are unimplemented

`PipelineWorkflow.RunAsync` and all three activities throw `NotImplementedException`. Expected for a starter, but the skeleton is well-structured for implementation.

### 6. Tests are skipped stubs

Two tests exist, both `Skip`ped, returning `Task.CompletedTask`. No use of Temporal's `WorkflowEnvironment` test infrastructure.

### 7. `PipelineRunSummary` has a model ordering issue

`AndroidBuild` and `iOSBuild` are nullable (build only one platform), but `ReportPath` is non-nullable and required at construction. This creates a chicken-and-egg problem: the summary must exist to generate the report, but the report path must exist to create the summary.

### 8. No observability hooks

No structured logging enrichment, metrics, or tracing beyond Temporal's built-in UI.

### 9. No atomic file writes

`FileSystemUtilities.WriteJsonFileAsync` has no file locking or atomic write pattern (write-to-temp-then-rename), which matters if retried activities write to the same path.

## Additions

Cross-referencing the in-code TODOs against the challenge brief surfaces the following gaps — things the challenge explicitly asks for that have no TODO or scaffolding in the starter.

### TODO Inventory

| # | Location | Summary |
|---|----------|---------|
| T1 | `PipelineActivities.cs:18` | Validate Unity project (inspect path, confirm assets/settings, gather metadata) |
| T2 | `PipelineActivities.cs:25` | Execute platform build via Unity CLI, idempotent, persist artifacts, emit telemetry |
| T3 | `PipelineActivities.cs:36` | Generate report (workflow metadata, artifact paths, timings, diagnostics) |
| T4 | `Program.cs:24` | Wire up logging/telemetry providers |
| T5 | `PipelineWorkflow.cs:13` | Tune activity timeouts and retry policies |
| T6 | `PipelineWorkflow.cs:24` | Implement orchestration logic (validate → decide platforms → build → aggregate → report) |
| T7 | `PipelineWorkflowTests.cs:8` | Add workflow execution tests for full pipeline run |
| T8 | `PipelineWorkflowTests.cs:15` | Add test for platform-specific builds |

### Gaps vs Challenge Requirements

| # | Challenge Requirement | Gap |
|---|----------------------|-----|
| G1 | **Error classification** — "different error types might warrant different retry strategies" | No custom exception types for transient vs permanent failures. No scaffolding to distinguish between a missing Unity project (permanent) and a CLI timeout (transient). |
| G2 | **Differentiated retry policies** — "aggressive retries with backoff for transient issues, fail-fast for permanent problems" | Single `RetryPolicy { MaximumAttempts = 3 }` applied uniformly. Validation should fail fast; builds need longer timeouts and backoff. No per-activity override. |
| G3 | **Parallel platform builds** — "orchestrate" + T6 mentions "potentially in parallel" | No fan-out/fan-in scaffolding. Building both platforms concurrently is a natural fit but has no structure. |
| G4 | **Metrics collection** — "capturing metrics on performance and reliability (execution time, success rates, retry frequency)" | T4 mentions telemetry but there are no counters, histograms, or metric exporters anywhere. No OpenTelemetry or Prometheus wiring. |
| G5 | **Structured logging** — "tracing individual workflow runs across activities" | Console logging is configured but no enrichment (correlation IDs, run ID tags, platform labels). Logs can't be correlated across activities today. |
| G6 | **Documentation** — "documentation that explains setup, configuration decisions, and operational concerns" | No README, no operational runbook, no architecture decision records. Only in-code TODOs. |
| G7 | **Unity emulation** — "emulate the behaviour in a way that still lets you showcase orchestration, retries, and reporting" | No emulation/simulation mode for environments without Unity installed. Activities throw `NotImplementedException` unconditionally. |
| G8 | **Client doesn't await result** — "generates a report" | Client starts the workflow but never polls for completion or prints the outcome. No feedback loop to the operator. |
| G9 | **No input validation** — "accept a platform parameter (android or ios)" | Client accepts any string and passes it through. No validation that the value is `android`, `ios`, or `both`. Invalid input surfaces late as a workflow failure. |
| G10 | **No failure-mode tests** — "validate your orchestration logic under different scenarios (success paths, various failure modes, retry behavior)" | Test stubs only cover happy paths conceptually. No tests for activity failures, retry exhaustion, partial platform failure, or timeout scenarios. |

## Recommendations

1. **Extract a contracts project** (`BuildPipeline.Contracts`) containing models and the workflow interface so the Client doesn't reference the full Orchestrator.
2. **Fix the worker lifecycle** — use `BackgroundService` or store the `ExecuteAsync` task and await it in `StopAsync`, wiring up a `CancellationTokenSource` for shutdown.
3. **Restructure `PipelineRunSummary`** — make `ReportPath` nullable/optional, or split into pre-report and post-report models.
4. **Add OpenTelemetry** for tracing and metrics — Temporal's .NET SDK supports interceptors for automatic activity/workflow tracing.
5. **Use a solution-relative sentinel** (like a marker file) instead of fragile `..` path traversal for locating the Unity project.
6. **Add Unity emulation mode** — a simulated activity implementation that produces fake artifacts and realistic delays, controlled by config, so the full pipeline runs without Unity installed.
7. **Classify errors** — introduce custom exception types (e.g., `TransientBuildException`, `PermanentValidationException`) and map them to distinct Temporal retry policies.
8. **Client should await and report** — poll the workflow handle for completion and print the `PipelineRunSummary` or error to the console.
9. **Validate CLI input** — reject invalid platform values at the client boundary before starting a workflow.
10. **Write a README** — cover setup, configuration, architecture decisions, and operational playbook.
