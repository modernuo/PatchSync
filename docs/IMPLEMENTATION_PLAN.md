# PatchSync Implementation Plan

This document outlines the green field implementation plan for PatchSync v2.

---

## Important Notice: Green Field Approach

**The existing codebase is a prototype/guide only.** We are implementing fresh based on the architectural decisions documented in [DECISIONS.md](DECISIONS.md).

Use the existing code for reference on:
- HTTP handling patterns
- Progress reporting patterns
- Multi-framework targeting
- Async/streaming patterns

Do NOT attempt to refactor the existing code. Start clean.

---

## Key Architecture Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| **Chunking Algorithm** | FastCDC | Simplest (~25 lines core), normalized chunks, 2-3x faster than Buzhash |
| **Vectorization** | No (MVP) | Network is bottleneck, not CPU; IChunker allows future VRAM/etc. |
| **Build Tooling** | .NET CLI (NativeAOT) | Same algorithm client+server, single binary distribution |
| **Index Format** | Custom PSI1 | Algorithm-aware headers, version-tagged |
| **Cross-file Matching** | IChunkSource abstraction | Desync-inspired Seed mechanism for chunk reuse |
| **Compressed Fallback** | Automatic selection | Delta vs compressed size comparison |
| **Runtime** | .NET 10, NativeAOT | Single binary, no runtime dependency |

### Why FastCDC over Buzhash (desync)?

| Aspect | FastCDC | Buzhash (desync) |
|--------|---------|------------------|
| **Speed** | ~1-2 GB/s | ~400-600 MB/s |
| **Chunk Distribution** | Normalized (uniform) | Geometric (skewed small) |
| **Implementation** | 25 lines core | 50+ lines + window mgmt |
| **Hash Type** | Gear (shift+add) | Rolling (rotate+xor) |
| **Window** | None | 48-byte rolling |

FastCDC's normalized chunking creates more uniform chunk sizes, which means:
- More predictable signature file sizes
- Better deduplication ratio on structured data (pak files, archives)
- Fewer edge cases with tiny chunks

---

## Core Interfaces

### IChunker - Pluggable Chunking Algorithm

```csharp
public interface IChunker
{
    /// <summary>Algorithm identifier (e.g., "fastcdc-v1", "buzhash-casync-v1")</summary>
    string AlgorithmId { get; }

    /// <summary>Chunk a stream into content-defined boundaries</summary>
    IEnumerable<ChunkBoundary> Chunk(Stream input, ChunkingOptions options);
}

public readonly record struct ChunkBoundary(long Offset, int Length, byte[] Hash);

public sealed class ChunkingOptions
{
    public int MinSize { get; init; } = 4096;      // 4 KB
    public int AverageSize { get; init; } = 16384; // 16 KB
    public int MaxSize { get; init; } = 65536;     // 64 KB
}
```

### IChunkerRegistry - Algorithm Discovery

```csharp
public interface IChunkerRegistry
{
    IReadOnlyList<string> SupportedAlgorithms { get; }
    bool TryGetChunker(string algorithmId, out IChunker chunker);
    IChunker GetPreferred(IEnumerable<string> available);
}
```

### IChunkSource - Cross-File Chunk Matching

```csharp
public interface IChunkSource
{
    /// <summary>Try to find a chunk by hash in this source</summary>
    bool TryGetChunk(ReadOnlySpan<byte> hash, out ChunkLocation location);

    /// <summary>Find all matching chunks from a list of needed chunks</summary>
    IEnumerable<ChunkMatch> FindMatches(IEnumerable<ChunkInfo> needed);
}

public readonly record struct ChunkLocation(string FilePath, long Offset, int Length);
public readonly record struct ChunkMatch(ChunkInfo Needed, ChunkLocation Source);
```

---

## PSI1 Signature File Format

Algorithm-aware binary format for chunk signatures:

```
[Magic: 4 bytes]       "PSI1" (PatchSync Index v1)
[Algorithm: 1 byte]    0x01=fastcdc-v1, 0x02=buzhash-casync-v1
[Version: 1 byte]      Algorithm version for forward compat
[MinSize: 4 bytes]     Minimum chunk size (little-endian)
[AvgSize: 4 bytes]     Average chunk size (little-endian)
[MaxSize: 4 bytes]     Maximum chunk size (little-endian)
[ChunkCount: 4 bytes]  Number of chunks (little-endian)
[Chunks...]            Each: [Offset:8][Length:4][Hash:32]
```

Total overhead: 22 bytes header + 44 bytes per chunk.

For a 1GB file with 16KB average chunks (~65,536 chunks):
- Header: 22 bytes
- Chunks: 65,536 × 44 = 2.88 MB
- **~0.28% of file size**

---

## Project Structure

```
PatchSync/
├── src/
│   ├── PatchSync.Common/          # Shared data structures (NativeAOT compatible)
│   │   ├── Chunking/              # FastCDC + IChunker interface
│   │   ├── Signatures/            # PSI1 format handling
│   │   ├── Manifest/              # Game manifest structures
│   │   ├── Hashing/               # SHA256, XxHash3
│   │   └── Storage/               # Storage provider abstractions
│   │
│   ├── PatchSync.SDK/             # Core client library (NativeAOT compatible)
│   │   ├── Client/                # Main PatchSyncClient
│   │   ├── Delta/                 # Delta calculation
│   │   ├── Sources/               # IChunkSource implementations
│   │   ├── Assembly/              # File assembly from chunks
│   │   └── Download/              # HTTP download handling
│   │
│   └── PatchSync.CLI/             # Command-line tool (NativeAOT, single binary)
│       ├── Commands/
│       │   ├── BuildCommand.cs    # Generate signatures/manifest
│       │   ├── PatchCommand.cs    # Apply delta patches
│       │   ├── VerifyCommand.cs   # Verify installation integrity
│       │   └── InfoCommand.cs     # Display manifest/signature info
│       └── Program.cs
│
├── tools/
│   └── testvectors/               # FastCDC test vector generator (Go)
│
├── tests/
│   ├── PatchSync.Tests/           # Unit + compatibility tests
│   └── PatchSync.Benchmarks/      # Performance benchmarks
│
└── docs/
    ├── ANALYSIS.md
    ├── DECISIONS.md
    └── IMPLEMENTATION_PLAN.md
```

### NativeAOT Requirements

