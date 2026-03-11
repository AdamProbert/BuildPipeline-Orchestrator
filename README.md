# Build Pipeline Orchestrator

> **Reviewer Guide** — start here to navigate the submission.
>
> | Resource | What it is |
> |---|---|
> | [challenge.md](challenge.md) | The original challenge brief (Markdown copy) |
> | [adrs/](adrs/) | Architecture Decision Records — explains *why* behind key design choices (retry strategy, observability, simulation mode, graceful cancellation, project-copy strategy) |
> | [mermaid-architecture-diagram.md](mermaid-architecture-diagram.md) | System architecture diagram (paste into any Mermaid renderer or use VS Code's *Markdown Preview Mermaid Support* extension to view) |
> | [mermaid-workflow-execution-flow-diagram.md](mermaid-workflow-execution-flow-diagram.md) | Workflow execution sequence diagram (same rendering options) |
> | [justfile](justfile) | All project commands in one place — run `just` to list them (see [Quick Start](#quick-start) below) |
> | [CLAUDE.md](CLAUDE.md) | Concise AI assistant context file used during development |

## Now for the main event..
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

Run `just` to list available commands. Here's some of the main ones:

| Command | Description |
|---|---|
| `just setup` | Start infra + restore + build |
| `just worker` | Run the Temporal worker |
| `just worker-sim` | Run worker in simulated mode |
| `just run <platform>` | Trigger a workflow |
| `just test` | Run unit + integration tests |
| `just ui` | Open Temporal dashboard |
| `just status` | Show infrastructure status |
| `just infra-up` | Start infrastructure containers |
| `just build` | Build the solution |
| `just logs` | Follow Temporal server logs |

## Architecture

See the architecture diagrams for a visual overview of the system:

- [System Architecture](mermaid-architecture-diagram.md) — high-level component diagram showing projects, Temporal, Docker infrastructure, and observability
- [Workflow Execution Flow](mermaid-workflow-execution-flow-diagram.md) — sequence diagram showing the step-by-step data flow from CLI through validation, parallel builds, and report generation

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
| `OTEL_EXPORTER_OTLP_ENDPOINT` | *(none)* | OTLP collector endpoint (e.g. `http://localhost:4317`). Enables tracing, metrics, and structured logs. |
| `PIPELINE_VALIDATION_TIMEOUT_SECONDS` | `30` | Activity timeout for project validation |
| `PIPELINE_BUILD_TIMEOUT_MINUTES` | `30` | Activity timeout for Unity builds |
| `PIPELINE_REPORT_TIMEOUT_SECONDS` | `60` | Activity timeout for report generation |
| `PIPELINE_BUILD_RETRY_INTERVAL_SECONDS` | `5` | Initial interval between Temporal build retries |
| `PIPELINE_LICENSING_MAX_RETRIES` | `5` | Max inner retries for Unity licensing errors |
| `PIPELINE_LICENSING_RETRY_DELAY_SECONDS` | `30` | Delay between licensing retry attempts |
