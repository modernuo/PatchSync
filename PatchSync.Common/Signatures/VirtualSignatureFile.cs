using PatchSync.Common.Chunking;
using PatchSync.Common.Containers;

namespace PatchSync.Common.Signatures;

/// <summary>
/// Virtual signature file for container formats.
/// Extends the standard signature model with entry-level information
/// and layout data for byte-identical reconstruction.
/// </summary>
public sealed class VirtualSignatureFile
{
    /// <summary>Container format identifier (e.g., "uop-v1", "zip-v1").</summary>
    public required string ContainerFormat { get; init; }

    /// <summary>Chunking algorithm used for sub-entry CDC (e.g., "fastcdc-v1").</summary>
    public required string AlgorithmId { get; init; }

    /// <summary>Minimum chunk size for CDC.</summary>
    public required int MinSize { get; init; }

    /// <summary>Average chunk size for CDC.</summary>
    public required int AverageSize { get; init; }

    /// <summary>Maximum chunk size for CDC.</summary>
    public required int MaxSize { get; init; }

    /// <summary>Total size of the target container file.</summary>
    public required long TotalSize { get; init; }

    /// <summary>SHA256 hash of the complete container file.</summary>
    public required byte[] ContainerHash { get; init; }

    /// <summary>Layout information for reconstruction.</summary>
    public required ContainerLayout Layout { get; init; }

    /// <summary>Entry signatures (one per container entry).</summary>
    public required IReadOnlyList<EntrySignature> Entries { get; init; }

    /// <summary>
    /// Total bytes that would need to be downloaded if no local data exists.
    /// </summary>
    public long TotalEntryBytes => Entries.Sum(e => (long)e.StoredSize);

    /// <summary>
    /// Number of entries that have CDC sub-chunks.
    /// </summary>
    public int ChunkedEntryCount => Entries.Count(e => e.Chunks is { Count: > 0 });

    /// <summary>
    /// Total number of CDC chunks across all entries.
    /// </summary>
    public int TotalChunkCount => Entries.Sum(e => e.Chunks?.Count ?? 0);

    /// <summary>
    /// Extracts chunking options from this signature.
    /// </summary>
    public ChunkingOptions GetChunkingOptions()
    {
        return new ChunkingOptions
        {
            MinSize = MinSize,
            AverageSize = AverageSize,
            MaxSize = MaxSize
        };
    }
}

/// <summary>
/// Signature for a single container entry.
/// Contains entry-level hash and optionally CDC chunks for large uncompressed entries.
/// </summary>
public sealed class EntrySignature
{
    /// <summary>Entry identifier (path, hash-based ID, or index).</summary>
    public required string EntryId { get; init; }

    /// <summary>Byte offset where this entry appears in the target container.</summary>
    public required long TargetOffset { get; init; }

    /// <summary>Size of entry data as stored (may be compressed).</summary>
    public required int StoredSize { get; init; }

    /// <summary>Compression type used for this entry.</summary>
    public ContainerCompression Compression { get; init; } = ContainerCompression.None;

    /// <summary>SHA256 hash of the stored (raw) bytes.</summary>
    public required byte[] StoredHash { get; init; }

    /// <summary>
    /// CDC chunks for this entry. Only populated for large uncompressed entries.
    /// Null means entry-level matching only (no sub-entry delta).
    /// </summary>
    public IReadOnlyList<SignatureChunk>? Chunks { get; init; }

    /// <summary>
    /// Returns the stored hash as a hex string.
    /// </summary>
    public string StoredHashHex => Convert.ToHexString(StoredHash);

    /// <summary>
    /// Whether this entry has sub-entry CDC chunks.
    /// </summary>
    public bool HasChunks => Chunks is { Count: > 0 };

    /// <summary>
    /// End offset (exclusive) of this entry in the target container.
    /// </summary>
    public long TargetEnd => TargetOffset + StoredSize;
}

/// <summary>
/// Target container layout for byte-identical reconstruction.
/// Contains the header template and entry positions.
/// </summary>
public sealed class ContainerLayout
{
    /// <summary>
    /// Format-specific header template.
    /// This is the raw header bytes that should be written to the output container.
    /// May include block tables, directory structures, etc.
    /// </summary>
    public required byte[] HeaderTemplate { get; init; }

    /// <summary>
    /// Layout information for each entry.
    /// </summary>
    public required IReadOnlyList<EntryLayout> Entries { get; init; }

    /// <summary>
    /// Size of the complete container file.
    /// </summary>
    public required long TotalSize { get; init; }
}

/// <summary>
/// Layout information for a single entry in the target container.
/// </summary>
public sealed class EntryLayout
{
    /// <summary>Entry identifier (matches EntrySignature.EntryId).</summary>
    public required string EntryId { get; init; }

    /// <summary>Byte offset where entry data begins in the target container.</summary>
    public required long Offset { get; init; }

    /// <summary>Size of per-entry header (e.g., UOP entry metadata before data).</summary>
    public int HeaderSize { get; init; }

    /// <summary>Size of entry data.</summary>
    public required int DataSize { get; init; }

    /// <summary>
    /// Per-entry header bytes (if any).
    /// Written to container before entry data.
    /// </summary>
    public byte[]? HeaderBytes { get; init; }
}