All code must be NativeAOT compatible:
- No reflection-based serialization (use source generators)
- No dynamic code generation
- Explicit JSON serialization contexts
- Trim-safe code patterns

```xml
<!-- PatchSync.CLI.csproj -->
<PropertyGroup>
  <PublishAot>true</PublishAot>
  <InvariantGlobalization>true</InvariantGlobalization>
</PropertyGroup>
```

---

## Phase 1: Foundation ✅ COMPLETE

### Goal: Core data structures and CDC algorithm

#### 1.1 IChunker Interface and Types ✅

Implemented in `PatchSync.Common/Chunking/`:
- `IChunker.cs` - Pluggable chunker interface
- `IChunkerRegistry.cs` - Algorithm discovery interface
- `ChunkingOptions` with validation

#### 1.2 FastCDC Chunker ✅

Implemented in `PatchSync.Common/Chunking/FastCDCChunker.cs`:
- Full port of fastcdc-go algorithm
- Normalized chunking with two masks (MaskS/MaskL)
- Zero-allocation patterns: `Span<T>`, `stackalloc`, `IncrementalHash`
- Gear hash table matching fastcdc-go exactly

**Verified**: Test vectors generated from fastcdc-go, all 6 test cases pass.

#### 1.3 ChunkerRegistry ✅

Implemented in `PatchSync.Common/Chunking/IChunkerRegistry.cs`:
- Factory-based algorithm instantiation
- Algorithm negotiation for client-server compatibility
- Extensible for future algorithms (VRAM, etc.)

---

## Phase 2: Delta Engine

### Goal: Client-side delta calculation, cross-file matching, and download strategy

#### 2.1 Cross-File Chunk Source (Desync Seed-Inspired)

This is the key innovation from desync - the ability to find chunks across multiple local files:

```csharp
// PatchSync.SDK/Sources/IChunkSource.cs

public interface IChunkSource
{
    /// <summary>Try to find a chunk by its SHA256 hash</summary>
    bool TryGetChunk(ReadOnlySpan<byte> hash, out ChunkLocation location);

    /// <summary>Build index of all chunks in this source</summary>
    IReadOnlyDictionary<Hash256, ChunkLocation> GetChunkIndex();
}

public readonly record struct ChunkLocation(string FilePath, long Offset, int Length);
```

**Use cases:**
- Find chunks in the target file being updated (self-seed)
- Find chunks in other game files (cross-file matching)
- Find chunks in old pak files when assets moved between archives

```csharp
// PatchSync.SDK/Sources/LocalFileSource.cs
public sealed class LocalFileSource : IChunkSource
{
    private readonly FrozenDictionary<Hash256, ChunkLocation> _index;

    public static LocalFileSource Build(
        string filePath,
        IChunker chunker,
        ChunkingOptions options)
    {
        // Chunk local file and build hash -> location map
        // Uses content-addressed hashing: same content = same hash
    }
}

// PatchSync.SDK/Sources/CompositeChunkSource.cs
public sealed class CompositeChunkSource : IChunkSource
{
    private readonly List<IChunkSource> _sources = new();

    public void AddSource(IChunkSource source) => _sources.Add(source);

    public bool TryGetChunk(ReadOnlySpan<byte> hash, out ChunkLocation location)
    {
        // Priority order: target file first, then other local files
        foreach (var source in _sources)
        {
            if (source.TryGetChunk(hash, out location))
                return true;
        }
        location = default;
        return false;
    }
}
```

#### 2.3 Delta Calculator

```csharp
// PatchSync.SDK/Delta/DeltaCalculator.cs

public sealed class DeltaCalculator
{
    public DeltaPlan Calculate(SignatureFile remoteSignature, IChunkSource localSources)
    {
        var plan = new List<DeltaAction>();

        foreach (var chunk in remoteSignature.Chunks)
        {
            if (localSources.TryGetChunk(chunk.Hash, out var location))
            {
                // Found locally (could be in target file OR another file)
                plan.Add(new CopyLocal(chunk.Offset, location, chunk.Length));
            }
            else
            {
                // Need to download from CDN
                plan.Add(new DownloadRemote(chunk.Offset, chunk.Length, chunk.Hash));
            }
        }

        return new DeltaPlan(plan);
    }
}

public abstract record DeltaAction(long TargetOffset, int Length);

public record CopyLocal(
    long TargetOffset,
    ChunkLocation Source,  // Contains FilePath, Offset - supports cross-file copy
    int Length
) : DeltaAction(TargetOffset, Length);

public record DownloadRemote(
    long TargetOffset,
    int Length,
    byte[] Hash
) : DeltaAction(TargetOffset, Length);

public sealed class DeltaPlan
{
    public IReadOnlyList<DeltaAction> Actions { get; }
    public long TotalBytes => Actions.Sum(a => a.Length);
    public long BytesToDownload => Actions.OfType<DownloadRemote>().Sum(a => a.Length);
    public long BytesToCopy => Actions.OfType<CopyLocal>().Sum(a => a.Length);
}
```

#### 2.4 Download Strategy Selection (Compressed Fallback)

```csharp
// PatchSync.SDK/Download/DownloadStrategySelector.cs

public sealed class DownloadStrategySelector
{
    private readonly double _deltaThreshold;  // Default: 0.8

    public DownloadStrategySelector(double deltaThreshold = 0.8)
    {
        _deltaThreshold = deltaThreshold;
    }

    public DownloadDecision Select(ManifestFile file, DeltaPlan plan)
    {
        var deltaBytes = plan.BytesToDownload;
        var hasCompressedFallback = file.CompressedUrl != null && file.CompressedSize > 0;

        // Case 1: No local file exists (all chunks need downloading)
        if (plan.BytesToCopy == 0)
        {
            if (hasCompressedFallback)
                return new DownloadDecision(DownloadMethod.Compressed, file.CompressedSize,
                    "No local data available, using compressed download");
            return new DownloadDecision(DownloadMethod.Full, file.Size,
                "No local data available");
        }

        // Case 2: Compare delta vs compressed size
        // Use delta only if it's significantly better than compressed
        if (hasCompressedFallback && deltaBytes >= file.CompressedSize * _deltaThreshold)
        {
            return new DownloadDecision(DownloadMethod.Compressed, file.CompressedSize,
                $"Delta ({deltaBytes:N0} bytes) not enough better than compressed ({file.CompressedSize:N0} bytes)");
        }

        // Case 3: Delta is the best choice
        return new DownloadDecision(DownloadMethod.Delta, deltaBytes,
            $"Delta saves {file.Size - deltaBytes:N0} bytes ({100.0 * plan.BytesToCopy / file.Size:F1}% reused locally)");
    }
}

public enum DownloadMethod { Delta, Compressed, Full }

public readonly record struct DownloadDecision(
    DownloadMethod Method,
    long EstimatedBytes,
    string Reason
);
```

