# ADR: Junction-Based Project Copies for Concurrent Unity Builds

**Status**: Accepted  
**Date**: 2026-03-10

## Context

Our build pipeline creates isolated copies of the Unity project for each concurrent platform build (Android + iOS in parallel). The current implementation in `FileSystemUtilities.CopyDirectory()` performs a full recursive `File.Copy`, duplicating every file except the `Temp/` directory.

For real Unity projects (1–5GB+), this means:
- **2x disk usage** per concurrent build
- **Minutes of I/O** before the actual build starts
- Unnecessary copying of large read-only directories (`Assets/`, `Packages/`)

## Decision

Replace full directory copies with a **hybrid approach**: NTFS **directory junctions** for read-only directories, hard copies only for directories Unity writes to during builds.

| Directory | Strategy | Rationale |
|-----------|----------|-----------|
| `Assets/` | Junction | Read-only in batch-mode builds |
| `Packages/` | Junction | Read-only in batch-mode builds |
| `ProjectSettings/` | Junction | Read-only in batch-mode builds |
| `Library/` | Hard copy | Contains lock files (`ArtifactDB-lock`, `ilpp.pid`), platform-specific caches, `EditorUserBuildSettings.asset` |
| `Logs/` | Hard copy | Written during builds |
| `UserSettings/` | Hard copy | May be modified |
| `Temp/` | Excluded | Created fresh by Unity at runtime |

## What Is a Junction?

An NTFS junction (`mklink /J`) is a directory-level reparse point that transparently redirects all filesystem access to a target directory. It's a metadata-only operation — no files are copied, no extra disk space is consumed.

Key properties:
- **No admin rights required** (unlike `mklink /D` symlinks)
- **No configuration needed** — built into NTFS since Windows 2000
- **Works on Windows 11 Home** — no Pro/Enterprise features required (my current desktop)
- **Transparent to applications** — Unity, .NET, Explorer all see a real directory
- **Safe deletion** — `Directory.Delete(junction)` removes the link, not the target

Ref: [Hard links and junctions — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/fileio/hard-links-and-junctions)

## Why Library Must Be Copied

Library contains exclusive lock files and platform-specific state:
- `ArtifactDB-lock`, `SourceAssetDB-lock` — database locks held during the build
- `ilpp.pid` — IL post-processor lock
- `EditorUserBuildSettings.asset` — active build platform (Android vs iOS)
- `ScriptAssemblies/` — compiled DLLs, rebuilt per platform
- `ShaderCache/` — platform-dependent compiled shaders
- `Bee/` — incremental build system state

Two concurrent Unity instances cannot share these files.

## Alternatives Considered

| Option | Verdict | Reason |
|--------|---------|--------|
| **Dev Drive + ReFS Block Cloning** | Complementary | True CoW on ReFS; requires Windows 11 24H2+. Can layer on top — `File.Copy` automatically benefits. Ref: [Block Cloning — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/fileio/block-cloning), [Dev Drive — Microsoft Learn](https://learn.microsoft.com/en-us/windows/dev-drive/) |
| **Docker (GameCI containers)** | Deferred | Perfect isolation via bind mounts + overlay. But: Win 11 Home = Linux containers only, Unity licensing complexity, large architecture change. Ref: [GameCI Docker Images](https://game.ci/docs/docker/docker-images) |
| **ProjFS (Projected File System)** | Rejected | Requires writing a custom provider application — disproportionate engineering effort. Ref: [ProjFS — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/projfs/projected-file-system) |

## Implementation

**Files to modify**:
- `src/BuildPipeline.Orchestrator/Infrastructure/FileSystemUtilities.cs` — add `CreateJunction()` and `CopyDirectoryHybrid()`
- `src/BuildPipeline.Orchestrator/Activities/PipelineActivities.cs` — update `PrepareProjectCopyAsync` to use hybrid copy
- `tests/BuildPipeline.Orchestrator.Tests/PipelineActivityTests.cs` — add junction-specific tests

**Fallback**: If junction creation fails (e.g., cross-volume paths), fall back to full copy with a warning log.

## Risks

| Risk | Likelihood | Mitigation |
|------|-----------|------------|
| Unity writes to Assets/ during batch build | Very low | `BuildPipeline.BuildPlayer` in `-batchmode` reads Assets, doesn't write. Ref: [Unity default directories](https://docs.unity3d.com/Manual/default-directories.html) |
| Cleanup deletes original files | None | `Directory.Delete` on a junction removes the reparse point, not the target. Verified in .NET runtime behavior. |
| Junctions not supported on target volume | Very low | Only fails on non-NTFS (FAT32/exFAT USB drives). Temp dir is always NTFS. Fallback to full copy handles this. |

## Expected Impact

- **~90% reduction in copy size** — only Library (~30-50% of project) is copied; Assets (~50-60%) is junctioned
- **Near-instant junction creation** — metadata-only operation regardless of directory size
- **Zero Windows configuration required** — works out of the box on NTFS

---

## Update: Configurable Copy Strategy (2026-03-10)

The copy strategy is now configurable at the worker level via the `PIPELINE_COPY_STRATEGY` environment variable, rather than being hardcoded to the junction-based hybrid approach.

**Values**:

| Value | Behaviour |
|-------|-----------|
| `junction` (default) | Hybrid copy — NTFS junctions for read-only dirs, hard copy for writable dirs |
| `full` | Plain recursive `File.Copy` — no OS-specific features, works on any filesystem |

**Rationale**: Not all environments support NTFS junctions (e.g., CI runners on Linux, non-NTFS volumes). Making the strategy configurable lets operators choose the right mode without code changes.

**Changes**:
- `PipelineConfig` — added `ProjectCopyStrategy` enum and `CopyStrategy` field (read from `PIPELINE_COPY_STRATEGY`)
- `PipelineActivities.PrepareProjectCopyAsync` — branches on `_config.CopyStrategy`
- `justfile` — added `worker-full` recipe (`PIPELINE_COPY_STRATEGY=full`)
- `README.md` — documented the new env var and command

---

## Update: Configurable Junction Directories (2026-03-10)

The set of directories that are junctioned (rather than hard-copied) is now configurable via `PIPELINE_JUNCTION_DIRS`.

| Variable | Default | Description |
|----------|---------|-------------|
| `PIPELINE_JUNCTION_DIRS` | `Assets,Packages,ProjectSettings` | Comma-separated directory names to junction instead of copy |

**Rationale**: Different Unity projects may have additional read-only directories (e.g., vendored plugin folders) or may need to exclude a default directory from junctioning if a build step writes to it.

**Changes**:
- `PipelineConfig` — added `JunctionDirs` (`HashSet<string>`) parsed from `PIPELINE_JUNCTION_DIRS`; defaults moved from `FileSystemUtilities` into `PipelineConfig`
- `FileSystemUtilities` — removed static `JunctionSafeDirs` field; `CopyDirectoryHybrid` now requires the junction set to be passed explicitly
- `PipelineActivities` — passes `_config.JunctionDirs` to `CopyDirectoryHybrid`