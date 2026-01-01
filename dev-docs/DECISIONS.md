# PatchSync Architectural Decisions

This document records key architectural decisions and their rationale.

---

## Decision 1: Content-Defined Chunking (CDC) over Fixed-Size Chunking

**Status**: Accepted

**Context**: The original PatchSync prototype uses fixed-size chunking (chunk size = sqrt(fileSize)). This works well for appends but fails catastrophically for insertions and deletions - a single byte inserted at the beginning shifts all chunk boundaries, causing the entire file to be re-downloaded.

**Decision**: Use Content-Defined Chunking (CDC) with desync/casync-compatible buzhash algorithm.

**Rationale**:
- CDC chunk boundaries are determined by content, not position
- Insertions/deletions only affect 1-2 chunks, not all subsequent
- Proven at scale (casync, desync, restic, borg, Ceph, Google)
- 48-byte rolling window provides good boundary detection
- Compatible with existing casync/desync ecosystem

**Consequences**:
- Signature files must store explicit chunk boundaries (offset + length)
- Slightly more complex implementation than fixed-size
- Must ensure identical chunking on build server and client

---

## Decision 2: Hybrid Desync Integration (CLI for builds, .NET port for client)

**Status**: Accepted

**Context**: Desync (Go) provides excellent CDC implementation but has no native .NET bindings. Options evaluated:
1. Go C-shared library + P/Invoke - complex, requires fork
2. Subprocess CLI - simple but less control
3. Full .NET port - most effort but full control
4. Hybrid - CLI for builds, port client-side only

**Decision**: Use hybrid approach:
- Build server: Use desync CLI directly (no integration work)
- Game client: Port CDC chunker and .caibx parsing to .NET

**Rationale**:
- Build servers are developer-controlled (can run any tools)
- Game clients need lean, embeddable SDK (no external binaries)
- Only ~500 lines of code needed for client-side port
- Maintains .caibx format compatibility with desync ecosystem
- Avoids CGO complexity and ~5MB Go runtime overhead per DLL

**Consequences**:
- Must maintain .NET port of CDC algorithm
- Must ensure chunking produces identical results to desync
- Build process uses desync CLI (simple shell commands)
- No desync binary distribution needed for game launchers

---

## Decision 3: CDN-Only Architecture (No Server-Side Computation)

**Status**: Accepted

**Context**: Traditional delta patching (Steam, etc.) computes patches server-side for each version pair. This requires:
- Server infrastructure to run computations
- O(n) or O(n^2) patches for n versions
- Vulnerability to DDoS attacks

**Decision**: Use "reverse rsync" model with static CDN hosting only.

**Rationale**:
- Indie game developers cannot afford to host servers
- DDoS protection is essentially free for static CDN hosting
- Client-side delta computation naturally handles version jumps (a -> d)
- Minimal CDN storage: only latest version + signatures
- Works with any static file host (S3, R2, CloudFront, Backblaze, etc.)

**Consequences**:
- All delta computation happens on client
- Client must download signature files (~2% of file size)
- Client needs sufficient CPU for chunking (typically <1 second per GB)
- No dependency on game developer's infrastructure

---

## Decision 4: Byte-Range Requests over Chunk Store Model

**Status**: Accepted

**Context**: Desync uses a "chunk store" model where each chunk is a separate file (e.g., `2a/2a1b3c...cacnk`). For a 1GB file with 64KB chunks, this means ~16,000 HTTP requests.

**Decision**: Use HTTP byte-range requests on complete files instead of per-chunk requests.

**Rationale**:
- Fewer HTTP requests = lower latency
- CDNs optimize well for byte-range requests
- Game files are already present on CDN (for full downloads)
- Simpler CDN structure (no chunk store directory)
- Can coalesce adjacent ranges into single requests

**Consequences**:
- Different storage model than casync/desync
- Signature files store byte offsets, not chunk IDs
- Must implement range coalescing logic
- Some CDNs have limited multi-range support (fallback needed)

**Alternative**: For games with many small files, consider chunk store model.

---

## Decision 5: Manifest Layer with Per-File Strategies

**Status**: Accepted