---

## Phase 3: File Assembly (Week 5-6)

### Goal: Reconstruct files from local + remote chunks

#### 3.1 Range Coalescing

```csharp
// PatchSync.SDK/Download/RangeCoalescer.cs

public sealed class RangeCoalescer
{
    private readonly long _maxGap;  // Default: 4KB - merge ranges within this gap

    public IEnumerable<(long Start, long End)> Coalesce(
        IEnumerable<DownloadRemote> actions)
    {
        // Sort by offset and merge adjacent/overlapping ranges
    }
}
```

#### 3.2 File Assembler

```csharp
// PatchSync.SDK/Assembly/FileAssembler.cs

public sealed class FileAssembler
{
    public async Task AssembleAsync(
        string targetPath,
        string? sourcePath,  // Existing local file (for CopyLocal)
        DeltaPlan plan,
        IStorageProvider storage,
        string remoteFilePath,
        IProgress<AssemblyProgress>? progress = null,
        CancellationToken ct = default)
    {
        // 1. Create/open target file
        // 2. For CopyLocal: copy from source to target
        // 3. For DownloadRemote: download ranges and write to target
        // 4. Verify final hash
    }
}

public readonly record struct AssemblyProgress(
    long BytesComplete,
    long BytesTotal,
    long BytesDownloaded,
    long BytesCopied
);
```

---

## Phase 4: Client SDK (Week 7-8)

### Goal: High-level API for game launchers

#### 4.1 PatchSyncClient

```csharp
// PatchSync.SDK/Client/PatchSyncClient.cs

public sealed class PatchSyncClient
{
    private readonly IStorageProvider _storage;
    private readonly PatchSyncOptions _options;

    public PatchSyncClient(IStorageProvider storage, PatchSyncOptions? options = null)
    {
        _storage = storage;
        _options = options ?? new PatchSyncOptions();
    }

    public async Task<GameManifest> GetManifestAsync(
        string manifestUrl,
        CancellationToken ct = default)
    {
        // Download and parse manifest.json
    }

    public async Task<PatchPlan> CreatePlanAsync(
        GameManifest manifest,
        string installPath,
        CancellationToken ct = default)
    {
        // Analyze local installation vs manifest
        // Return plan with files to update/add/delete
    }

    public async Task ApplyPatchAsync(
        PatchPlan plan,
        IProgress<PatchProgress>? progress = null,
        CancellationToken ct = default)
    {
        // Execute the patch plan
    }

    public async Task<VerifyResult> VerifyAsync(
        GameManifest manifest,
        string installPath,
        CancellationToken ct = default)
    {
        // Verify installation matches manifest
    }
}
```

#### 4.2 Progress Reporting

```csharp
// PatchSync.SDK/Client/PatchProgress.cs

public readonly record struct PatchProgress(
    PatchPhase Phase,
    string CurrentFile,
    int FilesComplete,
    int FilesTotal,
    long BytesDownloaded,
    long BytesTotal,
    double SpeedBytesPerSecond,
    TimeSpan EstimatedRemaining
);

public enum PatchPhase
{
    Analyzing,
    Downloading,
    Assembling,
    Verifying,
    Complete
}
```

---

## Phase 5: CLI Tool (NativeAOT)

### Goal: Single-binary CLI for both build (server) and patch (client)

The CLI uses the **same FastCDC implementation** for both building signatures and patching.
This ensures perfect algorithm compatibility without external dependencies.

#### 5.1 Build Command

```csharp
// PatchSync.CLI/Commands/BuildCommand.cs

public class BuildCommand
{
    public required string InputPath { get; set; }
    public required string OutputPath { get; set; }
    public string? ManifestOverrides { get; set; }
    public int MinChunkSize { get; set; } = 4096;
    public int AvgChunkSize { get; set; } = 16384;
    public int MaxChunkSize { get; set; } = 65536;
    public bool Compress { get; set; } = true;

    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        // 1. Scan input directory
        // 2. For each file:
        //    a. Determine update strategy (delta, compressed, skip)
        //    b. Generate PSI1 signature using FastCDC
        //    c. Optionally compress with zstd
        // 3. Generate manifest.json (with JSON source generator)
        // 4. Copy files to output structure
    }
}
```

**Build workflow (same code as client):**

```
patchsync build -i GameFolder -o CDNUpload

Output:
CDNUpload/
├── manifest.json              # Game manifest
├── files/
│   ├── game.exe               # Raw files for byte-range
│   └── assets.pak
├── signatures/
│   ├── game.exe.psi1          # FastCDC signatures
│   └── assets.pak.psi1
└── compressed/                # Optional zstd fallback
    ├── game.exe.zst
    └── assets.pak.zst
```

#### 5.2 Patch Command

```csharp
// PatchSync.CLI/Commands/PatchCommand.cs

public class PatchCommand
{
    public required string ManifestUrl { get; set; }
    public required string InstallPath { get; set; }
    public bool Verify { get; set; } = true;
    public int MaxConcurrency { get; set; } = 4;

    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var storage = new HttpStorageProvider(new Uri(ManifestUrl).GetLeftPart(UriPartial.Authority));
        var client = new PatchSyncClient(storage);

        var manifest = await client.GetManifestAsync(ManifestUrl, ct);
        var plan = await client.CreatePlanAsync(manifest, InstallPath, ct);

        Console.WriteLine($"Files to update: {plan.FilesToUpdate}");
        Console.WriteLine($"Download: {plan.BytesToDownload:N0} bytes");
        Console.WriteLine($"Reuse local: {plan.BytesToCopy:N0} bytes");

        await client.ApplyPatchAsync(plan, new ConsoleProgress(), ct);
    }
}
```

#### 5.3 Verify Command

```csharp
// PatchSync.CLI/Commands/VerifyCommand.cs

public class VerifyCommand
{
    public required string ManifestUrl { get; set; }
    public required string InstallPath { get; set; }
    public bool Repair { get; set; } = false;

    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        // Verify all files match manifest hashes
        // Optionally repair by re-downloading corrupted chunks
    }
}
```

