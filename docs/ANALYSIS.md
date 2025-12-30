# PatchSync Deep Dive Analysis

## Executive Summary

PatchSync implements a **reverse rsync** delta patching system for efficient game/software updates. The current implementation is solid for basic binary delta patching but has significant gaps for the stated MVP file types.

---

## Current Implementation Analysis

### What's Working Well

1. **Core Delta Algorithm** (`PatchSync.SDK/FilePatcher/FilePatcher.cs`)
   - Adler32 rolling hash + XxHash3 strong hash (classic rsync two-stage matching)
   - O(n) file scanning with O(1) per-byte rolling hash updates
   - Circular buffer for memory efficiency
   - Chunk deduplication in signature index

2. **Manifest System** (`PatchSync.Common/Manifest/`)
   - Comprehensive `ManifestFileCommand` enum covering update strategies
   - Adaptive chunk sizing: sqrt(fileSize) for large files, 1KB for smaller
   - Dual hashing: FastHash (XxHash3) for quick checks, SHA256 for integrity

3. **HTTP Download Strategy** (`PatchSync.SDK/Client/`, `PatchSync.Common/RemoteFiles/`)
   - Multi-range byte requests (RFC 7233)
   - Multipart/byteranges response handling
   - Polly retry with exponential backoff
   - Smart threshold: falls back to full download if >66% needs updating

4. **Architecture**
   - Clean three-tier separation (CLI → SDK → Common)
   - Multi-framework targeting for broad compatibility
   - Async/streaming throughout

### Critical Gaps vs. MVP Goals

| File Type | Current Handling | Issue Severity |
|-----------|------------------|----------------|
| Text configs | Binary delta | Minor - works, not optimal |
| Binary exe/dll | Delta patching | Good - ideal use case |
| **Tar-style archives** | **Binary blob** | **CRITICAL** - see below |
| **Raw data (insert/delete)** | **Fixed-size chunks** | **HIGH** - poor boundary alignment |
| Media files | Binary delta | Medium - compressed = no benefit |

---

## Problem Analysis: File Type Scenarios

### 1. Text Configuration Files
**Status**: Acceptable
- Binary delta works but isn't space-optimal
- Small files (<1KB) use `UpdateIfFullHashMismatch` (no delta)
- **Recommendation**: Fine as-is for MVP

### 2. Binary Executables/DLLs
**Status**: Good
- This is exactly what rsync-style delta excels at
- Minor modifications produce minimal patches
- **Recommendation**: Current approach is appropriate

### 3. Tar-Style Archives with Headers (CRITICAL ISSUE)

This is the **most problematic** scenario. Consider a tar-style game archive:

```
[Header: 1KB] [Block A: 10MB] [Block B: 5MB] [Block C: 8MB]
     ^-- Contains offsets to blocks A, B, C
```

**Problems with current approach:**

1. **Header changes cascade**: If Block A grows by 100 bytes, the header's pointers to B and C change, AND all blocks shift by 100 bytes. With fixed-size chunks, EVERY chunk boundary after the insertion is misaligned.

2. **Encrypted/compressed blocks**: If blocks are encrypted or compressed, they appear as pseudo-random data. Delta patching provides zero benefit - two versions of the same logical data will have completely different byte sequences.

3. **Block rearrangement**: If the archive reorders blocks (A,B,C → B,A,C), the current algorithm CAN detect moved chunks but doesn't optimize for large-scale reordering.

**Current code behavior** (`FilePatcher.cs:127-139`):
```csharp
// Only sets if first match OR position-perfect match
if (existingSlice == null || start == entryIndexStart)
{
    slices[entryIndex] = new PatchSlice(PatchSliceLocation.ExistingSlice, start);
}
```
This prefers position-aligned chunks, missing opportunities when data moves.

### 4. Raw Data Files with Insertions/Deletions (HIGH ISSUE)

Example: A level file where enemies are inserted in the middle:

```
Before: [Enemy1][Enemy2][Enemy3][Boss]
After:  [Enemy1][NewEnemy][Enemy2][Enemy3][Boss]
```

With **fixed-size chunking**, inserting `NewEnemy` shifts everything after it. The rolling hash will eventually find `Enemy2`, `Enemy3`, `Boss` at different offsets, but:
- Every chunk boundary is now misaligned
- Many partial matches are missed
- Worst case: entire file downloaded despite minimal logical change

### 5. Media Files (mp3, ogg, wav)

**Compressed formats (mp3, ogg)**: Delta patching is nearly useless. These use lossy compression - even re-encoding the same audio produces different bytes.

**Uncompressed (wav)**: Delta works well for modifications.

**Recommendation**: Consider `AlwaysFullUpdate` for compressed media, or better: detect file type and auto-select strategy.

---

## Alternative Approaches to Consider

### 1. Content-Defined Chunking (CDC) - Deep Dive

Instead of fixed-size chunks, use content-aware boundaries determined by the data itself.

#### Algorithm Comparison

| Algorithm | Speed vs Rabin | Complexity | Used By |
|-----------|---------------|------------|---------|
| **Rabin Fingerprint** | 1x (baseline) | High (polynomial math) | restic, LBFS |
| **Buzhash** | 2-3x | Medium (cyclic rotation) | Borg, IPFS |
| **Gear Hash** | 5x | Low (shift + add) | Basic CDC |
| **FastCDC** | 10x | Low + normalized | Google, Ceph |

#### Recommendation: FastCDC

FastCDC is optimal for PatchSync because:
1. **10x faster than Rabin** - critical for large game files
2. **Normalized chunking** - uses two masks to create normal distribution (not geometric)
3. **Skip optimization** - doesn't check boundaries until minimum size reached
4. **Proven** - used by Google CDC File Transfer, Ceph

#### FastCDC Algorithm

```csharp
public static int FindBoundary(ReadOnlySpan<byte> data)
{
    const int MIN_SIZE = 4096;    // 4KB minimum
    const int AVG_SIZE = 16384;   // 16KB average
    const int MAX_SIZE = 65536;   // 64KB maximum

    int length = Math.Min(data.Length, MAX_SIZE);
    if (length <= MIN_SIZE) return length;

    ulong fingerprint = 0;

    // Skip minimum size (no boundary checks needed)
    for (int i = 0; i < MIN_SIZE; i++)
        fingerprint = (fingerprint << 1) + GEAR_TABLE[data[i]];

    // Search for boundary with normalized chunking
    for (int i = MIN_SIZE; i < length; i++)
    {
        fingerprint = (fingerprint << 1) + GEAR_TABLE[data[i]];

        // Two masks: harder below average, easier above average
        ulong mask = (i < AVG_SIZE) ? MASK_S : MASK_L;
        if ((fingerprint & mask) == 0)
            return i + 1;  // Found boundary
    }

    return length;  // Force boundary at max size
}
```

#### Gear Table
256 random 64-bit integers (one per byte value). The same table must be used for signature generation AND client-side chunking:

```csharp
// Fixed seed ensures reproducibility across builds
var rng = new Random(0x50415443);  // "PATC" in hex
ulong[] GEAR_TABLE = new ulong[256];
for (int i = 0; i < 256; i++)
    GEAR_TABLE[i] = (ulong)rng.NextInt64();
```

#### Normalized Chunking (Key Innovation)

Two masks create a more uniform chunk size distribution:

```
MASK_S (stricter): Check 15 bits - harder to find boundary below average
MASK_L (looser):   Check 11 bits - easier to find boundary above average

Result: Chunks cluster around average size instead of geometric distribution
```

#### Chunk Size Recommendations for Game Patching

| Scenario | Min | Avg | Max | Rationale |
|----------|-----|-----|-----|-----------|
| Frequent patches | 4KB | 16KB | 64KB | Fine granularity for small changes |
| Large asset files | 16KB | 64KB | 256KB | Less metadata overhead |
| Executables | 2KB | 8KB | 32KB | Catch small code changes |

