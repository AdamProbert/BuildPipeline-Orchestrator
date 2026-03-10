# ADR: Retry Strategy & Error Classification

**Status**: Accepted  
**Date**: 2026-03-10

## Context

Unity builds have several distinct failure modes — missing project files, licensing flakiness, editor crashes, I/O errors — each warranting a different response. A single retry policy cannot handle all of these well: too aggressive and you waste time retrying permanent failures, too conservative and transient glitches kill the pipeline.

## Decision

Use a **two-layer retry model** — Temporal's built-in retry policies at the activity level, plus an application-level retry loop inside `ExecutePlatformBuildAsync` for licensing-specific errors — with `InvalidOperationException` as the convention for permanent (non-retryable) failures.

### Error classification convention

All permanent failures throw `InvalidOperationException`. This is registered as a non-retryable error type on every activity that can encounter permanent issues. Generic `Exception` is used for transient/retryable failures.

| Failure | Exception Type | Retryable | Rationale |
|---------|---------------|-----------|-----------|
| Missing project directory | `InvalidOperationException` | No | Won't appear between retries |
| Missing Assets/ or ProjectSettings/ | `InvalidOperationException` | No | Structural — not transient |
| Unity editor not found | `InvalidOperationException` | No | Installation issue |
| Platform module not installed | `InvalidOperationException` | No | Requires manual install |
| License file missing | `InvalidOperationException` | No | Requires activation |
| Licensing error (runtime) | `InvalidOperationException` after exhausting inner retries | No | Escalated after 5 inner attempts |
| Unity lockfile present | `InvalidOperationException` | No | Another Unity instance running |
| Unity build crash (non-zero exit) | `Exception` | Yes | Transient editor crashes do happen |
| Report I/O failure | `Exception` | Yes | Disk/network glitches |
| Clone I/O failure | `Exception` | Yes | Transient filesystem errors |

### Per-activity retry policies

Each activity has a policy tuned to its expected failure profile:

| Activity | Timeout | Max Attempts | Backoff | Non-Retryable | Rationale |
|----------|---------|-------------|---------|---------------|-----------|
| **ValidateUnityProject** | 30s | 1 (fail-fast) | — | `InvalidOperationException` | Validation checks are deterministic. If the project is broken, retrying won't fix it. Fail-fast gives operators immediate feedback. |
| **ExecutePlatformBuild** | 30min | 3 | Exponential (2×, from 5s) | `InvalidOperationException` | Builds are long-running and Unity can crash transiently. 3 attempts with exponential backoff gives the system time to recover. 30min accommodates large projects. |
| **GenerateReport** | 60s | 2 | — | — | Report generation is fast I/O. One retry covers transient disk issues. No permanent failure classification needed — it's just file serialisation. |
| **PrepareProjectCopy** | 5min | 2 | — | `InvalidOperationException` | Copying a Unity project can take minutes for large Libraries. One retry handles transient I/O; permanent path issues fail fast. |
| **CleanupProjectCopy** | 2min | 2 | — | — | Cleanup is best-effort. No errors are marked non-retryable — if it fails twice, it's an orphaned temp directory, not a pipeline failure. |

### Heartbeat timeout

Build activities have a 5-minute heartbeat timeout. `TailUnityLogAsync` heartbeats Temporal on every Unity log line (poll interval: 1s). If the editor stops producing output for 5 minutes, Temporal cancels the activity and triggers a retry — this catches hung Unity processes without needing a separate watchdog.

### The licensing retry layer (inner loop)

Unity's licensing system is notoriously flaky in batch mode — license servers can be temporarily unreachable, license seats can momentarily be exhausted. These are transient by nature but need a different cadence than build crashes.

The inner retry loop in `ExecutePlatformBuildAsync`:

