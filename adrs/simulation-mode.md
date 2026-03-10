# ADR: Simulation Mode & Unity Abstraction

**Status**: Accepted  
**Date**: 2026-03-10

## Context

Unity is a heavyweight runtime dependency — it requires a license, platform-specific build modules, and gigabytes of disk space. Not every development machine or CI runner has it installed. Yet the orchestration logic, retry behaviour, observability, and reporting are all testable without a real Unity process. We need a way to develop, demonstrate, and test the full pipeline without Unity.

## Decision

Define an `IPipelineActivities` interface that the workflow calls exclusively. Provide two implementations — `PipelineActivities` (real Unity) and `SimulatedPipelineActivities` (no Unity required) — selected at startup via the `PIPELINE_SIMULATE` environment variable.

### The interface seam

```
IPipelineActivities
├── ValidateUnityProjectAsync(input) → ProjectMetadata
├── ExecutePlatformBuildAsync(input) → BuildArtifactResult
├── GenerateReportAsync(summary) → string (report path)
├── PrepareProjectCopyAsync(input) → string (cloned path)
└── CleanupProjectCopyAsync(path) → void
```

All five activity methods are declared on the interface with Temporal's `[Activity]` attribute. The workflow references only `IPipelineActivities` — it has no knowledge of which implementation is running.

### How simulation works

| Activity | Real (`PipelineActivities`) | Simulated (`SimulatedPipelineActivities`) |
|----------|---------------------------|------------------------------------------|
| **Validate** | Checks project structure, resolves editor path, verifies platform modules | Checks project directory exists, reads real `ProjectVersion.txt` if available, skips editor/module checks |
| **Build** | Spawns Unity process, tails log, detects licensing errors, collects issues | Creates a placeholder artifact file after a 2-second delay |
| **Report** | Serialises `PipelineRunSummary` to JSON | Identical — same code path (delegates to `FileSystemUtilities`) |
| **Clone** | Hybrid copy with junctions or full copy | Creates an empty temp directory |
| **Cleanup** | Recursive delete with retry on file locks | Simple `Directory.Delete` |

### What simulation preserves

- **Full workflow orchestration** — the Temporal workflow runs identically, including parallel fan-out, `Task.WhenAll`, and `finally`-based cleanup
- **Real report output** — the report is a real JSON file with the same schema, written to the same output directory
- **Observability** — all OTLP traces, metrics, and structured logs fire in simulation mode; spans are tagged with `simulated=true` for differentiation
- **Cancellation** — simulated build `Task.Delay` respects `CancellationToken`, so cancellation and cleanup tests work
- **Temporal mechanics** — retries, timeouts, heartbeats, and worker lifecycle all function normally

### What simulation does not cover

- Unity CLI argument correctness (flags, `-executeMethod` target)
- Log file tailing and classification (no log file is produced)
- Licensing error detection and inner retry loop
- Real exit code handling and process lifecycle
- NTFS junction/hybrid copy behaviour (empty directory is created instead)
- Actual build timing characteristics

These gaps are documented here so they can be closed when a Unity-equipped CI environment is available.

### Selection mechanism

In `Program.cs`:

```csharp
if (config.Simulate)
    services.AddSingleton<IPipelineActivities, SimulatedPipelineActivities>();
else
    services.AddSingleton<IPipelineActivities, PipelineActivities>();
```

Controlled by `PIPELINE_SIMULATE=true` (default: `false`). The `just worker-sim` command sets this automatically.

### Enabling testability

The same `IPipelineActivities` interface enables the test suite to inject mock implementations via Moq:

```csharp
var mock = new Mock<IPipelineActivities>();
mock.Setup(a => a.ExecutePlatformBuildAsync(...)).ThrowsAsync(new Exception("Build crashed"));
```

This gives tests full control over activity outcomes without running any real or simulated logic. The test suite covers:

- Single-platform builds (Android, iOS)
- Multi-platform parallel builds
- Default platform selection (no parameter → both platforms)
- Validation failure (non-retryable → `WorkflowFailedException`)
- Build failure (retryable → `ActivityFailureException`)
- Partial platform failure (one fails → entire workflow fails)
- Cleanup after failure (`finally` semantics verified via `mock.Verify`)
- Cleanup after cancellation (workflow cancelled mid-build → cleanup still runs)

The three-tier approach — real activities, simulated activities, mock activities — means the workflow logic is validated at every level: unit (mocks), integration (simulation), and E2E (real Unity).

## Alternatives Considered

| Option | Verdict | Reason |
|--------|---------|--------|
| **Docker-based Unity (GameCI)** | Complementary | Provides real Unity in CI without local installation. But: large images (~5GB), licensing complexity, not a substitute for local dev speed. Would replace simulation in CI, not eliminate it. |
| **Record/replay of Unity output** | Deferred | Could capture real Unity log files and replay them in simulation for faithful log tailing tests. Worth adding if log classification logic becomes more complex. |
| **No abstraction (always require Unity)** | Rejected | Blocks development on any machine without Unity. Blocks CI without GameCI setup. Blocks the interview demo entirely. |

## Implementation

**Files**:
- `src/BuildPipeline.Orchestrator/Activities/IPipelineActivities.cs` — interface with `[Activity]` attributes
- `src/BuildPipeline.Orchestrator/Activities/PipelineActivities.cs` — real implementation
- `src/BuildPipeline.Orchestrator/Activities/SimulatedPipelineActivities.cs` — simulated implementation
- `src/BuildPipeline.Orchestrator/Program.cs` — DI registration based on `PIPELINE_SIMULATE`
- `tests/BuildPipeline.Orchestrator.Tests/PipelineWorkflowTests.cs` — mock-based workflow tests using the same interface
- `justfile` — `worker-sim` target for convenience