#### 5.4 Info Command

```csharp
// PatchSync.CLI/Commands/InfoCommand.cs

public class InfoCommand
{
    public required string Path { get; set; }  // manifest.json or .psi1 file

    public Task<int> ExecuteAsync()
    {
        // Display manifest or signature file details
        // - Algorithm, chunk sizes, chunk count
        // - File list with sizes and strategies
    }
}
```

#### 5.5 NativeAOT Configuration

```xml
<!-- PatchSync.CLI.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <StripSymbols>true</StripSymbols>
    <OptimizationPreference>Size</OptimizationPreference>
  </PropertyGroup>
</Project>
```

**JSON Serialization (AOT-compatible):**

```csharp
// PatchSync.Common/Manifest/ManifestJsonContext.cs

[JsonSerializable(typeof(GameManifest))]
[JsonSerializable(typeof(ManifestFile))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
public partial class ManifestJsonContext : JsonSerializerContext { }
```

---

## Desync-Inspired Features

While we're not using desync directly, these features from desync are incorporated:

### Cross-File Chunk Matching (Seed Mechanism)

From desync's `FileSeed`:
- Content-addressed chunks can be found across multiple local files
- Enables chunk reuse when assets move between pak files
- `CompositeChunkSource` implements this pattern

### Parallel Assembly

From desync's `AssembleFile`:
- Multiple chunks downloaded concurrently
- Configurable concurrency limit
- Progress reporting per-chunk

### Resumable Downloads

From desync's `-k` flag:
- Track which chunks have been downloaded
- Resume from interruption without re-downloading
- Verify partial downloads via chunk hashes

### HTTP/2 Support

From desync's HTTP store:
- Enable HTTP/2 for multiplexed connections
- Connection pooling for CDN efficiency

```csharp
var handler = new SocketsHttpHandler
{
    EnableMultipleHttp2Connections = true,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
};
```

### Range Request Coalescing

Optimize HTTP byte-range requests:
- Merge adjacent ranges within threshold (4KB gap)
- Batch small chunks into single requests
- Fall back to single-range when multi-range not supported

---

## Testing Strategy

### Unit Tests (PatchSync.Tests/)

```
PatchSync.Tests/
├── FastCDCCompatibilityTests.cs  # ✅ Verify against fastcdc-go
├── ChunkerRegistryTests.cs       # Algorithm discovery
├── SignatureFormatTests.cs       # PSI1 read/write round-trip
├── ChunkingOptionsTests.cs       # Validation
└── ManifestSerializationTests.cs # JSON source generator
```

### Integration Tests

```
PatchSync.Tests/Integration/
├── DeltaScenarios/
│   ├── AppendOnlyTest.cs         # File grows at end
│   ├── InsertionTest.cs          # Data inserted in middle
│   ├── DeletionTest.cs           # Data removed
│   ├── ModificationTest.cs       # Bytes changed in place
│   └── CrossFileTest.cs          # Chunks move between files
├── StorageProviders/
│   ├── HttpProviderTests.cs      # Against test server
│   └── LocalProviderTests.cs     # Local file system
└── EndToEnd/
    └── FullPatchCycleTests.cs    # Build → CDN → Patch
```

### Benchmarks (PatchSync.Benchmarks/)

```csharp
[MemoryDiagnoser]
public class ChunkingBenchmarks
{
    [Params(1_000_000, 100_000_000, 1_000_000_000)]
    public int FileSize { get; set; }

    [Benchmark]
    public int FastCDC_Chunk() => _chunker.Chunk(_stream, _options).Count();
}
```

**Targets:**
- 1GB file chunking: <1 second (>1 GB/s)
- 10GB game directory analysis: <30 seconds
- Download throughput: Saturate connection

---

## Milestones

| Milestone | Deliverable | Status |
|-----------|-------------|--------|
| M1: Foundation | FastCDC chunker + PSI1 format + tests | ✅ Complete |
| M2: Delta Engine | IChunkSource + delta calculation + strategy selection | 🔄 Next |
| M3: Assembly | File assembly + byte-range downloads | Pending |
| M4: SDK | PatchSyncClient with full API | Pending |
| M5: CLI | Build + patch + verify commands (NativeAOT) | Pending |
| M6: Polish | Documentation, examples, perf tuning | Pending |

---

## Dependencies

**Required:**
- .NET 10 SDK (for latest performance features)

**NuGet packages:**
- `System.IO.Hashing` - XxHash3, CRC32 (built-in .NET 10)
- `System.Text.Json` - Manifest serialization (source generators)
- `ZstdSharp` - Zstandard compression (optional)
- `Spectre.Console` - CLI progress/UI (optional)
- `System.CommandLine` - CLI parsing

**No external dependencies required** - FastCDC is fully implemented in .NET.

---

---

## Phase 6: CLI Workspace System

### Goal: Structured project management with channels, versioning, and publish workflows

This phase transforms the CLI from standalone commands into a comprehensive workspace-based system that supports professional game development workflows.

### 6.1 Motivation & Problem Statement

**Current limitations:**
- Each build is standalone with no history
- No relationship between builds and uploads
- Cannot manage prod/beta/dev deployments
- No way to promote versions between channels
- No "staged" vs "published" distinction
- Cannot roll back to previous versions
- No CI/CD integration points

**Target users:**
- Game developers with multiple release channels
- CI/CD pipelines that build and publish automatically
- Teams that need rollback capability
- Developers who want to preview before publishing

---

### 6.2 Workspace Structure

```
mygame-patchsync/                      # Workspace root
├── patchsync.workspace.json           # Main configuration (required)
├── .patchsync/                        # Internal state (git-ignored)
│   ├── credentials.protected          # DPAPI-encrypted credentials (Windows)
│   ├── state.json                     # Runtime state & pending operations
│   └── cache/                         # Chunk hash cache for incremental builds
│       └── chunks/                    # Content-addressed chunk metadata
│
├── channels/                          # Channel-organized builds
│   ├── prod/                          # Production channel
│   │   ├── channel.json               # Channel config & version history
│   │   └── versions/
│   │       ├── 1.0.0/                 # Version-specific directory
│   │       │   ├── version.json       # Build metadata & status
│   │       │   ├── manifest.json      # Game manifest
│   │       │   ├── signatures/        # PSI1 signature files
│   │       │   │   └── *.sig
│   │       │   ├── files/             # Raw files (optional, for local testing)
│   │       │   └── compressed/        # Compressed fallbacks (optional)
│   │       └── 1.0.1/
│   │
│   ├── beta/
│   │   ├── channel.json
│   │   └── versions/
│   │       └── 1.1.0-beta.1/
│   │
│   └── dev/
│       ├── channel.json
│       └── versions/
│           └── nightly-20251230/
│
└── .gitignore                         # Ignore .patchsync/, credentials, large files
```

