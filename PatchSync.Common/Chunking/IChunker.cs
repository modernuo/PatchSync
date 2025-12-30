namespace PatchSync.Common.Chunking;

/// <summary>
/// Defines a content-defined chunking algorithm.
/// Different algorithms produce different chunk boundaries for the same input.
/// Build server and client MUST use the same algorithm for compatible signatures.
/// </summary>
public interface IChunker
{
    /// <summary>
    /// Unique algorithm identifier (e.g., "fastcdc-v1", "buzhash-casync-v1").
    /// This ID is stored in signature files to ensure algorithm compatibility.
    /// </summary>
    string AlgorithmId { get; }

    /// <summary>
    /// Chunks a stream into content-defined boundaries.
    /// </summary>
    /// <param name="input">The input stream to chunk.</param>
    /// <param name="options">Chunking parameters (min/avg/max sizes).</param>
    /// <returns>Enumerable of chunk boundaries with offset, length, and hash.</returns>
    IEnumerable<ChunkBoundary> Chunk(Stream input, ChunkingOptions options);
}

/// <summary>
/// Represents a single chunk boundary produced by a chunking algorithm.
/// </summary>
/// <param name="Offset">Byte offset from the start of the stream.</param>
/// <param name="Length">Length of the chunk in bytes.</param>
/// <param name="Hash">SHA256 hash of the chunk data (32 bytes).</param>
public readonly record struct ChunkBoundary(long Offset, int Length, byte[] Hash)
{
    /// <summary>
    /// End offset (exclusive) of this chunk.
    /// </summary>
    public long End => Offset + Length;
}

/// <summary>
/// Options for controlling chunk sizes in content-defined chunking.
/// </summary>
public sealed class ChunkingOptions
{
    /// <summary>
    /// Default chunking options: 4KB min, 16KB avg, 64KB max.
    /// Suitable for files with frequent small changes (executables, configs).
    /// </summary>
    public static ChunkingOptions Default { get; } = new();

    /// <summary>
    /// Large file options: 16KB min, 64KB avg, 256KB max.
    /// Suitable for large asset files where less granularity is acceptable.
    /// </summary>
    public static ChunkingOptions LargeFiles { get; } = new()
    {
        MinSize = 16 * 1024,
        AverageSize = 64 * 1024,
        MaxSize = 256 * 1024
    };

    /// <summary>
    /// Minimum chunk size in bytes. No chunk will be smaller than this.
    /// Default: 4096 (4 KB)
    /// </summary>
    public int MinSize { get; init; } = 4096;

    /// <summary>
    /// Target average chunk size in bytes. The algorithm uses normalized
    /// chunking to produce chunks clustered around this size.
    /// Default: 16384 (16 KB)
    /// </summary>
    public int AverageSize { get; init; } = 16384;

    /// <summary>
    /// Maximum chunk size in bytes. Chunks are forced to split at this boundary.
    /// Default: 65536 (64 KB)
    /// </summary>
    public int MaxSize { get; init; } = 65536;

    /// <summary>
    /// Normalization level (0-3). Higher values produce more uniform chunk sizes.
    /// 0 = disabled (geometric distribution), 2 = default (good uniformity).
    /// Default: 2
    /// </summary>
    public int Normalization { get; init; } = 2;

    /// <summary>
    /// Validates the options and throws if invalid.
    /// </summary>
    public void Validate()
    {
        if (MinSize < 64)
            throw new ArgumentOutOfRangeException(nameof(MinSize), "MinSize must be at least 64 bytes");
        if (MaxSize > 1 << 30)
            throw new ArgumentOutOfRangeException(nameof(MaxSize), "MaxSize must be at most 1 GiB");
        if (MinSize >= MaxSize)
            throw new ArgumentException("MinSize must be less than MaxSize");
        if (AverageSize < MinSize || AverageSize > MaxSize)
            throw new ArgumentException("AverageSize must be between MinSize and MaxSize");
        if (Normalization < 0 || Normalization > 3)
            throw new ArgumentOutOfRangeException(nameof(Normalization), "Normalization must be 0, 1, 2, or 3");
    }
}
