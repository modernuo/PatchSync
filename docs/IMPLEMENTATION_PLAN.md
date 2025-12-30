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
| **Chunking Algorithm** | FastCDC | Simplest (~25 lines core), normalized chunks, >1 GB/s |
| **Vectorization** | No (MVP) | Network is bottleneck, not CPU |
| **Build Tooling** | .NET port (same as client) | Identical algorithm everywhere |
| **Index Format** | Custom PSI1 | Algorithm-aware headers, version-tagged |
| **Desync Integration** | None (reference only) | Use for research, not runtime |
| **Cross-file Matching** | IChunkSource abstraction | Local files as chunk sources |
| **Compressed Fallback** | Automatic selection | Delta vs compressed size comparison |

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
│   ├── PatchSync.Common/          # Shared data structures
│   │   ├── Chunking/              # CDC algorithm
│   │   ├── Index/                 # .caibx format handling
│   │   ├── Manifest/              # Game manifest structures
│   │   ├── Hashing/               # SHA256, XxHash3
│   │   └── Storage/               # Storage provider abstractions
│   │
│   ├── PatchSync.SDK/             # Core client library
│   │   ├── Client/                # Main PatchSyncClient
│   │   ├── Delta/                 # Delta calculation
│   │   ├── Assembly/              # File assembly from chunks
│   │   └── Download/              # HTTP download handling
│   │
│   └── PatchSync.CLI/             # Command-line interface
│       ├── Commands/
│       │   ├── Build/             # Build signatures/manifest
│       │   ├── Patch/             # Apply patches
│       │   └── Verify/            # Verify installation
│       └── Program.cs
│
├── tools/
│   └── desync/                    # Desync CLI wrapper scripts
│
├── tests/
│   ├── PatchSync.Common.Tests/
│   ├── PatchSync.SDK.Tests/
│   └── PatchSync.Integration.Tests/
│
└── docs/
    ├── ANALYSIS.md
    ├── DECISIONS.md
    ├── IMPLEMENTATION_PLAN.md
    └── SIMILAR_TOOLS_RESEARCH.md
```

---

## Phase 1: Foundation

### Goal: Core data structures and CDC algorithm

#### 1.1 IChunker Interface and Types

Define the pluggable chunker abstraction:

```csharp
// PatchSync.Common/Chunking/IChunker.cs

public interface IChunker
{
    string AlgorithmId { get; }
    IEnumerable<ChunkBoundary> Chunk(Stream input, ChunkingOptions options);
}

public readonly record struct ChunkBoundary(long Offset, int Length, byte[] Hash);

public sealed class ChunkingOptions
{
    public int MinSize { get; init; } = 4096;
    public int AverageSize { get; init; } = 16384;
    public int MaxSize { get; init; } = 65536;
}
```

#### 1.2 FastCDC Chunker Port (~50 lines core)

Port fastcdc-go algorithm to C# (NOT desync's buzhash):

```csharp
// PatchSync.Common/Chunking/FastCDCChunker.cs

public sealed class FastCDCChunker : IChunker
{
    public string AlgorithmId => "fastcdc-v1";

    // Gear hash table - 256 random 64-bit values (same seed as fastcdc-go)
    private static readonly ulong[] GearTable = GenerateGearTable(seed: 0);

    public IEnumerable<ChunkBoundary> Chunk(Stream input, ChunkingOptions options)
    {
        // Normalized chunking with two masks:
        // - MaskS (strict): Used below average size - harder to find boundary
        // - MaskL (loose): Used above average size - easier to find boundary
        // This creates uniform chunk size distribution around the average
    }

    private static int FindBoundary(ReadOnlySpan<byte> data, int minSize, int avgSize, int maxSize)
    {
        ulong fingerprint = 0;
        int n = Math.Min(data.Length, maxSize);

        // Skip minimum size (no boundary checks needed)
        for (int i = 0; i < Math.Min(n, minSize); i++)
            fingerprint = (fingerprint << 1) + GearTable[data[i]];

        // Below average: use strict mask
        ulong maskS = CalculateMask(avgSize, normalization: 2);
        for (int i = minSize; i < Math.Min(n, avgSize); i++)
        {
            fingerprint = (fingerprint << 1) + GearTable[data[i]];
            if ((fingerprint & maskS) == 0)
                return i + 1;
        }

        // Above average: use loose mask
        ulong maskL = CalculateMask(avgSize, normalization: -2);
        for (int i = avgSize; i < n; i++)
        {
            fingerprint = (fingerprint << 1) + GearTable[data[i]];
            if ((fingerprint & maskL) == 0)
                return i + 1;
        }

        return n; // Force boundary at max size
    }
}
```

**Verification**: Generate test vectors from fastcdc-go, verify .NET produces identical boundaries.

#### 1.3 ChunkerRegistry

```csharp
// PatchSync.Common/Chunking/ChunkerRegistry.cs

