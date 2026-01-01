# Similar Tools Research: Delta Sync & Game Patching Solutions

This document analyzes existing tools and approaches for efficient file synchronization and game patching, with a focus on CDN-compatible, client-side delta calculation solutions.

---

## Executive Summary

### Tools Evaluated

| Tool | Type | CDN Compatible | .NET Integration | Best For |
|------|------|----------------|------------------|----------|
| **zsync** | Reverse rsync | ✅ Excellent | ⚠️ Subprocess | ISO/large file distribution |
| **casync/desync** | Content-addressed sync | ✅ Excellent | ⚠️ Subprocess | Large file sets, deduplication |
| **restic** | Backup | ❌ Requires server | ❌ No library API | Backups only |
| **borg** | Backup | ❌ Requires SSH | ❌ No library API | Backups only |
| **rclone** | Cloud sync | ✅ Yes | ⚠️ Subprocess | File mirroring (no delta) |
| **FastRsyncNet** | rsync library | ✅ Yes | ✅ Native NuGet | .NET rsync implementation |
| **bsdiff** | Binary diff | ✅ Yes | ✅ NuGet available | Executables |

### Key Finding

**zsync and casync/desync are the closest matches to PatchSync's goals**, but neither has native .NET support. The best path forward is either:
1. Implement CDC algorithm natively in .NET (recommended)
2. Use FastRsyncNet with CDC enhancement
3. Shell to desync CLI as subprocess

---

## 1. zsync - Closest to PatchSync's Model

### How It Works

zsync implements "reverse rsync" - exactly what PatchSync aims to do:

1. **Server generates once**: `.zsync` control file containing block checksums
2. **Client downloads**: Small control file (~2% of file size)
3. **Client computes**: Delta against local file
4. **Client requests**: Only missing blocks via HTTP Range requests
5. **No server computation**: Static file hosting only

### Technical Details

| Aspect | Value |
|--------|-------|
| Rolling hash | Adler-32 variant (same family as PatchSync) |
| Strong hash | MD4 (128-bit, truncatable) |
| Block size | Auto-selected, typically 1024-4096 bytes |
| Overhead | ~2% of file size for control file |
| Protocol | HTTP/1.1 Range requests |

### .zsync File Format

```
zsync: 0.6.2
Filename: game.exe
Length: 104857600
Hash-Lengths: 2,2,5
Blocksize: 2048
URL: game.exe
SHA-1: a1b2c3d4e5...

[binary checksum data: 20 bytes per block]
```

### Why zsync Is Relevant

- **Same model as PatchSync**: Client-side delta, CDN hosting
- **Proven at scale**: Ubuntu ISOs distributed via zsync since 2009
- **Simple protocol**: Just HTTP Range requests
- **Known limitation**: Fixed block sizes (not CDC)

### Integration Options

1. **zsync2** (C++): Adds HTTPS, used by AppImage
2. **zsync4j** (Java): Salesforce implementation
3. **hsynz** (C): Modern rewrite with zstd, directories

---

## 2. casync/desync - Content-Addressed Approach

### Architecture

casync (by Lennart Poettering) combines rsync + git concepts:

```
Server hosts:
  myapp.caibx     # Index file (list of chunk hashes)
  myapp.castr/    # Chunk store (individual chunk files)
    2a/2a1b3c...  # Each chunk named by SHA512/256 hash
    3b/3b2c4d...
```

### Key Innovation: Content-Defined Chunking

casync uses **buzhash** (rolling hash) for CDC:
- Variable chunk sizes: 16KB min, 64KB avg, 256KB max
- Chunk boundaries determined by content, not position
- Handles insertions/deletions gracefully

### desync - The Practical Choice

desync is a Go rewrite of casync with better features:

| Feature | casync | desync |
|---------|--------|--------|
| Language | C | Go |
| Maintenance | Slow | Active |
| Cloud backends | HTTP only | S3, GCS, Azure, HTTP |
| Parallelism | Limited | Full parallel downloads |
| License | LGPL 2.1 | BSD-3 |

### CDN Compatibility

Excellent - each chunk is an immutable file named by hash:
- Perfect for CDN caching (content never changes)
- No special server software needed
- Deduplication across versions automatic

