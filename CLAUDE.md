# Build Pipeline Orchestrator

## Rules

1. Be succinct — don't over-explain or overbuild. Don't add features not requested.
2. Challenge the user — be devil's advocate for their ideas.
3. Update this file with any new context required, but be brief. Remove context when no longer valid.
4. Don't preserve old code with "legacy" statements — the codebase reflects the current point in time. Git history is used for history.

## Overview

**Unity build pipeline orchestration** using [Temporal](https://temporal.io/) and .NET 8. Accepts a platform parameter (`android` or `ios`) and orchestrates: project validation → build execution → report generation.

## Structure

- **`src/BuildPipeline.Orchestrator/`** — Temporal worker host, workflow definitions, and activity implementations
- **`src/BuildPipeline.Client/`** — CLI client to trigger workflow runs with a platform argument
- **`tests/BuildPipeline.Orchestrator.Tests/`** — Test project for workflow logic
- **`unity-sample/`** — Sample Unity project with an editor build script
- **`docker-compose.yml`** — Temporal server infrastructure
- **`dynamicconfig/`** — Temporal dynamic configuration

## Just Commands

Run `just` to list all available commands.

## Quick Start

```bash
just setup           # start infra + restore + build
just worker          # terminal 1 – start the Temporal worker
just run android     # terminal 2 – trigger a build workflow
just ui              # open the Temporal dashboard
```