#### Signature File Format Change

Current (fixed-size, boundaries implied):
```
[RollingHash:4][FullHash:8] × N chunks
```

New (variable-size, boundaries explicit):
```
[ChunkCount:4]
[Offset:8][Length:4][RollingHash:4][FullHash:8] × N chunks
```

This adds 12 bytes per chunk but enables variable-size chunking.

**Advantages**:
- Insertions/deletions only affect 1-2 chunks, not all subsequent
- Handles data shifting gracefully
- 10x faster than Rabin with same deduplication ratio

**Disadvantages**:
- Signature files ~12 bytes larger per chunk
- Variable chunks complicate range request coalescing
- Requires updating both generator and client

### 2. Archive-Aware Patching

For tar-style files, decompose → patch → recompose:

```
Archive v1 → Extract entries → Patch individual entries → Repackage
```

**Advantages**:
- Perfect for game asset archives
- Each logical file gets optimal treatment
- Handles encryption/compression at entry level

**Disadvantages**:
- Requires format-specific handlers
- More temporary disk space
- Complex orchestration

### 3. bsdiff/xdelta for Executables

For exe/dll files, bsdiff often outperforms rsync-style:
- Better compression of instruction sequences
- Handles compiler optimizations better

**Recommendation**: Consider as optional enhancement, not MVP requirement.

### 4. Signature Streaming

Current flow:
```
Download full .sig file → Compare locally → Download chunks
```

Alternative:
```
Stream signatures progressively → Compare as they arrive → Pipeline downloads
```

Reduces latency for large signature files.

---

## HTTP/Network Considerations

### Current State
- Multi-range requests work but aren't parallelized
- No explicit HTTP/2 configuration
- No HTTP/3 support
- No compression (gzip/brotli)

### CDN Compatibility Notes

| CDN | Multi-Range Support | Notes |
|-----|---------------------|-------|
| Cloudflare R2 | Limited | May return 200 instead of 206 for multi-range |
| AWS S3 | Single range only | No multipart/byteranges |
| Bunny CDN | Yes | Full RFC 7233 support |
| Fastly | Yes | Full support |

**Recommendation**: Implement fallback to single-range requests when multipart not supported.

---

## User Requirements Clarification (Answered)

| Question | Answer |
|----------|--------|
| Archive formats | Mix of both - custom per-game AND standard formats |
| Encryption | Varies: target game uses hash-based entries with mangled data; others use AES with version-specific keys |
| CDN targets | Multiple: Cloudflare R2, AWS S3/CloudFront, Backblaze, others - **need abstraction layer** |
| Priority | Updates primary, repairs secondary |

**Key Implications:**
- **Encryption with changing keys = delta useless** at archive level (different bytes each version)
- **Multi-CDN support requires abstraction** with fallback strategies
- **Archive handlers must be pluggable** (no single format assumption)

---

## Final Analysis: Will This Approach Work?

### Verdict: **Partially - Significant Gaps Need Addressing**

| Scenario | Current Status | Will It Work? | Fix Required |
|----------|---------------|---------------|--------------|
| Exe/DLL updates | Working well | Yes | None |
| Text config updates | Working | Yes | None |
| Raw data appends | Working | Yes | None |
| Raw data inserts/deletes | Broken | **No** | CDC required |
| Uncompressed archives | Partially works | Marginal | CDC helps |
| Encrypted archives | Broken | **No** | Archive-aware or AlwaysFullUpdate |
| Compressed media | Wasteful | Inefficient | Auto-detect → AlwaysFullUpdate |
| Multi-CDN support | Fragile | Unreliable | Abstraction + fallback |

### The Core Problem: Fixed-Size Chunking

The current algorithm fails when **data shifts** because chunk boundaries are position-based:

```
Original:  [Chunk1: 0-1023][Chunk2: 1024-2047][Chunk3: 2048-3071]
                          ↑ boundary at 1024

After 100-byte insert at position 500:
           [?????: 0-1023][?????: 1024-2047][?????: 2048-3071]
                          ↑ boundary still at 1024, but content shifted!
```

**Result**: Every chunk after the insertion has different content → all marked as RemoteSlice → full download.

---

## Recommended Architecture Changes

### Tier 1: MVP Critical (Must Have)

#### 1. Content-Defined Chunking (CDC)

Replace fixed-size chunking with content-determined boundaries using Gear/FastCDC algorithm:

```csharp
public interface IChunker
{
    IEnumerable<Chunk> Chunk(Stream input);
}

public class FixedSizeChunker : IChunker { }     // Current approach
public class ContentDefinedChunker : IChunker { } // New CDC approach
```

**Signature file format change**: Must store chunk boundaries, not assume fixed positions:

```
Current:  [RollingHash:4][FullHash:8] × N chunks (boundaries implied)
New:      [Offset:8][Length:4][RollingHash:4][FullHash:8] × N chunks
```

**Files to modify:**
- `PatchSync.SDK/Signatures/SignatureFileHandler.Generator.cs`
- `PatchSync.SDK/Signatures/SignatureFileHandler.Deserializer.cs`
- `PatchSync.SDK/FilePatcher/FilePatcher.cs`
- `PatchSync.Common/Signatures/SignatureFile.cs`
- `PatchSync.Common/Signatures/SignatureChunk.cs`

#### 2. CDN Abstraction Layer

```csharp
public interface IStorageProvider
{
    Task<Stream> DownloadAsync(string path, CancellationToken ct);
    Task<Stream> DownloadRangeAsync(string path, long start, long end, CancellationToken ct);
    Task<IEnumerable<Stream>> DownloadMultiRangeAsync(string path, IEnumerable<(long, long)> ranges, CancellationToken ct);
    bool SupportsMultiRange { get; }
}

public class S3StorageProvider : IStorageProvider { }      // Single-range only
public class R2StorageProvider : IStorageProvider { }      // Limited multi-range
public class GenericHttpProvider : IStorageProvider { }    // Full RFC 7233
```

**Files to modify:**
- New: `PatchSync.SDK/Storage/IStorageProvider.cs`
- New: `PatchSync.SDK/Storage/S3StorageProvider.cs`
- New: `PatchSync.SDK/Storage/HttpStorageProvider.cs`
- Modify: `PatchSync.SDK/Client/PatchSyncClient.cs`

#### 3. Smart Fallback for Range Requests

When multi-range fails or isn't supported:
1. Batch sequential single-range requests
2. Use parallel downloads with connection pooling
3. Consider download coalescing (merge adjacent ranges)

### Tier 2: High Priority (Should Have)

#### 4. File-Type Detection for Strategy Selection

```csharp
public static ManifestFileCommand DetectOptimalCommand(string filePath, long fileSize)
{
    var ext = Path.GetExtension(filePath).ToLower();

    // Compressed media - delta useless
    if (ext is ".mp3" or ".ogg" or ".mp4" or ".webm")
        return ManifestFileCommand.AlwaysFullUpdate;

    // Small files - hash check sufficient
    if (fileSize < 1024)
        return ManifestFileCommand.UpdateIfFullHashMismatch;

    // Default to delta
    return ManifestFileCommand.DeltaUpdate;
}
```

**Files to modify:**
- `PatchSync.CLI/Commands/Build Signatures/BuildSignatures.cs`
- `PatchSync.Common/Manifest/ManifestFileEntry.cs`

#### 5. Input Manifest Support

Allow game developers to specify commands per-file:

```json
{
  "defaults": {
    "command": "DeltaUpdate"
  },
  "overrides": [
    { "pattern": "*.mp4", "command": "AlwaysFullUpdate" },
    { "pattern": "data/*.pak", "command": "AlwaysFullUpdate" },
    { "pattern": "config/*", "command": "UpdateIfFullHashMismatch" }
  ]
}
```