---

### 6.3 Configuration Schemas

#### 6.3.1 `patchsync.workspace.json` - Main Workspace Configuration

```json
{
  "$schema": "https://patchsync.dev/schemas/workspace.v1.json",
  "schemaVersion": 1,

  "project": {
    "name": "MyAwesomeGame",
    "id": "my-awesome-game",
    "description": "Delta patching workspace for MyAwesomeGame"
  },

  "defaults": {
    "inputPath": "C:/GameDev/MyAwesomeGame/Build/Output",
    "chunking": {
      "algorithm": "fastcdc-v1",
      "minChunkSize": 4096,
      "avgChunkSize": 16384,
      "maxChunkSize": 65536,
      "minDeltaSize": 65536
    },
    "compression": {
      "enabled": true,
      "algorithm": "zstd",
      "level": 9
    }
  },

  "channels": {
    "prod": {
      "displayName": "Production",
      "description": "Stable release channel",
      "isDefault": true,
      "versionPattern": "^\\d+\\.\\d+\\.\\d+$",
      "retainVersions": 10,
      "publish": {
        "profile": "production-cdn"
      }
    },
    "beta": {
      "displayName": "Beta",
      "description": "Pre-release testing",
      "versionPattern": "^\\d+\\.\\d+\\.\\d+-beta\\.\\d+$",
      "retainVersions": 5,
      "publish": {
        "profile": "beta-cdn",
        "prefix": "beta/"
      }
    },
    "dev": {
      "displayName": "Development",
      "description": "Internal builds",
      "versionPattern": ".*",
      "retainVersions": 3,
      "publish": {
        "profile": "dev-cdn",
        "prefix": "dev/"
      }
    }
  },

  "publishProfiles": {
    "production-cdn": {
      "type": "s3",
      "endpoint": "https://s3.amazonaws.com",
      "bucket": "mygame-cdn",
      "region": "us-east-1",
      "publicUrl": "https://cdn.mygame.com",
      "credentialSource": "environment"
    },
    "beta-cdn": {
      "type": "s3",
      "endpoint": "https://s3.amazonaws.com",
      "bucket": "mygame-cdn",
      "region": "us-east-1",
      "publicUrl": "https://beta-cdn.mygame.com",
      "credentialSource": "environment"
    },
    "dev-cdn": {
      "type": "s3",
      "endpoint": "https://nyc3.digitaloceanspaces.com",
      "bucket": "mygame-dev",
      "region": "nyc3",
      "publicUrl": "https://dev.mygame.com",
      "credentialSource": "stored"
    }
  }
}
```

**⚠️ CRITICAL: JSON Source Generation Required**

All configuration types MUST use `System.Text.Json` source generators for NativeAOT compatibility:

```csharp
[JsonSerializable(typeof(WorkspaceConfig))]
[JsonSerializable(typeof(ChannelConfig))]
[JsonSerializable(typeof(VersionMetadata))]
[JsonSerializable(typeof(WorkspaceState))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class WorkspaceJsonContext : JsonSerializerContext { }
```

#### 6.3.2 `channels/{channel}/channel.json` - Channel State

```json
{
  "schemaVersion": 1,
  "channelId": "prod",
  "displayName": "Production",

  "current": {
    "version": "1.0.1",
    "publishedAt": "2025-12-28T15:30:00Z",
    "manifestUrl": "https://cdn.mygame.com/manifest.json"
  },

  "history": [
    {
      "version": "1.0.1",
      "status": "live",
      "builtAt": "2025-12-28T14:00:00Z",
      "publishedAt": "2025-12-28T15:30:00Z"
    },
    {
      "version": "1.0.0",
      "status": "superseded",
      "builtAt": "2025-12-20T10:00:00Z",
      "publishedAt": "2025-12-20T12:00:00Z"
    }
  ],

  "rollbackAvailable": ["1.0.0"]
}
```

#### 6.3.3 `channels/{channel}/versions/{version}/version.json` - Build Metadata

```json
{
  "schemaVersion": 1,
  "version": "1.0.1",
  "channel": "prod",

  "build": {
    "builtAt": "2025-12-28T14:00:00Z",
    "builtBy": "ci-build-agent-01",
    "buildNumber": "456",
    "duration": 127.5,
    "patchsyncVersion": "2.0.0"
  },

  "input": {
    "path": "C:/GameDev/MyAwesomeGame/Build/Output",
    "hash": "sha256:abc123def456...",
    "fileCount": 1847,
    "totalSize": 52428800000
  },

  "output": {
    "manifestHash": "sha256:manifest123...",
    "signatureCount": 1842,
    "signatureTotalSize": 2621440,
    "compressedAvailable": true,
    "compressedTotalSize": 35200000000
  },

  "chunking": {
    "algorithm": "fastcdc-v1",
    "minChunkSize": 4096,
    "avgChunkSize": 16384,
    "maxChunkSize": 65536,
    "totalChunks": 3215847
  },

  "status": "staged",

  "publish": {
    "profile": "production-cdn",
    "publishedAt": null,
    "manifestUrl": null,
    "uploadedFiles": 0,
    "uploadedBytes": 0
  },

  "comparison": {
    "previousVersion": "1.0.0",
    "newFiles": 12,
    "modifiedFiles": 45,
    "deletedFiles": 10,
    "unchangedFiles": 1780,
    "estimatedDeltaDownload": 524288000
  },

  "notes": "Bug fixes and performance improvements",
  "tags": ["hotfix", "perf"]
}
```

---

### 6.4 Version Status Lifecycle

