# Build Pipeline Orchestrator – just commands
# https://github.com/casey/just

# Default recipe: list available commands
default:
    @just --list

# Start Temporal infrastructure (PostgreSQL, Temporal server, UI)
infra-up:
    docker compose up -d

# Stop Temporal infrastructure
infra-down:
    docker compose down

# Stop infrastructure and remove volumes
infra-clean:
    docker compose down -v

# Restore .NET dependencies
restore:
    dotnet restore

# Build the entire solution
build:
    dotnet build

# Run the orchestration worker
worker:
    dotnet run --project src/BuildPipeline.Orchestrator

# Run the worker in simulated mode (no Unity installation required)
worker-sim:
    PIPELINE_SIMULATE=true dotnet run --project src/BuildPipeline.Orchestrator

# Trigger a workflow for the given platform (default: android)
run platform="android":
    dotnet run --project src/BuildPipeline.Client -- {{platform}}

# End-to-end validation: trigger a workflow and wait for completion (requires a running worker)
e2e platform="android":
    dotnet run --project src/BuildPipeline.Client -- {{platform}} --wait

# Lint: check code style and formatting (fails on violations)
lint:
    dotnet format --verify-no-changes --verbosity normal

# Lint: auto-fix code style and formatting issues
lint-fix:
    dotnet format

# Run tests
test:
    dotnet test

# Run tests with verbose output
test-verbose:
    dotnet test --verbosity normal

# Full setup: start infra, restore, and build
setup: infra-up restore build

# Open the Temporal UI in the default browser
ui:
    @echo "Opening http://localhost:8080"
    @powershell -c "Start-Process 'http://localhost:8080'"

# Show Temporal infrastructure status
status:
    docker compose ps

# View Temporal worker logs
logs:
    docker compose logs -f temporal