**Files to modify:**
- New: `PatchSync.Common/Manifest/InputManifest.cs`
- Modify: `PatchSync.CLI/Commands/Build Signatures/BuildSignatures.cs`

### Tier 3: Future Enhancement (Nice to Have)

#### 6. Archive Handler Abstraction

```csharp
public interface IArchiveHandler
{
    bool CanHandle(string filePath);
    IEnumerable<ArchiveEntry> ListEntries(Stream archive);
    Stream ExtractEntry(Stream archive, ArchiveEntry entry);
    void RepackArchive(Stream output, IEnumerable<(ArchiveEntry, Stream)> entries);
}
```

Given encryption complexity (keys change per version), recommend:
- For encrypted archives: Use `AlwaysFullUpdate`
- For unencrypted archives: Standard delta or archive-aware patching
- Defer custom format handlers to post-MVP

#### 7. Parallel HTTP/2 Downloads

Enable multiplexed range downloads for CDNs that support it.

#### 8. Delta Compression

Compress patch data (the remote chunks being downloaded) with zstd/brotli.

---

## Implementation Roadmap

### Phase 1: Foundation (MVP Critical)
1. Implement `IStorageProvider` abstraction
2. Add single-range fallback to `PatchSyncClient`
3. Add file-type detection in `BuildSignatures`

### Phase 2: Core Algorithm Upgrade
4. Implement `ContentDefinedChunker` using FastCDC
5. Update signature file format for variable chunks
6. Modify `FilePatcher` to handle variable-size chunks

### Phase 3: Developer Experience
7. Add input manifest support
8. Add CLI options for strategy override
9. Improve progress reporting

### Phase 4: Optimization (Post-MVP)
10. HTTP/2 parallel downloads
11. Archive handler framework
12. Delta compression

---

## Key Files Reference

| Component | Path | Change Needed |
|-----------|------|---------------|
| Delta algorithm | `PatchSync.SDK/FilePatcher/FilePatcher.cs` | CDC integration |
| Signature gen | `PatchSync.SDK/Signatures/SignatureFileHandler.Generator.cs` | Variable chunks |
| Signature deser | `PatchSync.SDK/Signatures/SignatureFileHandler.Deserializer.cs` | New format |
| Chunk record | `PatchSync.Common/Signatures/SignatureChunk.cs` | Add offset/length |
| Manifest entry | `PatchSync.Common/Manifest/ManifestFileEntry.cs` | Type detection |
| Build command | `PatchSync.CLI/Commands/Build Signatures/BuildSignatures.cs` | Input manifest |
| Client | `PatchSync.SDK/Client/PatchSyncClient.cs` | Storage abstraction |
| HTTP handler | `PatchSync.Common/RemoteFiles/HttpHandler/HttpHandler.cs` | Fallback logic |

---

---

## Desync Deep Dive: Foundation Analysis

### Why Desync is Attractive

| Feature | Desync | PatchSync Current |
|---------|--------|-------------------|
| Chunking | **CDC (rolling hash)** | Fixed-size |
| Chunk size | min/avg/max configurable (16/64/256 KB default) | sqrt(fileSize) |
| Library architecture | Clean, reusable packages | Monolithic |
| Store backends | HTTP, S3, SFTP, local, GCS | HTTP only |
| Seed/delta support | Built-in, sophisticated | Basic |
| Progress reporting | Per-chunk callbacks | Per-file |
| HTTP/2 | Enabled (`ForceAttemptHTTP2: true`) | Not configured |
| License | BSD-3 | Apache 2.0 |
| Cross-platform | Windows, Mac, Linux | Same |

### Desync's CDC Algorithm (from `chunker.go`)

```go
// Rolling hash with 48-byte window
const ChunkerWindowSize = 48

// Boundary detection formula
discriminator := uint32(float64(avg) / (-1.42888852e-7*float64(avg) + 1.33237515))
boundary := hash % discriminator == discriminator - 1
```