```
                    ┌────────────┐
                    │   BUILD    │
                    └─────┬──────┘
                          │
                          ▼
┌─────────────────────────────────────────┐
│              STAGED                      │
│  - Signatures generated                  │
│  - Manifest created                      │
│  - Not yet uploaded to CDN               │
│  - Can be modified (notes, tags)         │
│  - Can be deleted without side effects   │
└─────────────────┬───────────────────────┘
                  │
                  │ patchsync publish
                  ▼
┌─────────────────────────────────────────┐
│            PUBLISHED                     │
│  - Uploaded to CDN                       │
│  - Accessible via manifest URL           │
│  - Becomes "live" for this channel       │
│  - Previous live version → SUPERSEDED    │
└─────────────────┬───────────────────────┘
                  │
                  │ (new version published)
                  ▼
┌─────────────────────────────────────────┐
│           SUPERSEDED                     │
│  - Still on CDN (for rollback)           │
│  - Not the current version               │
│  - Can be promoted to LIVE via rollback  │
└─────────────────┬───────────────────────┘
                  │
                  │ patchsync clean
                  ▼
┌─────────────────────────────────────────┐
│            ARCHIVED                      │
│  - Removed from CDN                      │
│  - Metadata retained locally             │
│  - Cannot be rolled back to              │
└─────────────────────────────────────────┘
```

**Status enum:**

```csharp
public enum VersionStatus
{
    Building,      // Build in progress (lock file exists)
    Staged,        // Built, not published
    Publishing,    // Upload in progress
    Live,          // Current version for this channel
    Superseded,    // Was live, now replaced by newer
    Archived,      // Removed from CDN, metadata only
    Failed         // Build or publish failed
}
```

---

### 6.5 Command Specifications

#### `patchsync init`

Initialize a new workspace or upgrade existing configuration.

```bash
patchsync init [--path <dir>] [--name <project-name>]

Options:
  --path <dir>           Directory to initialize (default: current)
  --name <name>          Project name
  --from-existing        Migrate from existing patchsync.json
  --channels <list>      Comma-separated channel names (default: prod,beta,dev)

Examples:
  patchsync init --name "MyGame" --channels prod,beta
  patchsync init --from-existing
```

**Implementation notes:**
- Interactive wizard mode when no arguments provided
- Validate directory doesn't already have workspace
- Create .gitignore with recommended patterns

#### `patchsync build`

Build signatures and manifest for a version.

```bash
patchsync build --channel <channel> --version <version> [options]

Required:
  -c, --channel <name>   Target channel (prod, beta, dev)
  -v, --version <ver>    Version string (must match channel pattern)

Input:
  -i, --input <path>     Input directory (overrides workspace default)

Chunking (overrides workspace defaults):
  -a, --algorithm <alg>  Chunking algorithm
  --min-chunk <N>        Minimum chunk size
  --avg-chunk <N>        Average chunk size
  --max-chunk <N>        Maximum chunk size

Options:
  --compress             Generate compressed fallbacks (default: true)
  --compare <version>    Compare against specific version
  --notes <text>         Build notes/description
  --tags <list>          Comma-separated tags
  --dry-run              Show what would be built without building
  --force                Overwrite existing version

Output:
  --json                 Output build result as JSON

Examples:
  patchsync build -c prod -v 1.0.0 -i C:/Game/Build
  patchsync build -c beta -v 1.1.0-beta.1 --notes "New features"
```

**⚠️ CRITICAL: Build Locking**

Prevent concurrent builds to the same version:

```csharp
// channels/{channel}/versions/{version}/.build.lock
{
  "lockedBy": "machine-name",
  "lockedAt": "2025-12-30T10:00:00Z",
  "pid": 12345
}
```

Lock acquisition must be atomic. Consider file-based locking with retry logic.

#### `patchsync publish`

Publish a staged version to CDN.

```bash
patchsync publish --channel <channel> [--version <version>] [options]

Required:
  -c, --channel <name>   Channel to publish

Options:
  -v, --version <ver>    Version to publish (default: latest staged)
  --profile <name>       Override publish profile
  --dry-run              Show what would be uploaded
  --force                Force republish even if already published
  --parallel <N>         Upload parallelism (default: 4)

Examples:
  patchsync publish -c prod
  patchsync publish -c prod -v 1.0.1 --dry-run
```

**Implementation notes:**
- Must validate version is in "staged" status
- Update channel.json atomically after successful upload
- Support resumable uploads (track uploaded files in version.json)

#### `patchsync promote`

Promote a version from one channel to another.

```bash
patchsync promote <source-channel>:<version> <target-channel> [options]

Arguments:
  <source>               Source channel:version (e.g., beta:1.1.0-beta.3)
  <target>               Target channel (e.g., prod)

Options:
  --as <version>         Rename version for target (e.g., --as 1.1.0)
  --publish              Immediately publish after promoting
  --dry-run              Show what would be promoted

Examples:
  patchsync promote beta:1.1.0-beta.3 prod --as 1.1.0
  patchsync promote dev:nightly-20251230 beta --as 1.2.0-beta.1 --publish
```

**Implementation notes:**
- Copy version directory, don't move (preserve source)
- Update version.json with new channel/version
- Validate target version matches channel's versionPattern

#### `patchsync rollback`

Roll back a channel to a previous version.

```bash
patchsync rollback --channel <channel> [--to <version>] [options]

Required:
  -c, --channel <name>   Channel to roll back

Options:
  --to <version>         Target version (default: previous)
  --dry-run              Show what would happen

Examples:
  patchsync rollback -c prod
  patchsync rollback -c prod --to 1.0.0
```

**⚠️ CRITICAL: Rollback is CDN-level**

Rollback updates channel.json's `current` pointer and potentially re-uploads the manifest. The old version's files must still exist on CDN (not cleaned).

#### `patchsync status`

Show current workspace status.

```bash
patchsync status [options]

Options:
  -c, --channel <name>   Filter by channel
  --staged               Show only staged (unpublished) versions
  --json                 Output as JSON

Examples:
  patchsync status
  patchsync status -c prod
  patchsync status --staged --json
```

#### `patchsync list`

List versions and channels.

```bash
patchsync list [channels|versions] [options]

Subcommands:
  channels               List all channels
  versions               List versions

Options:
  -c, --channel <name>   Filter by channel (for versions)
  --limit <N>            Max results (default: 20)
  --status <status>      Filter by status (staged, live, superseded)
  --json                 Output as JSON

Examples:
  patchsync list channels
  patchsync list versions -c prod --limit 10
  patchsync list versions --status staged
```

#### `patchsync diff`

Compare two versions.

