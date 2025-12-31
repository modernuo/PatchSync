using System.Collections.Frozen;
using PatchSync.Common.Chunking;
using PatchSync.Common.Hashing;

namespace PatchSync.SDK.Sources;

/// <summary>
/// A chunk source backed by a local file.
/// Chunks the file using the specified chunker and builds an index for fast lookup.
/// </summary>
public sealed class LocalFileSource : IChunkSource
{
    private readonly string _filePath;
    private readonly FrozenDictionary<Hash256, ChunkLocation> _index;
    private readonly int _chunkCount;
    private readonly long _totalSize;

    private LocalFileSource(
        string filePath,
        FrozenDictionary<Hash256, ChunkLocation> index,
        int chunkCount,
        long totalSize)
    {
        _filePath = filePath;
        _index = index;
        _chunkCount = chunkCount;
        _totalSize = totalSize;
    }

    /// <inheritdoc />
    public int ChunkCount => _chunkCount;

    /// <inheritdoc />
    public long TotalSize => _totalSize;

    /// <summary>
    /// The path to the file this source was built from.
    /// </summary>
    public string FilePath => _filePath;

    /// <inheritdoc />
    public bool TryGetChunk(Hash256 hash, out ChunkLocation location)
    {
        return _index.TryGetValue(hash, out location);
    }

    /// <summary>
    /// Builds a chunk source from a local file.
    /// </summary>
    /// <param name="filePath">Path to the file to chunk.</param>
    /// <param name="chunker">The chunker to use.</param>
    /// <param name="options">Chunking options.</param>
    /// <returns>A chunk source with indexed chunks.</returns>
    public static LocalFileSource Build(
        string filePath,
        IChunker chunker,
        ChunkingOptions options)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("File not found", filePath);

        var builder = new Dictionary<Hash256, ChunkLocation>();
        int chunkCount = 0;
        long totalSize = 0;

        using var stream = File.OpenRead(filePath);

        foreach (var boundary in chunker.Chunk(stream, options))
        {
            var hash = new Hash256(boundary.Hash);
            var location = new ChunkLocation(filePath, boundary.Offset, boundary.Length);

            // Only store first occurrence of each hash
            // (duplicate chunks will reference the first location)
            builder.TryAdd(hash, location);

            chunkCount++;
            totalSize += boundary.Length;
        }

        return new LocalFileSource(
            filePath,
            builder.ToFrozenDictionary(),
            chunkCount,
            totalSize);
    }

    /// <summary>
    /// Builds a chunk source from a local file asynchronously.
    /// Note: Chunking is CPU-bound, so this runs on thread pool.
    /// </summary>
    public static Task<LocalFileSource> BuildAsync(
        string filePath,
        IChunker chunker,
        ChunkingOptions options,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Build(filePath, chunker, options);
        }, cancellationToken);
    }
}