- Uses pre-computed hash table (256 entries)
- `bits.RotateLeft32` for rolling computation
- Compatible with casync format

### Gaps/Considerations for Game Patching

#### 1. Chunk Store Model vs File Model

**Desync approach:**
```
/chunk-store/
  2a/2a1b3c4d...cacnk  # Individual chunk files
  3b/3b2c4d5e...cacnk
  ...
game.caibx              # Index file (references chunks)
```

**PatchSync/game patcher approach:**
```
/game/
  game.exe              # Actual file (for byte-range requests)
  game.exe.sig          # Signature file
```

**Impact**: Desync downloads each chunk as a separate HTTP GET request. For a 1GB file with 64KB chunks = ~16,000 HTTP requests. PatchSync uses byte-range requests on a single file.

**Mitigation options:**
- Bundle chunks into larger "pack files" (like git)
- Use HTTP/2 multiplexing (desync already enables this)
- Accept the overhead (CDNs handle many small requests well)

#### 2. No Manifest Concept

Desync has no built-in:
- File list with versions
- Per-file update strategies
- Compressed size tracking
- Hash verification options

**Need to build** a manifest layer on top.

#### 3. No Compressed Fallback Decision

Desync always uses delta. PatchSync needs:
```
if (deltaSize > compressedSize * 0.8)
    downloadCompressedFile();
else
    downloadDelta();
```

**Need to build:** Size estimation before download, decision logic.

#### 4. Single-Chunk HTTP Requests

Desync's HTTP store (`remotehttp.go:215-226`):
```go
func (r *RemoteHTTP) GetChunk(id ChunkID) (*Chunk, error) {
    p := r.nameFromID(id)  // e.g., "2a1b/2a1b3c4d...cacnk"
    b, err := r.GetObject(p)  // Single GET request
    ...
}
```

**No byte-range coalescing**. Each chunk = one HTTP request.

### What Desync Does Well (Keep/Use)

1. **CDC Algorithm** - The chunker is excellent, directly usable
2. **Seed Mechanism** - `FileSeed`, `SelfSeed`, `NullSeed` are sophisticated
3. **Index Format** - `.caibx` is compact and proven
4. **Parallel Assembly** - `AssembleFile` uses goroutines effectively
5. **In-place Resume** - The `-k` flag allows resuming interrupted downloads
6. **Reflink Support** - Uses CoW on Btrfs/XFS when available

---

## Desync Integration Analysis

### Go C-Shared Library Compatibility

Go supports building C-shared libraries using `go build -buildmode=c-shared`, which produces:
- A `.dll` (Windows), `.so` (Linux), or `.dylib` (macOS)
- A C header file with exported function signatures

**C-Shared Requirements:**
1. Functions must be exported with `//export FunctionName` comment
2. Parameters/returns must use C-compatible types (or `unsafe.Pointer`)
3. No Go-specific types (slices, strings, channels) across boundary
4. Memory management must be explicit

**Desync Current State:**
- Written as a pure Go CLI tool
- No `//export` declarations
- Uses Go slices, channels, and interfaces extensively
- Would require significant wrapper layer for C ABI

### Integration Strategy Options

#### Option A: Go C-Shared Library + P/Invoke (Complex)

```
[.NET Game Launcher]
      │
      ▼ P/Invoke
[desync.dll (Go c-shared)]
      │
      ▼
[desync library code]
```

**Effort Required:**
1. Fork desync repository
2. Create C ABI wrapper layer (new Go package)
3. Handle memory management (strings → C strings, callbacks)
4. Build CI/CD for multi-platform DLLs

**Pros:**
- Best performance
- Single binary distribution per platform
- Full access to desync internals

**Cons:**
- High initial effort (2-3 weeks)
- CGO complexity and debugging difficulty
- Must maintain fork
- Go runtime bundled in DLL (~5MB overhead)

#### Option B: Subprocess CLI (Simplest)

```
[.NET Game Launcher]
      │
      ▼ Process.Start("desync", args)
[desync.exe CLI]
```

