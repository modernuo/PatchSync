# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

PatchSync is a .NET File Patching and Upload SDK & CLI for efficient software patching and delta/binary updates. It enables applications to download only the changed portions of files rather than full file replacements.

**Primary use case**: Game launchers needing CDN-only delta patching (no server-side computation).

## Important Documentation

Before making changes, review these developer documents:

- **[dev-docs/ANALYSIS.md](dev-docs/ANALYSIS.md)** - Deep technical analysis of the approach
- **[dev-docs/DECISIONS.md](dev-docs/DECISIONS.md)** - Key architectural decisions and rationale
- **[dev-docs/IMPLEMENTATION_PLAN.md](dev-docs/IMPLEMENTATION_PLAN.md)** - Green field implementation roadmap
- **[dev-docs/SIMILAR_TOOLS_RESEARCH.md](dev-docs/SIMILAR_TOOLS_RESEARCH.md)** - Research on zsync, casync, desync, etc.

User-facing documentation is in the **[docs/](docs/)** folder.

**NOTE**: The current codebase is a prototype/guide. We are operating as **green field** - use the existing code for reference but implement fresh based on the architecture decisions.

## Build Commands

```bash
# Build the solution
dotnet build

# Build in Release configuration
dotnet build --configuration Release

# Run the CLI
dotnet run --project PatchSync.CLI

# Publish AOT (CLI supports ahead-of-time compilation)
dotnet publish --configuration Release
```

## Architecture

The solution follows a three-tier library architecture:

```
PatchSync.CLI (Executable)
    │
    ▼
PatchSync.SDK (Core Library)
    │
    ▼
PatchSync.Common (Shared Library)
```

### PatchSync.Common
Shared data structures and utilities used by both SDK and CLI:
- **Manifest/** - `PatchManifest`, `ManifestFileEntry`, `ManifestFileCommand` enum
- **Signatures/** - `SignatureFile`, `SignatureChunk` record struct
- **Hashing/** - `Adler32RollingChecksum` for rolling hash calculations
- **RemoteFiles/** - `Downloader`, HTTP handling with byte-range support
- **LocalFiles/** - File change tracking and progress types

### PatchSync.SDK
Core patching functionality exposed as a reusable library:
- **PatchSyncClient** - Main API class (split across partial files for organization)
  - Downloads delta patches using byte-range HTTP requests
  - Manages manifest downloads and file validation
  - Reports progress via `IProgress<T>`
- **FilePatcher** - Delta calculation engine using rolling hash + XxHash3
- **SignatureFileHandler** - Generates and deserializes file signatures
- **ThreadWorker** - Parallel file processing across CPU cores

### PatchSync.CLI
Command-line interface with four main commands:
- `BuildSignatures` - Generate signature files for a directory
- `UploadSignatures` - Upload signatures to remote storage (S3)
- `PatchInstallation` - Apply patches to a local installation
- `TestPatchDownload` - Test patch download functionality

## Key Technical Patterns

**Multi-framework targeting:**
- CLI: .NET 8.0 with AOT compilation
- SDK: netstandard2.0, netstandard2.1, net6.0, net7.0, net8.0
- Common: netstandard2.0, netstandard2.1, net6.0, net8.0

**Delta patching algorithm:**
1. Files are divided into chunks (size calculated from file size)
2. Adler32 rolling hash enables fast chunk boundary detection
3. XxHash3 provides full chunk verification
4. `PatchSlice` records describe which chunks exist locally vs need downloading

**ManifestFileCommand enum values:**
- `DeltaUpdate` - Download only changed chunks
- `AlwaysFullUpdate` - Always download full file
- `UpdateIfFullHashMismatch` - Download if hash differs
- `UpdateIfMissing` - Download only if file missing
- `NeverUpdate` - Skip this file
- `Delete` - Remove local file

**Performance conventions:**
- `Span<T>` and `Memory<T>` for buffer operations
- `[MethodImpl(MethodImplOptions.AggressiveInlining)]` on hot paths
- `unsafe` blocks for low-level memory operations
- Async streaming with `IAsyncEnumerable`

## Core Architectural Concepts

**Reverse rsync model**: All delta computation happens client-side against static CDN files. Server hosts only:
- `manifest.json` - File list with hashes, sizes, update strategies
- Raw game files - For HTTP byte-range requests
- `.sig` signature files - CDC chunk hashes for each file
- `.zst` compressed files (optional) - Fallback when delta isn't worth it

**Content-Defined Chunking (CDC)**: Variable-size chunks determined by content, not position. Essential for handling insertions/deletions. Use desync/casync-compatible buzhash algorithm.

**CDN-Only Constraint**: Game developers should not need to host servers. Static file hosting only (S3, R2, CloudFront, etc.).

**Version Jumping**: Handles any local state → target state naturally. No version-to-version patches needed.