```bash
patchsync diff <version1> <version2> [options]

Arguments:
  <version1>             First version (channel:version or just version)
  <version2>             Second version

Options:
  --files                Show file-level differences
  --stats                Show statistics only (default)
  --json                 Output as JSON

Examples:
  patchsync diff prod:1.0.0 prod:1.0.1
  patchsync diff beta:1.1.0-beta.1 beta:1.1.0-beta.2 --files
```

#### `patchsync clean`

Remove old versions to save space.

```bash
patchsync clean [options]

Options:
  -c, --channel <name>   Clean specific channel only
  --keep <N>             Keep N most recent versions (overrides config)
  --before <date>        Remove versions before date
  --status <status>      Clean only versions with status
  --dry-run              Show what would be cleaned

Examples:
  patchsync clean -c dev --keep 3
  patchsync clean --status superseded --dry-run
```

**⚠️ CRITICAL: CDN Cleanup**

When cleaning published versions, must also remove from CDN. This is destructive and should require `--confirm` flag.

---

### 6.6 CI/CD Integration

#### Environment Variables

```bash
# Workspace location (optional, defaults to current directory)
PATCHSYNC_WORKSPACE=/path/to/workspace

# Credentials per profile (naming: PATCHSYNC_{PROFILE}_*)
PATCHSYNC_PRODUCTION_CDN_ACCESS_KEY=AKIA...
PATCHSYNC_PRODUCTION_CDN_SECRET_KEY=...
PATCHSYNC_BETA_CDN_ACCESS_KEY=AKIA...
PATCHSYNC_BETA_CDN_SECRET_KEY=...

# Build metadata injection
PATCHSYNC_BUILD_NUMBER=456
PATCHSYNC_BUILD_AGENT=ci-agent-01
```

#### Exit Codes

```csharp
public enum ExitCode
{
    Success = 0,
    GeneralError = 1,
    InvalidArguments = 2,
    WorkspaceNotFound = 3,
    VersionAlreadyExists = 4,
    VersionNotFound = 5,
    ChannelNotFound = 6,
    PublishFailed = 7,
    ValidationFailed = 8,
    CredentialsMissing = 9,
    NetworkError = 10,
    LockConflict = 11,
    CancelledByUser = 130
}
```

#### Machine-Readable Output (--json)

All commands support `--json` for CI/CD parsing:

```json
{
  "success": true,
  "operation": "build",
  "exitCode": 0,
  "result": {
    "channel": "prod",
    "version": "1.0.1",
    "status": "staged",
    "files": 1847,
    "totalSize": 52428800000,
    "duration": 127.5,
    "manifestPath": "channels/prod/versions/1.0.1/manifest.json"
  },
  "warnings": [],
  "errors": []
}
```

#### GitHub Actions Example

```yaml
name: Build and Publish
on:
  push:
    tags: ['v*']

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Build Game
        run: ./build.sh

      - name: Build PatchSync
        run: |
          patchsync build \
            -c prod \
            -v ${{ github.ref_name }} \
            -i ./build/output \
            --json > build-result.json

      - name: Publish to CDN
        env:
          PATCHSYNC_PRODUCTION_CDN_ACCESS_KEY: ${{ secrets.CDN_ACCESS_KEY }}
          PATCHSYNC_PRODUCTION_CDN_SECRET_KEY: ${{ secrets.CDN_SECRET_KEY }}
        run: |
          patchsync publish -c prod --json > publish-result.json
```

---

### 6.7 Implementation Complexity & Critical Areas

#### 🔴 HIGH COMPLEXITY: Atomic State Updates

**Problem:** Multiple JSON files must be updated together (version.json, channel.json, state.json). Partial writes can corrupt state.

**Solution:**
1. Write to `.tmp` files first
2. Validate JSON is parseable
3. Rename atomically (File.Move with overwrite)
4. For multi-file updates, use a transaction log in state.json

```csharp
public class WorkspaceTransaction : IDisposable
{
    private readonly List<(string Target, string TempPath)> _pending = new();

    public void Stage(string targetPath, string content)
    {
        var tempPath = targetPath + ".tmp";
        File.WriteAllText(tempPath, content);
        _pending.Add((targetPath, tempPath));
    }

    public void Commit()
    {
        foreach (var (target, temp) in _pending)
            File.Move(temp, target, overwrite: true);
    }

    public void Rollback()
    {
        foreach (var (_, temp) in _pending)
            if (File.Exists(temp)) File.Delete(temp);
    }
}
```

#### 🔴 HIGH COMPLEXITY: Concurrent Build Prevention

**Problem:** Two CI jobs could build the same version simultaneously, causing corruption.

**Solution:**
1. Create lock file with PID and timestamp
2. Check lock file before build starts
3. Implement lock timeout (stale lock detection)
4. Use file system atomic create (FileMode.CreateNew)

```csharp
public class BuildLock : IDisposable
{
    private readonly string _lockPath;
    private FileStream? _lockStream;

    public static BuildLock? TryAcquire(string versionPath, TimeSpan timeout)
    {
        var lockPath = Path.Combine(versionPath, ".build.lock");

        // Check for stale lock
        if (File.Exists(lockPath))
        {
            var lockInfo = ReadLockInfo(lockPath);
            if (DateTime.UtcNow - lockInfo.LockedAt > timeout)
            {
                // Stale lock, remove it
                File.Delete(lockPath);
            }
            else
            {
                return null; // Lock held by another process
            }
        }

        // Atomic lock creation
        try
        {
            var stream = new FileStream(lockPath,
                FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var lockInfo = new LockInfo(
                Environment.MachineName,
                DateTime.UtcNow,
                Environment.ProcessId);
            // Write lock info...
            return new BuildLock(lockPath, stream);
        }
        catch (IOException)
        {
            return null; // Another process beat us
        }
    }
}
```

#### 🔴 HIGH COMPLEXITY: Resumable Uploads

**Problem:** Large uploads (50GB+) can fail mid-way. Must resume without re-uploading.

**Solution:**
Track uploaded files in version.json's `publish` section:

```json
"publish": {
  "uploadedFiles": ["manifest.json", "signatures/file1.sig", ...],
  "uploadedBytes": 1234567890,
  "lastUploadAt": "2025-12-30T10:00:00Z"
}
```

On resume:
1. Load list of uploaded files
2. Verify each file exists on CDN (HEAD request)
3. Continue from first missing file

#### 🟡 MEDIUM COMPLEXITY: Version Pattern Validation

**Problem:** Channels define version patterns (regex). Must validate before build.

