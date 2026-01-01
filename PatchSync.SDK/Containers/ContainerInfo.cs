using PatchSync.Common.Containers;

namespace PatchSync.SDK.Containers;

/// <summary>
/// Parsed information about a container file.
/// </summary>
public sealed class ContainerInfo
{
    /// <summary>
    /// Format identifier (e.g., "uop-v1", "zip-v1").
    /// </summary>
    public required string FormatId { get; init; }

    /// <summary>
    /// Total container file size in bytes.
    /// </summary>
    public required long TotalSize { get; init; }

    /// <summary>
    /// Entries in this container.
    /// </summary>
    public required IReadOnlyList<ContainerEntry> Entries { get; init; }

    /// <summary>
    /// Format-specific metadata (header bytes, version info, etc.).
    /// </summary>
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Total bytes of all entry data.
    /// </summary>
    public long TotalEntryBytes => Entries.Sum(e => (long)e.StoredSize);

    /// <summary>
    /// Number of compressed entries.
    /// </summary>
    public int CompressedEntryCount => Entries.Count(e => e.Compression != ContainerCompression.None);

    /// <summary>
    /// Number of uncompressed entries.
    /// </summary>
    public int UncompressedEntryCount => Entries.Count(e => e.Compression == ContainerCompression.None);
}

/// <summary>
/// A single entry within a container.
/// </summary>
public sealed class ContainerEntry
{
    /// <summary>
    /// Entry identifier (path-like key, hash-based ID, or index).
    /// Must be unique within the container.
    /// </summary>
    public required string EntryId { get; init; }

    /// <summary>
    /// Byte offset within container where entry data starts.
    /// </summary>
    public required long Offset { get; init; }

    /// <summary>
    /// Size of entry data as stored (may be compressed).
    /// </summary>
    public required int StoredSize { get; init; }

    /// <summary>
    /// Size of entry data when decompressed.
    /// Same as StoredSize if uncompressed.
    /// </summary>
    public int DecompressedSize { get; init; }

    /// <summary>
    /// Compression type used for this entry.
    /// </summary>
    public ContainerCompression Compression { get; init; } = ContainerCompression.None;

    /// <summary>
    /// SHA256 hash of the stored (raw) bytes.
    /// Computed lazily during signature generation.
    /// </summary>
    public byte[]? StoredHash { get; set; }

    /// <summary>
    /// SHA256 hash of decompressed bytes (if different from stored).
    /// </summary>
    public byte[]? DecompressedHash { get; set; }

    /// <summary>
    /// Size of any per-entry header that precedes the data.
    /// </summary>
    public int HeaderSize { get; init; }

    /// <summary>
    /// Raw per-entry header bytes (if any).
    /// Used for reconstruction.
    /// </summary>
    public byte[]? HeaderBytes { get; init; }

    /// <summary>
    /// Format-specific extra data (e.g., UOP file hash).
    /// </summary>
    public ulong ExtraData { get; init; }

    /// <summary>
    /// Whether this entry is large enough for CDC sub-chunking.
    /// Only uncompressed entries >= 64KB are chunked.
    /// </summary>
    public bool IsLargeUncompressed => Compression == ContainerCompression.None && StoredSize >= 65536;

    /// <summary>
    /// Whether this entry is compressed.
    /// </summary>
    public bool IsCompressed => Compression != ContainerCompression.None;

    /// <summary>
    /// End offset (exclusive) of this entry's data in the container.
    /// </summary>
    public long End => Offset + StoredSize;
}
