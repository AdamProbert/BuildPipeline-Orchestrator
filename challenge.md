# Workflow Orchestration – Candidate Brief

## Context

At Live Technology we operate the live platforms that power both our customer-facing experiences and the internal tools teams rely on to ship them. Keeping those systems healthy means every workload, from compute or database capacity tuning to building production-ready Unity artifacts, needs to execute in durable, observable, and resilient ways. This challenge asks you to bring that mindset to a focused scenario.

## Objective & Requirements

Build a workflow orchestration system that manages a Unity build process. The system should accept a platform parameter (android or ios) and orchestrate a multi-step pipeline that validates the project, executes the build, and generates a report.

**Workflow & Activities:** To maintain and scale orchestration systems effectively, it helps to separate concerns between coordinating work and executing it. The workflow layer manages execution order, error handling, and state, while activities encapsulate the actual operations. Consider patterns like idempotent activities and timeout configurations to handle failures gracefully.

**Error Handling & Retries:** Production systems need resilience against various failure modes. Some failures are transient (network glitches, temporary resource unavailability) while others are permanent (bad configuration, missing dependencies). Consider how different error types might warrant different retry strategies, such as aggressive retries with backoff for transient issues, fail-fast behavior for permanent problems, and how to surface meaningful error context to operators.

**Observability:** At scale, understanding what's happening inside your workflows becomes critical for debugging issues and maintaining system health. Consider how to make execution visible: tracing individual workflow runs across activities, capturing metrics on performance and reliability (execution time, success rates, retry frequency), or exposing workflow state through monitoring interfaces. Think about what information would help you troubleshoot a production incident.

**Project Organization:** A well-organized project makes it easier for teams to understand, run, and contribute to the codebase. Consider how you structure your code, manage dependencies, orchestrate containers, and enable local development. Clear organization around running services locally, configuring environments, and navigating the codebase helps reduce onboarding friction.

**Testing & Documentation:** Confidence in production systems comes from both automated verification and clear communication. Consider how tests can validate your orchestration logic under different scenarios (success paths, various failure modes, retry behavior). Documentation that explains setup, configuration decisions, and operational concerns helps others understand and maintain your work.

## What We Look For

Rather than prescribing a single solution, we want to see how you shape the system. Treat the brief as a product problem with room for interpretation. A strong submission usually:

- Demonstrates clean architecture with a clear separation between orchestration flow and the activities that do the work, plus thoughtful configuration and dependency management.
- Shows resilience thinking through pragmatic retry strategies, defensive coding around Unity integration, and meaningful surfacing of failure context.
- Bakes in observability hooks so it is easy to trace an execution and answer "what happened?" without reading source code.
- Invests in developer experience via documentation, run commands, or tooling that make it easy for another engineer to pick up your project.

There is no hard checklist; justify your trade-offs, document assumptions, and lean into the areas you think move the needle most.

## Provided Starter Kit

We've provided a starter project that includes workflow infrastructure via Docker Compose, a .NET project scaffold with worker host and workflow skeleton, a CLI client with platform argument support, a test project, a Unity project with BuildScript, and configuration with environment variable support. The starter code includes TODOs marking areas that require implementation, including Unity CLI invocation, retry policies, metrics collection, and error handling.

The workflow engine used in the starter kit is [Temporal](https://temporal.io/), a battle-tested orchestration platform with strong guarantees around state persistence and fault tolerance. However, you're welcome to use a different workflow orchestration system if you prefer (such as Cadence, Conductor, or others). If you choose to use a different system, please document your choice and any trade-offs you considered.

You may use any tools, IDEs, or AI assistants to complete the assignment. Be prepared to discuss your approach, design decisions, and trade-offs during the review.

## Getting Started

### Prerequisites

- .NET 8 SDK
- Docker Desktop (or Docker Engine 20+)
- (Optional) Unity 2022.3 LTS or newer for actual builds

### Quick Start

```bash
cd starter

# 1. Start workflow infrastructure
docker compose up -d

# 2. Restore .NET dependencies
dotnet restore

# 3. Start the orchestration worker (in one terminal)
dotnet run --project src/BuildPipeline.Orchestrator

# 4. Trigger a workflow (in another terminal)
dotnet run --project src/BuildPipeline.Client android

# 5. View workflow execution
open http://localhost:8080
```

> **No Unity?** If you cannot run the Unity editor locally, emulate the behaviour in a way that still lets you showcase orchestration, retries, and reporting. Capture the gap and how you would close it in a real environment.