**Effort Required:**
1. Bundle desync binaries per platform
2. Write process wrapper in C#
3. Parse stdout/stderr for progress

**Pros:**
- Simplest integration (1-2 days)
- No CGO complexity
- Easy to update desync independently
- Already battle-tested CLI

**Cons:**
- Process management overhead
- Less control over progress reporting
- Must bundle ~10MB binary per platform
- Harder to customize behavior

#### Option C: Port to .NET (Most Control)

Port key algorithms from desync to C#:
- `Chunker` → C# implementation (~200 lines)
- `Index` format parsing (~150 lines)
- `AssembleFile` logic (~300 lines)

**Effort Required:**
1. Port CDC chunker algorithm
2. Port .caibx index format
3. Build assembly logic with seeds
4. Add HTTP byte-range store (PatchSync already has this)

**Pros:**
- Full control over implementation
- Native .NET performance
- No external dependencies
- Customize for game-specific needs (byte-ranges vs chunks)

**Cons:**
- Most development effort (2-4 weeks)
- Must maintain parity for .caibx compatibility
- Risk of subtle algorithm differences

#### Option D: Hybrid Approach (Recommended)

Use different components for different roles:

| Component | Build Server | Game Client |
|-----------|--------------|-------------|
| Chunking | desync CLI (Go) | .NET CDC port |
| Index creation | desync CLI (Go) | N/A |
| Chunk upload | desync + CDN tools | N/A |
| Manifest creation | Custom .NET CLI | N/A |
| Delta calculation | N/A | .NET CDC port |
| Download | N/A | .NET HTTP client |
| Assembly | N/A | .NET file writer |

**Rationale:**
- Build server can use full desync CLI (no P/Invoke needed)
- Game launcher needs only client-side components
- Port only what's needed for client (~500 lines of C#)
- Maintain .caibx format compatibility

---

## Critical Requirements (From User Clarifications)

### 1. CDN-Only Constraint

**Rationale**: Indie developers hosting servers get DDoS'd into shutdown.

**Implications**:
- No restic REST server, borg SSH, or server-side delta computation
- Static file hosting only (S3, R2, CloudFront, etc.)
- zsync/casync "reverse rsync" model is correct

### 2. Version Jumping (a → d Problem)

**Scenario**: User on version A (possibly modified/corrupted) needs version D.

**Why reverse rsync naturally handles this**:
```
Traditional delta:  Server precomputes A→B, B→C, C→D patches
                    User on A needs all 3 patches
                    Modified files? Patches may fail!

Reverse rsync:      Server hosts: latest signatures + latest files
                    Client has: whatever local state exists
                    Client computes: local → target delta at runtime
                    Downloads: only chunks client doesn't have
```

### 3. Minimal CDN Storage

**Goal**: Don't host every version ever made.

**Storage per game**:
```
/game/
  manifest.json           # Current version manifest
  files/
    game.exe              # Uncompressed (for byte-range delta)
    assets.pak
  signatures/
    game.exe.sig          # CDC signatures
    assets.pak.sig
  compressed/             # Optional fallback
    game.exe.zst
    assets.pak.zst
```

---

## Conclusion

The current PatchSync implementation provides a solid foundation with correct rsync-style delta concepts. However, **fixed-size chunking is fundamentally incompatible** with the stated file type requirements (insertions, deletions, archive modifications).

**Key Finding**: Desync provides the ideal CDC algorithm and format, but requires integration work. The **hybrid approach** (desync for builds, .NET port for client) offers the best balance of effort vs. capability.

**Recommended path forward:**
1. **Immediate**: Port desync's CDC chunker to .NET (~200 lines)
2. **Short-term**: Implement .caibx format parsing and byte-range HTTP store
3. **Medium-term**: Build manifest layer and CLI tools
4. **Long-term**: Optional archive-aware patching for complex game formats

The algorithm is sound; it's the chunking strategy and integration approach that needs finalization.