public sealed class ChunkerRegistry : IChunkerRegistry
{
    private readonly Dictionary<string, Func<IChunker>> _factories = new()
    {
        ["fastcdc-v1"] = () => new FastCDCChunker(),
        // Future: ["buzhash-casync-v1"] = () => new BuzhashChunker(),
    };

    public IReadOnlyList<string> SupportedAlgorithms => _factories.Keys.ToList();

    public bool TryGetChunker(string algorithmId, out IChunker chunker)
    {
        if (_factories.TryGetValue(algorithmId, out var factory))
        {
            chunker = factory();
            return true;
        }
        chunker = null!;
        return false;
    }

    public IChunker GetPreferred(IEnumerable<string> available)
    {
        // Return first supported algorithm from available list
        foreach (var alg in available)
        {
            if (TryGetChunker(alg, out var chunker))
                return chunker;
        }
        throw new NotSupportedException("No supported chunking algorithms found");
    }
}
```

#### 1.4 PSI1 Signature Format

```csharp
// PatchSync.Common/Signatures/SignatureFile.cs

public sealed class SignatureFile
{
    public const uint Magic = 0x31495350; // "PSI1" little-endian

    public string AlgorithmId { get; init; }
    public byte AlgorithmVersion { get; init; }
    public int MinSize { get; init; }
    public int AverageSize { get; init; }
    public int MaxSize { get; init; }
    public IReadOnlyList<SignatureChunk> Chunks { get; init; }

    public static SignatureFile Read(Stream input) { /* ... */ }
    public void Write(Stream output) { /* ... */ }
}

public readonly record struct SignatureChunk(
    long Offset,    // Byte offset in file
    int Length,     // Chunk length
    byte[] Hash     // SHA256 hash (32 bytes)
);
```

#### 1.5 Manifest Structure

```csharp
// PatchSync.Common/Manifest/GameManifest.cs

public sealed class GameManifest
{
    public string Version { get; init; }
    public DateTime BuildDate { get; init; }
    public IReadOnlyList<string> SupportedAlgorithms { get; init; } // e.g., ["fastcdc-v1"]
    public string PreferredAlgorithm { get; init; }
    public string? FallbackUrl { get; init; }   // De novo install archive
    public IReadOnlyList<ManifestFile> Files { get; init; }
}

public sealed class ManifestFile
{
    public string Path { get; init; }
    public long Size { get; init; }
    public long CompressedSize { get; init; }
    public string Hash { get; init; }           // SHA256
    public string SignatureUrl { get; init; }   // .psi1 signature file
    public string? CompressedUrl { get; init; } // Optional .zst compressed file
    public UpdateStrategy Strategy { get; init; }
}

public enum UpdateStrategy
{
    Delta,              // Use CDC delta patching
    FullCompressed,     // Always download .zst
    FullUncompressed,   // Always download raw
    HashCheck,          // Download if hash mismatch (small files)
    Skip                // Never update (user files)
}
```

#### 1.6 Storage Provider Abstraction

```csharp
// PatchSync.Common/Storage/IStorageProvider.cs

public interface IStorageProvider
{
    Task<Stream> GetAsync(string path, CancellationToken ct = default);

    Task<Stream> GetRangeAsync(
        string path,
        long start,
        long end,
        CancellationToken ct = default);

    Task<Stream> GetMultiRangeAsync(
        string path,
        IEnumerable<(long Start, long End)> ranges,
        CancellationToken ct = default);

    bool SupportsMultiRange { get; }
}

// Implementations
public class HttpStorageProvider : IStorageProvider { }
public class S3StorageProvider : IStorageProvider { }
public class LocalStorageProvider : IStorageProvider { }
```

---

## Phase 2: Delta Engine

### Goal: Client-side delta calculation, cross-file matching, and download strategy

#### 2.1 Cross-File Chunk Source

```csharp
// PatchSync.SDK/Sources/IChunkSource.cs