1. Run the Unity process
2. If exit code ≠ 0, check stdout/stderr for `[Licensing::` lines with failure keywords (`fail`, `error`, `unable`, `could not`)
3. If licensing error and attempts remain: wait 30s, re-invoke the process (same activity attempt)
4. If licensing error and attempts exhausted: throw `InvalidOperationException` (escalate to permanent — the licensing issue isn't going away)
5. If non-licensing error: throw generic `Exception` (Temporal may retry the whole activity)

This means a single Temporal activity attempt can internally retry licensing up to 5 times (150s of delays). Combined with 3 Temporal-level retries, the system can tolerate extended licensing outages (~7.5 minutes total) before giving up.

**Why two layers?** Temporal retries restart the entire activity — re-cloning the project, re-launching Unity. The inner loop retries just the Unity process invocation, which is much cheaper. Licensing errors almost always resolve by simply re-running the editor, so the inner loop avoids unnecessary project copy overhead.

### Partial failure policy

When building multiple platforms in parallel, if any platform fails (after exhausting retries), the entire workflow fails. This is deliberate:

- A "partially succeeded" build pipeline is operationally ambiguous — did iOS fail because of a code issue that would also affect Android?
- The report is generated only on full success, giving a clean signal
- Re-running the workflow is idempotent (isolated project copies, overwrite-safe artifacts), so retrying the whole pipeline is safe and simple

## Timeout configuration

All timeout values have sensible defaults in `TimeoutConfig.Default` but are configurable at the worker level via environment variables, loaded through `PipelineConfig`:

| Env Var | Default | Unit | Maps To |
|---------|---------|------|---------|
| `PIPELINE_VALIDATION_TIMEOUT_SECONDS` | 30 | seconds | `TimeoutConfig.ValidationTimeout` |
| `PIPELINE_BUILD_TIMEOUT_MINUTES` | 30 | minutes | `TimeoutConfig.BuildTimeout` |
| `PIPELINE_REPORT_TIMEOUT_SECONDS` | 60 | seconds | `TimeoutConfig.ReportTimeout` |
| `PIPELINE_LICENSING_MAX_RETRIES` | 5 | count | `TimeoutConfig.LicensingMaxRetries` |
| `PIPELINE_LICENSING_RETRY_DELAY_SECONDS` | 30 | seconds | `TimeoutConfig.LicensingRetryDelay` |
| `PIPELINE_BUILD_RETRY_INTERVAL_SECONDS` | 5 | seconds | `TimeoutConfig.BuildRetryInterval` |

The client reads the same env vars and passes them as `TimeoutConfig` in the workflow input, so the values flow from environment → `PipelineConfig.Timeouts` → `PipelineWorkflowInput.Timeouts` → per-activity `ActivityOptions`. This keeps timeout tuning as configuration, not code changes — operators can adjust for their hardware and project size without recompilation.

Per-run overrides are also possible since `TimeoutConfig` is part of `PipelineWorkflowInput` — a custom client could pass different values for a specific build.


## Alternatives Considered

| Option | Verdict | Reason |
|--------|---------|--------|
| Custom exception hierarchy (`TransientBuildException`, `PermanentBuildException`) | Rejected | Over-engineering for two categories. `InvalidOperationException` is idiomatic .NET for "you called this wrong / precondition violated". Adding custom types would require mapping in Temporal's `NonRetryableErrorTypes` by string name anyway. |
| Single retry policy for all activities | Rejected | Validation and builds have fundamentally different failure profiles. A policy aggressive enough for builds (3 retries, exponential backoff) wastes 30+ seconds on a missing project directory. |
| Retry licensing at the Temporal level only | Rejected | Each Temporal retry re-runs the full activity including project copy validation. The inner loop retries just the Unity process, which is the minimal retry scope for licensing. |

## Implementation

**Files**:
- `src/BuildPipeline.Orchestrator/Config/PipelineConfig.cs` — loads timeout env vars into `TimeoutConfig`, passes to workflow input via client
- `src/BuildPipeline.Orchestrator/Workflows/PipelineWorkflow.cs` — per-activity `ActivityOptions` with differentiated `RetryPolicy` and `NonRetryableErrorTypes`
- `src/BuildPipeline.Orchestrator/Activities/PipelineActivities.cs` — `InvalidOperationException` for permanent failures, inner licensing retry loop, heartbeat integration
- `src/BuildPipeline.Orchestrator/Activities/Models.cs` — `TimeoutConfig` record with configurable values and sensible defaults
- `src/BuildPipeline.Client/Program.cs` — passes `config.Timeouts` into `PipelineWorkflowInput`
- `tests/BuildPipeline.Orchestrator.Tests/PipelineWorkflowTests.cs` — tests for validation failure (non-retryable), build failure (retryable), and partial platform failure
