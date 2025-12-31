using PatchSync.Common.Hashing;

namespace PatchSync.SDK.Sources;

/// <summary>
/// Represents a source that can provide chunks by their content hash.
/// Used for cross-file chunk matching (desync's "Seed" concept).
/// </summary>
public interface IChunkSource
{
    /// <summary>
    /// Tries to find a chunk by its SHA256 hash.
    /// </summary>
    /// <param name="hash">The SHA256 hash of the chunk content.</param>
    /// <param name="location">The location where the chunk can be found.</param>
    /// <returns>True if the chunk was found, false otherwise.</returns>
    bool TryGetChunk(Hash256 hash, out ChunkLocation location);

    /// <summary>
    /// Gets the total number of indexed chunks.
    /// </summary>
    int ChunkCount { get; }

    /// <summary>
    /// Gets the total size in bytes of all indexed chunks.
    /// </summary>
    long TotalSize { get; }
}

/// <summary>
/// Represents the location of a chunk within a file.
/// </summary>
/// <param name="FilePath">The path to the file containing the chunk.</param>
/// <param name="Offset">The byte offset within the file where the chunk starts.</param>
/// <param name="Length">The length of the chunk in bytes.</param>
public readonly record struct ChunkLocation(string FilePath, long Offset, int Length);