public interface IChunkSource
{
    bool TryGetChunk(ReadOnlySpan<byte> hash, out ChunkLocation location);
    IEnumerable<ChunkMatch> FindMatches(IEnumerable<SignatureChunk> needed);
}

public readonly record struct ChunkLocation(string FilePath, long Offset, int Length);
public readonly record struct ChunkMatch(SignatureChunk Needed, ChunkLocation Source);
```

```csharp
// PatchSync.SDK/Sources/LocalFileSource.cs

public sealed class LocalFileSource : IChunkSource
{
    private readonly string _filePath;
    private readonly Dictionary<byte[], List<ChunkLocation>> _chunkIndex;

    public static async Task<LocalFileSource> BuildAsync(
        string filePath,
        IChunker chunker,
        ChunkingOptions options,
        CancellationToken ct = default)
    {
        // Chunk local file and build hash -> locations map
        // Same hash can appear multiple times (duplicate chunks)
    }

    public bool TryGetChunk(ReadOnlySpan<byte> hash, out ChunkLocation location)
    {
        // Return first matching location
    }
}
```

```csharp
// PatchSync.SDK/Sources/CompositeChunkSource.cs

public sealed class CompositeChunkSource : IChunkSource
{
    private readonly List<IChunkSource> _sources = new();

    public void AddSource(IChunkSource source) => _sources.Add(source);

