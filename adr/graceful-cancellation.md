# ADR: Graceful Unity Process Cleanup on Workflow Termination

**Status**: Proposed  
**Date**: 2026-03-10

## Context

When a Temporal workflow is terminated or cancelled, the Unity Editor process launched by `ExecutePlatformBuildAsync` continues running as an orphaned OS process. `RunUnityProcessAsync` calls `process.WaitForExitAsync()` without a `CancellationToken`, so the activity never reacts to Temporal's cancellation signal. The Unity process (`-batchmode -nographics`) runs indefinitely, holding project locks (`Temp/UnityLockfile`, `Library/ArtifactDB-lock`) and consuming resources.

Additionally, the workflow-level cleanup in `BuildWithIsolatedProjectAsync` uses `Workflow.ExecuteActivityAsync` in its `finally` block. During cancellation, the workflow's cancellation token is already fired, so the cleanup activity may fail to schedule — leaving temporary project copies on disk.

## Decision

Thread Temporal's activity cancellation token through the process lifecycle so that termination kills the Unity Editor and triggers cleanup.

### Activity-level: Kill the process

`RunUnityProcessAsync` accepts a `CancellationToken` sourced from `ActivityExecutionContext.Current.CancellationToken`. On cancellation:

1. `process.WaitForExitAsync(ct)` throws `OperationCanceledException`
2. Catch block calls `process.Kill(entireProcessTree: true)` — kills Unity and all child processes
3. Re-throw so Temporal sees the cancellation

The cancellation token also flows into the licensing retry `Task.Delay`, aborting retries immediately.

### Workflow-level: Detached cleanup

The `finally` block in `BuildWithIsolatedProjectAsync` schedules `CleanupProjectCopyAsync` with `CancellationToken = CancellationToken.None` on the `ActivityOptions`, so it dispatches even when the workflow is cancelled. This mirrors the existing `finally` pattern but makes it cancellation-safe.

## Why `entireProcessTree: true`?

Unity in batch mode may spawn child processes (shader compiler workers, IL2CPP, Android SDK tools). Killing only the parent leaves these orphaned. `Process.Kill(entireProcessTree: true)` (.NET 5+) terminates the full tree.

## Heartbeat dependency

`TailUnityLogAsync` already calls `ActivityExecutionContext.Current.Heartbeat()` on every log line. This is critical — Temporal delivers cancellation signals via heartbeat responses. Without heartbeats, the activity wouldn't learn about termination until the `HeartbeatTimeout` (currently 5 minutes) expires.

## Implementation

**Files to modify**:
- `src/BuildPipeline.Orchestrator/Activities/PipelineActivities.cs` — `RunUnityProcessAsync`: add `CancellationToken` param, kill on cancel. `ExecutePlatformBuildAsync`: pass `ActivityExecutionContext.Current.CancellationToken` through.
- `src/BuildPipeline.Orchestrator/Activities/SimulatedPipelineActivities.cs` — `ExecutePlatformBuildAsync`: pass cancellation token to `Task.Delay`.
- `src/BuildPipeline.Orchestrator/Workflows/PipelineWorkflow.cs` — `BuildWithIsolatedProjectAsync`: detached cancellation on cleanup activity.
- `tests/BuildPipeline.Orchestrator.Tests/PipelineWorkflowTests.cs` — cancellation test verifying cleanup runs.

## Risks

| Risk | Likelihood | Mitigation |
|------|-----------|------------|
| Unity corrupts project on hard kill | Very low | Builds use isolated project copies; originals are untouched. Junctioned dirs are read-only in batch mode. |
| Cleanup activity fails to schedule during cancellation | Low | Detached cancellation token on cleanup options. Fallback: OS temp directory cleanup. |
| Heartbeat gap delays cancellation delivery | Low | Log tailing heartbeats every line; poll interval is 1s. Worst case delay ≈ `HeartbeatTimeout` (5 min). |

## Scope

**Included**: Process kill on cancel/terminate, detached cleanup scheduling, simulated activity cancellation, test coverage.  
**Excluded**: Process kill on worker shutdown (already handled by `TemporalWorkerHost.StopAsync` cancelling the worker token).