**Solution:**
```csharp
public bool ValidateVersion(string channel, string version)
{
    var config = _workspace.Channels[channel];
    if (string.IsNullOrEmpty(config.VersionPattern))
        return true;

    var regex = new Regex(config.VersionPattern);
    if (!regex.IsMatch(version))
    {
        _logger.Error($"Version '{version}' does not match pattern '{config.VersionPattern}' for channel '{channel}'");
        return false;
    }
    return true;
}
```

**⚠️ Note:** Regex compilation should be cached. Consider source-generated regex for AOT.

#### 🟡 MEDIUM COMPLEXITY: Promote with Rename

**Problem:** Promoting beta:1.1.0-beta.3 to prod as 1.1.0 requires updating all references.

**Solution:**
1. Copy version directory to new location
2. Update version.json: change `version` and `channel` fields
3. Re-generate manifest.json with new version
4. DO NOT regenerate signatures (content unchanged)

#### 🟡 MEDIUM COMPLEXITY: Credential Resolution

**Problem:** Multiple credential sources (environment, stored, profile-specific).

**Resolution order:**
1. Profile-specific env vars: `PATCHSYNC_{PROFILE_UPPER}_ACCESS_KEY`
2. Generic env vars: `PATCHSYNC_S3_ACCESS_KEY`
3. Stored credentials (DPAPI-encrypted)
4. Interactive prompt (if TTY available)

---

### 6.8 Testing Requirements

#### Unit Tests (Required)

| Test | Description | Priority |
|------|-------------|----------|
| `WorkspaceConfigSerializationTests` | Round-trip JSON serialization | 🔴 Critical |
| `VersionStatusTransitionTests` | Valid state transitions only | 🔴 Critical |
| `VersionPatternValidationTests` | Regex pattern matching | 🟡 High |
| `ChannelConfigTests` | Channel defaults, inheritance | 🟡 High |
| `BuildLockTests` | Lock acquire/release/timeout | 🔴 Critical |
| `WorkspaceTransactionTests` | Atomic commit/rollback | 🔴 Critical |

#### Integration Tests (Required)

| Test | Description | Priority |
|------|-------------|----------|
| `InitWorkspaceTests` | Create workspace, validate structure | 🔴 Critical |
| `BuildVersionTests` | Full build cycle, verify outputs | 🔴 Critical |
| `PublishVersionTests` | Upload to mock S3, verify status | 🔴 Critical |
| `PromoteVersionTests` | Cross-channel promotion | 🟡 High |
| `RollbackTests` | Rollback and verify channel state | 🟡 High |
| `ConcurrentBuildTests` | Two builds, one should fail | 🔴 Critical |
| `ResumableUploadTests` | Interrupt upload, resume | 🟡 High |

#### Edge Cases to Test

1. **Workspace in git submodule** - Paths may be relative
2. **Unicode in paths** - Project names with special characters
3. **Very long paths** - Windows 260 char limit
4. **Disk full during build** - Graceful failure
5. **Network timeout during publish** - Resume correctly
6. **Corrupted state.json** - Recovery mechanism
7. **Clock skew** - Timestamps from different machines

---

### 6.9 Files to Create

| File | Purpose |
|------|---------|
| `PatchSync.CLI/Workspace/WorkspaceConfig.cs` | Main configuration model |
| `PatchSync.CLI/Workspace/ChannelConfig.cs` | Channel configuration |
| `PatchSync.CLI/Workspace/VersionMetadata.cs` | Version build metadata |
| `PatchSync.CLI/Workspace/WorkspaceState.cs` | Runtime state |
| `PatchSync.CLI/Workspace/WorkspaceManager.cs` | Core workspace operations |
| `PatchSync.CLI/Workspace/BuildLock.cs` | Concurrent build prevention |
| `PatchSync.CLI/Workspace/WorkspaceTransaction.cs` | Atomic multi-file updates |
| `PatchSync.CLI/Workspace/WorkspaceJsonContext.cs` | JSON source generation |
| `PatchSync.CLI/Commands/InitCommand.cs` | Workspace initialization |
| `PatchSync.CLI/Commands/StatusCommand.cs` | Status display |
| `PatchSync.CLI/Commands/ListCommand.cs` | List versions/channels |
| `PatchSync.CLI/Commands/PromoteCommand.cs` | Channel promotion |
| `PatchSync.CLI/Commands/RollbackCommand.cs` | Version rollback |
| `PatchSync.CLI/Commands/DiffCommand.cs` | Version comparison |
| `PatchSync.CLI/Commands/CleanCommand.cs` | Cleanup old versions |

### 6.10 Files to Modify

| File | Changes |
|------|---------|
| `BuildCommand.cs` | Add workspace awareness, channel targeting, status tracking |
| `UploadCommand.cs` | Rename to `PublishCommand.cs`, integrate with workspace |
| `Program.cs` | Add new command routing |
| `CommandRegistry.cs` | Register new commands |

---

### 6.11 Implementation Order

1. **Workspace foundation** - WorkspaceConfig, WorkspaceManager, JSON context
2. **Init command** - Create workspace structure
3. **Build integration** - Update BuildCommand for workspace mode
4. **Status/List commands** - Visibility into workspace state
5. **Publish command** - Rename UploadCommand, add status tracking
6. **Promote command** - Cross-channel promotion
7. **Rollback command** - Version rollback
8. **Diff command** - Version comparison
9. **Clean command** - Old version cleanup
10. **CI/CD polish** - Exit codes, --json output, env vars

---

## Open Questions

1. ~~**Index format extension**: Do we need to extend .caibx for byte offsets?~~
   **Resolved**: Using custom PSI1 format with explicit offsets.

2. **Chunk storage model**: For very large games (100GB+), should we support chunk store as option?

3. **Parallel chunking**: Should client-side chunking be parallelized?
   - Current FastCDC is single-threaded but >1 GB/s
   - Network is the bottleneck, not CPU

4. **Signature caching**: Should we cache chunk hashes of local files between sessions?
   - Could speed up repeated patches
   - Need invalidation strategy

5. **Repair mode**: How to handle corrupted local files?
   - Re-chunk and identify bad chunks
   - Re-download only corrupted chunks

6. **GUI tool**: Should we build a GUI in addition to CLI?
   - Could use AvaloniaUI for cross-platform
   - Or provide SDK for launcher developers to embed

7. **Workspace format versioning**: How to handle workspace schema upgrades?
   - Migration scripts per version
   - Backward compatibility window
