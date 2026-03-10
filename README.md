# Build Pipeline Orchestrator

Unity build pipeline orchestration using [Temporal](https://temporal.io/) and .NET 8. Orchestrates: project validation → platform builds (parallel) → report generation.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Docker](https://www.docker.com/) (for Temporal infrastructure)
- [just](https://github.com/casey/just) (command runner)
- Unity (optional — simulated mode requires no installation)

## Quick Start

```bash
just setup           # start Temporal infra + restore + build
just worker-sim      # terminal 1 — start worker in simulated mode
just run android     # terminal 2 — trigger a build
just ui              # open Temporal dashboard at http://localhost:8080
```

## Running E2E Tests

### Option 1: Simulated (no Unity required)

Start the worker in one terminal, trigger from another:

```bash
just setup           # first time only

# terminal 1
just worker-sim

# terminal 2
just e2e android
```

Build multiple platforms:

```bash
just e2e android,ios
```

### Option 2: Real Unity builds

Requires Unity installed via Unity Hub with the matching editor version and platform modules (e.g. Android Build Support):

```bash
# terminal 1 — the editor is auto-detected from the project version
just worker

# terminal 2
just e2e android
```

Override the editor path if needed:

```bash
UNITY_EDITOR_PATH="/path/to/Unity" just worker
```

## Running Unit & Integration Tests

```bash
just test            # run all tests
just test-verbose    # with detailed output
```

Integration tests spin up an in-process Temporal server — no Docker required.

## All Commands

Run `just` to list available commands:

| Command | Description |
|---|---|
| `just setup` | Start infra + restore + build |
| `just worker` | Run the Temporal worker |
| `just worker-sim` | Run worker in simulated mode |
| `just worker-full` | Run worker with full (non-junction) copies |
| `just run <platform>` | Trigger a workflow (fire-and-forget) |
| `just e2e <platform>` | Trigger and wait for completion |
| `just test` | Run unit + integration tests |
| `just test-verbose` | Run tests with detailed output |
| `just ui` | Open Temporal dashboard |
| `just status` | Show infrastructure status |
| `just infra-up` | Start Temporal containers |
| `just infra-down` | Stop Temporal containers |
| `just infra-clean` | Stop containers and remove volumes |
| `just restore` | Restore .NET dependencies |
| `just build` | Build the solution |
| `just lint` | Check code formatting |
| `just lint-fix` | Auto-fix formatting |
| `just logs` | Follow Temporal server logs |

## Configuration

All config is via environment variables:

| Variable | Default | Description |
|---|---|---|
| `UNITY_EDITOR_PATH` | Auto-detected | Path to Unity editor executable |
| `PIPELINE_SIMULATE` | `false` | Use simulated activities (no Unity needed) |
| `PIPELINE_UNITY_PROJECT_PATH` | `../unity-sample` | Path to Unity project |
| `PIPELINE_OUTPUT_DIR` | `../output` | Build artifact output directory |
| `TEMPORAL_ADDRESS` | `localhost:7233` | Temporal server address |
| `TEMPORAL_NAMESPACE` | `default` | Temporal namespace |
| `PIPELINE_TASK_QUEUE` | `build-pipeline-task-queue` | Temporal task queue name |
| `PIPELINE_COPY_STRATEGY` | `junction` | Project copy strategy: `junction` (NTFS junctions for read-only dirs, Windows) or `full` (plain recursive copy) |
| `PIPELINE_JUNCTION_DIRS` | `Assets,Packages,ProjectSettings` | Comma-separated list of directories to junction instead of copy (used when strategy is `junction`) |