**Context**: Different file types need different update strategies:
- Executables: Delta patching (ideal case)
- Compressed media: Full update (delta provides no benefit)
- Config files: Hash check only (small files)
- Some archives: Full update (encryption invalidates delta)

**Decision**: Build a manifest layer that specifies update strategy per-file.

**Rationale**:
- `DeltaUpdate` for most binary files
- `AlwaysFullUpdate` for compressed/encrypted content
- `UpdateIfFullHashMismatch` for small files (<1KB)
- `NeverUpdate` for user-modifiable files
- Game developers can override via input manifest

**Consequences**:
- Manifest must include file-type detection or explicit overrides
- Build process accepts optional input manifest with patterns
- Signature generation skipped for `AlwaysFullUpdate` files

---

## Decision 6: Green Field Implementation

**Status**: Accepted

**Context**: The existing PatchSync codebase uses fixed-size chunking and has architectural patterns that don't align with CDC requirements.

**Decision**: Treat current codebase as reference/guide only. Implement fresh based on new architecture.

**Rationale**:
- Fundamental algorithm change (fixed -> CDC) affects most components
- Cleaner to implement correctly from start than refactor
- Existing code provides useful patterns for HTTP handling, progress, etc.
- New architecture requires different data structures

**Consequences**:
- Start with clean project structure
- Port proven patterns from existing code (HTTP retry, progress, etc.)
- Maintain same three-tier architecture (CLI -> SDK -> Common)
- Preserve multi-framework targeting strategy

---

## Decision 7: Compressed Fallback with Automatic Selection

**Status**: Accepted

**Context**: Sometimes downloading a compressed full file is more efficient than delta patching (e.g., when >80% of file changed, or when file is already compressed).

**Decision**: Provide both raw files (for delta) and compressed files (for fallback) on CDN. Client automatically chooses.

**Rationale**:
- Compressed fallback essential for media files
- Client can estimate delta size from signature comparison
- Decision: `delta if deltaSize < compressedSize * threshold`
- Threshold configurable (default: 0.8 = delta must be 20%+ better)

**Consequences**:
- CDN stores both raw and .zst compressed versions
- Manifest includes compressed size for decision making
- Build process generates both versions
- Client implements selection logic

---

## Decision 8: .caibx Index Format Compatibility

**Status**: Under Consideration

**Context**: Desync uses `.caibx` index format (casync-compatible). Options:
1. Use .caibx for ecosystem compatibility
2. Use custom format optimized for byte-ranges

**Decision**: TBD - Implement .caibx parsing but may extend for byte-range optimization.

**Rationale**:
- .caibx is proven and well-documented
- Ecosystem compatibility allows using desync tools
- May need extension for byte offsets (vs chunk IDs)

**Open Questions**:
- Do we need chunk IDs or just offsets + hashes?
- Should we add compressed size hints to index?
- How to handle custom extensions while maintaining compatibility?

---

## Decision 9: Multi-CDN Provider Abstraction

**Status**: Accepted

**Context**: Different CDNs have different capabilities:
- AWS S3: Single-range only
- Cloudflare R2: Limited multi-range
- Bunny CDN: Full RFC 7233

**Decision**: Implement `IStorageProvider` abstraction with capability detection and fallback.

**Rationale**:
- Game developers use various CDN providers
- Graceful degradation better than hard failures
- Single-range fallback works everywhere
- HTTP/2 multiplexing compensates for more requests

**Consequences**:
- Each provider implements capability flags
- Download logic adapts to provider capabilities
- Connection pooling for parallel single-range requests
- May batch sequential ranges when multi-range unavailable

---

## Summary

| Decision | Choice | Key Benefit |
|----------|--------|-------------|
| Chunking | CDC (buzhash) | Handles insertions/deletions |
| Desync Integration | Hybrid | Best of both worlds |
| Architecture | CDN-only | No server infrastructure |
| Download Model | Byte-ranges | Fewer HTTP requests |
| Update Strategy | Per-file manifest | Right tool for each file |
| Implementation | Green field | Clean architecture |
| Fallback | Compressed + selection | Optimal bandwidth |
| Index Format | .caibx compatible | Ecosystem support |
| CDN Support | Provider abstraction | Multi-vendor support |
