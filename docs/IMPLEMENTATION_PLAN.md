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