### Integration Path

```bash
# Generate index and chunks
desync make game.caibx game.castr game-folder/

# Client update (seeds from existing installation)
desync extract --seed game-v1/ http://cdn/game.caibx game-v2/
```

---

## 3. restic & borg - Backup Tools (Not Suitable)

### Why They Don't Fit

| Issue | restic | borg |
|-------|--------|------|
| Server requirement | REST server or cloud API | SSH with borg installed |
| Model | Push to repository | Push to repository |
| Library API | ❌ Internal packages only | ❌ No stable API |
| Chunk size | 1 MiB (too large) | 2 MiB (too large) |

### Key Learning: Chunk Size Matters

Both use ~1-2 MiB chunks - optimized for backup deduplication, not delta patching.

For game patching, **32-64 KB chunks** are ideal:
- Small change = 1 chunk download (~32KB)
- restic: Small change = 1 chunk download (~1MB) - 30x worse!

---

## 4. rclone - No Delta Support

rclone does NOT support block-level delta transfers:
- Transfers entire files when changed
- Has chunking for upload size limits, not delta
- Useful for initial sync, not patching

---

## 5. .NET Libraries for Delta Patching

### FastRsyncNet (Recommended)

```csharp
// Signature generation
var signatureBuilder = new SignatureBuilder();
using var signatureStream = signatureBuilder.Build(sourceStream, new SignatureBuilder.Options());

// Delta generation
var deltaBuilder = new DeltaBuilder();
using var deltaStream = deltaBuilder.Build(sourceStream, signatureStream);

// Delta application
var deltaApplier = new DeltaApplier();
deltaApplier.Apply(sourceStream, deltaStream, targetStream);
```

| Feature | Value |
|---------|-------|
| NuGet | `FastRsyncNet` |
| License | MIT |
| Rolling hash | Adler-32 |
| Strong hash | xxHash64 (fast!) |
| Pure .NET | Yes |

### BsDiff (For Executables)

```csharp
using BsDiff;

// Create patch
BsDiff.Create(oldFile, newFile, patchFile);

// Apply patch
BsPatch.Apply(oldFile, patchFile, newFile);
```

| Feature | Value |
|---------|-------|
| NuGet | `BsDiff` |
| Best for | Executables (50-80% smaller than xdelta) |
| Memory | High (17n bytes) |
| Max size | 2 GB |

### xdelta3.net

```csharp
using Xdelta3.net;

// Encode (create delta)
Xdelta3.Encode(sourceFile, targetFile, deltaFile);

// Decode (apply delta)
Xdelta3.Decode(sourceFile, deltaFile, outputFile);
```

| Feature | Value |
|---------|-------|
| NuGet | `xdelta3.net` |
| Format | VCDIFF (RFC 3284) |
| Speed | Fastest generation |
| Native | Requires platform binaries |

---

## 6. Game Industry Approaches

### Steam (SteamPipe)

- ~1 MB chunks
- Compressed and encrypted
- Delta computed server-side per version pair
- Requires Steam infrastructure

### Riot Games (League of Legends)

**Most relevant to PatchSync goals:**

- **FastCDC** for chunking (~64KB average)
- **Zstandard** compression (level 19)
- **SQLite** local chunk inventory
- **HTTP Range requests** to CDN
- **Result**: 14x faster patching, 95% fewer failures

Key insight from Riot:
> "We moved from binary delta (bsdiff) to content-defined chunking. Now we don't need to compute patches between versions - the client figures out what it needs."

### GOG Galaxy

- Delta patching available
- Requires 2x disk space during patch
- Performance depends on developer implementation

---

## 7. Comparison Matrix for CDN-Only Model

| Tool | CDC | CDN-Only | Version Jump | .NET Native | License |
|------|-----|----------|--------------|-------------|---------|
| **zsync** | ❌ Fixed | ✅ Yes | ✅ Natural | ❌ No | Artistic 2.0 |
| **casync** | ✅ Buzhash | ✅ Yes | ✅ Natural | ❌ No | LGPL 2.1 |
| **desync** | ✅ Buzhash | ✅ Yes | ✅ Natural | ❌ No | BSD-3 |
| **FastRsyncNet** | ❌ Fixed | ✅ Yes | ✅ Natural | ✅ Yes | MIT |
| **PatchSync** | ❌ Fixed | ✅ Yes | ✅ Natural | ✅ Yes | Apache 2.0 |

