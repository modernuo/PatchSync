# Game Bundle Format Analysis for Delta Patching

## Format Categories

### Category 1: Sequential Archives (CDC-Friendly)
These formats store entries sequentially without central offset tables.

| Format | Extension | Structure | CDC Behavior |
|--------|-----------|-----------|--------------|
| TAR | `.tar` | `[Header1][Data1][Header2][Data2]...` | Good - unchanged entries match |
| CPIO | `.cpio` | Sequential headers + data | Good |
| AR | `.a` | Unix archive, sequential | Good |

**Why they work**: Each entry is self-contained. If entry N changes size, only entries after N shift. Entries before N remain identical and will match via CDC.

### Category 2: Offset-Table Archives (CDC-Hostile)
These formats have headers/directories containing offsets to entries.

| Format | Extension | Structure | CDC Behavior |
|--------|-----------|-----------|--------------|
| UOP/MYP | `.uop` | Header with block list + scattered entries | Poor - offset table changes cascade |
| ZIP | `.zip` | Local headers + Central Directory at end | Mixed - CD changes, but local headers stable |
| PAK (Quake) | `.pak` | Header + offset table + data | Poor |
| BSA (Bethesda) | `.bsa` | Header + folder/file tables + data | Poor |
| BA2 (Fallout 4+) | `.ba2` | Similar to BSA | Poor |
| VPK (Valve) | `.vpk` | Directory file + data files | Better if split |
| RPF (Rockstar) | `.rpf` | Encrypted + offset tables | Very poor |
| UPK/UASSET | `.upk`, `.uasset` | Export/import tables + data | Poor |

### Category 3: Compressed Archives
These are inherently CDC-hostile due to compression.

| Format | Extension | Notes |
|--------|-----------|-------|
| ZIP (compressed) | `.zip` | Each entry independently compressed |
| GZIP | `.gz` | Stream compression |
| ZSTD | `.zst` | Block compression (better) |
| LZ4 | `.lz4` | Block compression |

---

## Detailed Format Analysis

### TAR vs UOP Comparison

```
TAR Structure:
┌──────────────────────────────────────────────────────────┐
│ [512B Header][File1 Data][512B Header][File2 Data]...    │
└──────────────────────────────────────────────────────────┘
- Headers are at fixed offsets relative to their data
- No central index - purely sequential
- Modifying File1 shifts everything after it
- BUT: File2's content bytes remain identical (just at new offset)
- CDC should still match File2's chunks!

UOP Structure:
┌──────────────────────────────────────────────────────────┐
│ [Header: version, block_ptr]                             │
│ [Block1: count, next_ptr, entries[offset, size, hash]...]│
│ [Block2: ...]                                            │
│ [Entry Data scattered throughout file]                   │
└──────────────────────────────────────────────────────────┘
- Block entries contain ABSOLUTE offsets
- When any entry moves, its offset in the block changes
- Block contains many offsets, so it changes significantly
- CDC boundaries in blocks shift, causing cascade
```

### Why TAR Should Work Better

1. **No offset table**: TAR has no central directory with offsets
2. **Self-contained headers**: Each header only describes the following file
3. **Sequential layout**: Files are laid out in order
4. **Unchanged content**: If File3 doesn't change, its bytes are identical

**Expected CDC behavior for TAR**:
- File1 changes → File1 chunks differ
- File2 unchanged → File2 chunks MATCH (even at different absolute offset)
- File3 unchanged → File3 chunks MATCH

**Why this works**: CDC finds boundaries based on CONTENT, not position. The actual bytes of File2/File3 are identical, so the same chunks will be found.

---

## Implementation Strategies

### Strategy 1: Format-Aware Virtual Chunking
Parse the archive, extract entries as "virtual files", chunk each entry separately.

```
Archive.pak contains:
  - textures/sky.dds (2MB)
  - models/player.mdl (500KB)
  - sounds/music.ogg (5MB)

Virtual chunking:
  - Chunk textures/sky.dds → chunks A1, A2, A3
  - Chunk models/player.mdl → chunks B1
  - Chunk sounds/music.ogg → already compressed, skip

Signature stores:
  - Entry path + entry chunks (not file offset)
```

**Pros**: Maximum reuse, format-independent after parsing
**Cons**: Requires format parser, complex reconstruction

### Strategy 2: Entry-Level Hashing
Don't chunk entries, just hash each entry. If entry hash matches, copy from local.

```
Manifest:
  Archive.pak:
    - textures/sky.dds: hash=abc123, offset=0, size=2MB
    - models/player.mdl: hash=def456, offset=2MB, size=500KB

Delta:
  - sky.dds hash matches → copy from local archive at old offset
  - player.mdl hash differs → download new entry
```

**Pros**: Simple, works for any parseable format
**Cons**: No intra-entry delta (whole entry or nothing)

### Strategy 3: Split Archives
Some games use split archives (VPK dir + VPK data files).

```
game_dir.vpk (small, contains index)
game_000.vpk (large, contains data)
game_001.vpk (large, contains data)
```

**Strategy**:
- Index file: HashCheck (small, changes often)
- Data files: Delta (large, mostly stable)

### Strategy 4: Compressed Fallback
For hostile formats, pre-compress and serve compressed version.

```
Archive.uop (227MB) → Archive.uop.zst (85MB)
Delta overhead: 225MB download
Compressed: 85MB download
Winner: Compressed
```

---

## Recommended Implementation Plan

### Phase 1: Detection & Fallback (Current)
- [x] Detect known container formats by extension
- [x] Default to HashCheck/AlwaysCompressed for these
- [ ] Add compressed file generation to build pipeline

### Phase 2: TAR/Sequential Format Support
- [ ] Verify TAR works well with current CDC (test)
- [ ] If not, implement TAR-aware boundary hints
- [ ] Support .tar, .cpio, .ar

### Phase 3: Format Parsers (Priority Order)
1. **ZIP** - Most common, well-documented
   - Parse central directory
   - Chunk each entry separately
   - Reconstruct by assembling entries

2. **UOP** - Specific to UO
   - Parse block list
   - Chunk entry data (skip headers)
   - Reconstruct preserving structure

3. **BSA/BA2** - Bethesda games
   - Popular modding community
   - Parse file table
   - Entry-level or chunk-level delta

4. **VPK** - Valve games
   - Split format helps
   - Focus on data files

5. **PAK variants** - Per-engine
   - Quake PAK
   - Unreal PAK
   - Custom game PAKs

### Phase 4: Generic Framework
- Plugin system for format handlers
- Common interface: `IArchiveHandler`
  - `Parse(stream) → entries[]`
  - `ExtractEntry(entry) → stream`
  - `Reconstruct(entries[], sources[]) → stream`

---

## Testing Strategy

Create test archives with known content:

```csharp
// Test: TAR with unchanged entries
var tar1 = CreateTar(file1: "AAA", file2: "BBB", file3: "CCC");
var tar2 = CreateTar(file1: "AAA-modified", file2: "BBB", file3: "CCC");
// Expected: file2 and file3 chunks should match

// Test: UOP with unchanged entries
var uop1 = CreateUOP(entry1: data1, entry2: data2);
var uop2 = CreateUOP(entry1: data1_modified, entry2: data2);
// Expected: Poor matching due to offset shifts
```

---

## File Format References

| Format | Documentation |
|--------|---------------|
| TAR | POSIX.1-2001, GNU tar |
| ZIP | PKWARE APPNOTE |
| UOP | Reverse-engineered (ModernUO) |
| BSA | UESP Wiki |
| VPK | Valve Developer Wiki |
| PAK | Quake source code |
