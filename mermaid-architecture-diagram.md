graph TB
    subgraph Client["BuildPipeline.Client"]
        CLI["CLI Entry Point<br/>(Program.cs)"]
        CLI -->|"parse args<br/>(android, ios)"| WFInput["PipelineWorkflowInput<br/>RunId + Platforms"]
    end

    subgraph Infra["Docker Infrastructure"]
        Temporal["Temporal Server<br/>:7233"]
        Postgres["PostgreSQL 17<br/>:5432<br/>(Persistence)"]
        TemporalUI["Temporal UI<br/>:8080"]
        Aspire["Aspire Dashboard<br/>:18888<br/>(OTLP :4317)"]
        Temporal --- Postgres
        TemporalUI --- Temporal
    end

    subgraph Worker["BuildPipeline.Orchestrator"]
        direction TB
        Host["Worker Host<br/>(TemporalWorkerHost)"]
        DI["DI / Program.cs<br/>Config + OTel Setup"]

        subgraph Workflow["PipelineWorkflow.RunAsync()"]
            direction TB
            V["1. ValidateUnityProjectAsync"]
            PP["2. PrepareProjectCopyAsync<br/>(per platform, parallel)"]
            B["3. ExecutePlatformBuildAsync<br/>(per platform, parallel)"]
            CL["4. CleanupProjectCopyAsync<br/>(finally, per platform)"]
            R["5. GenerateReportAsync"]
            V --> PP --> B --> CL --> R
        end

        subgraph Activities["Activity Implementations"]
            direction TB
            Real["PipelineActivities<br/>(Real — Unity CLI)"]
            Sim["SimulatedPipelineActivities<br/>(Fake — for testing)"]
        end

        subgraph InfraCode["Infrastructure"]
            Telem["Telemetry<br/>ActivitySource + Meter"]
            Locator["UnityEditorLocator"]
            License["UnityLicenseChecker"]
            FSUtil["FileSystemUtilities<br/>Copy / Junctions"]
            SpanFilter["SpanFilterProcessor"]
        end

        subgraph Config["Configuration"]
            PConfig["PipelineConfig<br/>Env vars → typed config"]
        end

        Host --> Workflow
        Workflow --> Activities
        Activities --> InfraCode
        DI --> Host
        DI --> Config
    end

    subgraph Outputs["Output Artifacts"]
        Report["report-{runId}.json"]
        BuildDir["{runId}-{platform}/<br/>Build artifacts"]
    end

    CLI -->|"StartWorkflowAsync"| Temporal
    Temporal -->|"dispatch task"| Host
    Worker -->|"OTLP traces,<br/>metrics, logs"| Aspire
    Client -->|"OTLP traces"| Aspire
    R -->|"write"| Report
    B -->|"write"| BuildDir

    classDef temporal fill:#7B68EE,stroke:#333,color:#fff
    classDef client fill:#4CAF50,stroke:#333,color:#fff
    classDef worker fill:#2196F3,stroke:#333,color:#fff
    classDef output fill:#FF9800,stroke:#333,color:#fff
    classDef infra fill:#9C27B0,stroke:#333,color:#fff

    class Temporal,TemporalUI temporal
    class CLI,WFInput client
    class Host,DI worker
    class Report,BuildDir output
    class Aspire,Postgres infra