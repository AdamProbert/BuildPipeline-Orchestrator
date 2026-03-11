sequenceDiagram
    participant CLI as Client CLI
    participant T as Temporal Server
    participant W as Worker
    participant VA as ValidateActivity
    participant PA as PrepareActivity
    participant BA as BuildActivity
    participant CA as CleanupActivity
    participant RA as ReportActivity
    participant FS as FileSystem

    CLI->>CLI: Parse args (android,ios)
    CLI->>CLI: Generate RunId
    CLI->>T: StartWorkflowAsync(PipelineWorkflowInput)

    T->>W: Dispatch workflow task
    W->>W: PipelineWorkflow.RunAsync()

    Note over W,VA: Step 1 — Validate
    W->>VA: ValidateUnityProjectAsync()
    VA->>FS: Check project dirs, version, editor
    FS-->>VA: OK
    VA-->>W: ProjectMetadata

    Note over W,BA: Step 2 — Parallel Builds
    par Android Build
        W->>PA: PrepareProjectCopyAsync(android)
        PA->>FS: Clone project (junction/copy)
        FS-->>PA: cloned path
        PA-->>W: projectCopyPath

        W->>BA: ExecutePlatformBuildAsync(android)
        BA->>BA: Unity -batchmode -quit
        BA-->>W: BuildArtifactResult

        W->>CA: CleanupProjectCopyAsync(android)
        CA->>FS: Delete clone (retries)
    and iOS Build
        W->>PA: PrepareProjectCopyAsync(ios)
        PA->>FS: Clone project (junction/copy)
        FS-->>PA: cloned path
        PA-->>W: projectCopyPath

        W->>BA: ExecutePlatformBuildAsync(ios)
        BA->>BA: Unity -batchmode -quit
        BA-->>W: BuildArtifactResult

        W->>CA: CleanupProjectCopyAsync(ios)
        CA->>FS: Delete clone (retries)
    end

    Note over W,RA: Step 3 — Report
    W->>RA: GenerateReportAsync(summary)
    RA->>FS: Write report-{runId}.json
    RA-->>W: reportPath

    W-->>T: PipelineRunSummary
    T-->>CLI: Workflow complete