---

## 8. Recommendations for PatchSync

### Option A: Enhance PatchSync with CDC (Recommended)

Implement FastCDC algorithm natively:

```csharp
public interface IChunker
{
    IEnumerable<Chunk> GetChunks(Stream input);
}

public class FastCDCChunker : IChunker
{
    // Gear-based rolling hash
    // Normalized chunking (two masks)
    // Min/Avg/Max size bounds
}
```

**Pros**:
- Full control over algorithm
- Native .NET performance
- No external dependencies
- Maintains CDN-only model

### Option B: Integrate desync via Subprocess

```csharp
public class DesyncPatcher
{
    public async Task UpdateAsync(string cdnUrl, string localPath)
    {
        var process = Process.Start("desync",
            $"extract --seed {localPath} {cdnUrl}/game.caibx {localPath}.new");
        await process.WaitForExitAsync();
        // Atomic swap
    }
}
```

**Pros**:
- Proven implementation
- Full CDC support
- S3/cloud backend support

**Cons**:
- External dependency (Go binary)
- Less control over progress reporting
- Cross-platform binary distribution

### Option C: Use FastRsyncNet as Base

Keep current rsync-style but optimize:

1. Reduce chunk size (1KB → configurable)
2. Add compressed fallback path
3. Improve signature format

**Pros**:
- Minimal code changes
- Production-tested library

**Cons**:
- Fixed chunks still have insertion problem
- Less efficient than CDC

---

## 9. CDN Storage Comparison

### Traditional Versioned Patches (Steam-style)

```
/patches/
  v1.0/
    game.exe
  v1.1/
    v1.0-to-v1.1.patch
  v1.2/
    v1.0-to-v1.2.patch  # Need N patches for N versions!
    v1.1-to-v1.2.patch
```

Storage: O(n²) patches

### PatchSync/zsync Model

```
/latest/
  manifest.json
  game.exe           # Current version only
  game.exe.sig       # Signatures
  game.exe.zst       # Compressed fallback
```

Storage: O(1) - only latest version!

### casync/desync Model

```
/game.caibx          # Current version index
/game.castr/         # All chunks ever (deduplicated)
  2a/2a1b3c...
  3b/3b4c5d...
```

Storage: O(unique chunks) - grows slowly with versions

---

## 10. Conclusion

### For PatchSync's Goals:

1. **CDN-only**: Both zsync and casync models work perfectly
2. **Version jumping**: Reverse-rsync model handles this naturally
3. **Minimal storage**: Only need latest version + signatures
4. **Efficiency**: CDC (FastCDC) is the key missing piece

### Recommended Architecture:

```
[Game Build]
    ↓
[FastCDC Chunker] → Generate signatures + chunks
    ↓
[CDN Upload]
    - manifest.json
    - Uncompressed files (for Range requests)
    - Signature files (.sig)
    - Compressed fallbacks (.zst)
    ↓
[Game Launcher (.NET)]
    ↓
[FastCDC Chunker] → Chunk local files
    ↓
[Delta Calculator] → Compare local vs remote signatures
    ↓
[Smart Downloader]
    - If delta < compressed: HTTP Range requests
    - Else: Download compressed file
    ↓
[File Assembler] → Local chunks + downloaded chunks
    ↓
[Updated Game]
```

---

## Sources

- [Riot Games Patcher](https://technology.riotgames.com/news/supercharging-data-delivery-new-league-patcher)
- [zsync Official](https://zsync.moria.org.uk/)
- [casync Blog Post](https://0pointer.net/blog/casync-a-tool-for-distributing-file-system-images.html)
- [desync GitHub](https://github.com/folbricht/desync)
- [FastRsyncNet GitHub](https://github.com/GrzegorzBlok/FastRsyncNet)
- [restic Design](https://restic.readthedocs.io/en/stable/design.html)
- [FastCDC Paper](https://www.usenix.org/conference/atc16/technical-sessions/presentation/xia)
- [Steamworks Documentation](https://partner.steamgames.com/doc/sdk/uploading)