    public bool TryGetChunk(ReadOnlySpan<byte> hash, out ChunkLocation location)
    {
        // Try each source in order (target file first, then other local files)
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

#### 2.2 Chunk Index Builder

```csharp
// PatchSync.SDK/Delta/LocalChunkIndex.cs

public sealed class LocalChunkIndex
{
    private readonly Dictionary<byte[], long> _chunks;

    public static async Task<LocalChunkIndex> BuildAsync(
        string filePath,
        IChunker chunker,
        ChunkingOptions options,
        CancellationToken ct = default)
    {
        // Chunk local file and build hash -> offset map
    }

    public bool TryGetOffset(ReadOnlySpan<byte> hash, out long offset) { /* ... */ }
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

## Phase 5: CLI Tools (Week 9-10)

### Goal: Build and patch commands

#### 5.1 Build Command (wraps desync)

```csharp
// PatchSync.CLI/Commands/Build/BuildCommand.cs

[Command("build")]
public class BuildCommand
{
    [Option("-i|--input")]
    public string InputPath { get; set; }

    [Option("-o|--output")]
    public string OutputPath { get; set; }

    [Option("-m|--manifest")]
    public string? InputManifest { get; set; }  // Optional overrides

    public async Task<int> ExecuteAsync()
    {
        // 1. Scan input directory
        // 2. For each file:
        //    a. Determine update strategy
        //    b. Run desync to generate .caibx
        //    c. Optionally compress with zstd
        // 3. Generate manifest.json
    }
}
```

**Desync wrapper script** (tools/desync/build.ps1):

```powershell
param(
    [string]$InputFile,
    [string]$OutputIndex,
    [string]$ChunkStore
)

desync make $OutputIndex $ChunkStore $InputFile
```

#### 5.2 Patch Command

```csharp
// PatchSync.CLI/Commands/Patch/PatchCommand.cs

[Command("patch")]
public class PatchCommand
{
    [Option("-u|--url")]
    public string ManifestUrl { get; set; }

    [Option("-p|--path")]
    public string InstallPath { get; set; }

    public async Task<int> ExecuteAsync()
    {
        var storage = new HttpStorageProvider(_options.BaseUrl);
        var client = new PatchSyncClient(storage);

        var manifest = await client.GetManifestAsync(ManifestUrl);
        var plan = await client.CreatePlanAsync(manifest, InstallPath);

        Console.WriteLine($"Files to update: {plan.FilesToUpdate}");
        Console.WriteLine($"Download size: {plan.DownloadSize}");

        await client.ApplyPatchAsync(plan, new ConsoleProgress());
    }
}
```

---

## Desync Integration Details

### Build-Time Usage

The build process uses desync CLI directly. No P/Invoke or shared library needed.

**Build workflow:**

```
1. Developer runs: patchsync build -i GameFolder -o CDNUpload

2. For each file in GameFolder:
   a. Determine strategy (delta, compressed, skip)
   b. If delta:
      - Run: desync make GameFile.caibx ChunkStore GameFile
      - This generates .caibx index and chunks
   c. Copy original file to output (for byte-range requests)
   d. Optionally compress with zstd

3. Generate manifest.json with all file metadata

4. Upload CDNUpload folder to CDN
```

**Why this works:**
- Build servers are developer machines (can install desync)
- No need to embed desync in .NET
- Leverage desync's battle-tested chunking
- Output is just static files

### Client-Side Port

The game launcher uses a .NET port of the CDC algorithm:

**What to port from desync:**
1. `chunker.go` → `BuzhashChunker.cs` (~200 lines)
2. Index parsing (read .caibx files) (~100 lines)
3. Hash computation (SHA512/256) (~50 lines)

**What NOT to port:**
- Chunk store (we use byte-ranges instead)
- Seeds (simplified for game patching)
- S3/GCS backends (we have HttpStorageProvider)
- CLI infrastructure

### Ensuring Algorithm Compatibility

**Critical**: The .NET chunker MUST produce identical chunks to desync.

Test strategy:
```csharp
[Test]
public async Task Chunker_ProducesIdenticalBoundaries_ToDesync()
{
    // Generate chunks with .NET implementation
    var dotnetChunks = await ChunkFileAsync("testfile.bin");

    // Generate chunks with desync CLI
    var desyncChunks = await RunDesyncMakeAsync("testfile.bin");

    // Compare
    Assert.That(dotnetChunks, Is.EqualTo(desyncChunks));
}
```

Key implementation details to match:
- Hash table values (must be identical)
- Discriminator formula (floating point precision)
- Boundary condition (hash % discriminator == discriminator - 1)
- Window rotation direction

---

## Testing Strategy

### Unit Tests

```
PatchSync.Common.Tests/
├── Chunking/
│   ├── BuzhashChunkerTests.cs    # Boundary detection
│   ├── HashTableTests.cs         # Hash table verification
│   └── CompatibilityTests.cs     # desync compatibility
├── Index/
│   └── CaibxIndexTests.cs        # Parse/write round-trip
└── Manifest/
    └── GameManifestTests.cs      # Serialization
```

### Integration Tests

```
PatchSync.Integration.Tests/
├── DeltaScenarios/
│   ├── AppendOnlyTest.cs         # File grows at end
│   ├── InsertionTest.cs          # Data inserted in middle
│   ├── DeletionTest.cs           # Data removed
│   └── ModificationTest.cs       # Bytes changed in place
├── StorageProviders/
│   ├── HttpProviderTests.cs      # Against test server
│   └── S3ProviderTests.cs        # Against LocalStack
└── EndToEnd/
    └── FullPatchCycleTests.cs    # Build → CDN → Patch
```

### Performance Tests

- 1GB file chunking: Target <1 second
- 10GB game directory: Target <30 seconds analysis
- Download throughput: Saturate connection

---

## Milestones

| Milestone | Target | Deliverable |
|-----------|--------|-------------|
| M1: Foundation | Week 2 | CDC chunker + index parsing + tests |
| M2: Delta Engine | Week 4 | Local chunk index + delta calculation |
| M3: Assembly | Week 6 | File assembly + byte-range downloads |
| M4: SDK | Week 8 | PatchSyncClient with full API |
| M5: CLI | Week 10 | Build + patch commands |
| M6: Polish | Week 12 | Documentation, examples, perf tuning |

---

## Dependencies

**Required:**
- .NET 8.0 SDK
- desync CLI (build server only)
- zstd CLI (optional, for compression)

**NuGet packages:**
- `System.IO.Hashing` (XxHash, CRC32)
- `System.IO.Compression` (zstd via ZstdSharp)
- `System.Text.Json` (manifest serialization)
- `Polly` (retry policies)
- `Spectre.Console` (CLI UI)
- `System.CommandLine` (CLI parsing)

---

## Open Questions

1. **Index format extension**: Do we need to extend .caibx for byte offsets, or is cumulative size sufficient?

2. **Chunk storage model**: For very large games (100GB+), should we support chunk store as option?

3. **Parallel chunking**: Should client-side chunking be parallelized? (Currently single-threaded)

4. **Signature caching**: Should we cache chunk hashes of local files between sessions?

5. **Repair mode**: How to handle corrupted local files (re-download affected chunks)?
