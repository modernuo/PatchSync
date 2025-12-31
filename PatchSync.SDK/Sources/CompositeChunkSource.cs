using PatchSync.Common.Hashing;

namespace PatchSync.SDK.Sources;

/// <summary>
/// A chunk source that combines multiple sources for cross-file chunk matching.
/// Sources are searched in order (first added = highest priority).
/// </summary>
public sealed class CompositeChunkSource : IChunkSource
{
    private readonly List<IChunkSource> _sources = new();

    /// <inheritdoc />
    public int ChunkCount => _sources.Sum(s => s.ChunkCount);

    /// <inheritdoc />
    public long TotalSize => _sources.Sum(s => s.TotalSize);

    /// <summary>
    /// Gets the number of sources in this composite.
    /// </summary>
    public int SourceCount => _sources.Count;

    /// <summary>
    /// Adds a chunk source to search.
    /// Sources added first have higher priority.
    /// </summary>
    public void AddSource(IChunkSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _sources.Add(source);
    }

    /// <summary>
    /// Adds multiple chunk sources.
    /// </summary>
    public void AddSources(IEnumerable<IChunkSource> sources)
    {
        foreach (var source in sources)
        {
            AddSource(source);
        }
    }

    /// <inheritdoc />
    public bool TryGetChunk(Hash256 hash, out ChunkLocation location)
    {
        // Search sources in order (first added = highest priority)
        foreach (var source in _sources)
        {
            if (source.TryGetChunk(hash, out location))
                return true;
        }

        location = default;
        return false;
    }

    /// <summary>
    /// Creates a composite chunk source from multiple local files.
    /// The target file should be added first for best performance.
    /// </summary>
    /// <param name="filePaths">Paths to local files to index.</param>
    /// <param name="chunker">The chunker to use.</param>
    /// <param name="options">Chunking options.</param>
    /// <returns>A composite chunk source.</returns>
    public static CompositeChunkSource FromFiles(
        IEnumerable<string> filePaths,
        Common.Chunking.IChunker chunker,
        Common.Chunking.ChunkingOptions options)
    {
        var composite = new CompositeChunkSource();

        foreach (var path in filePaths)
        {
            if (File.Exists(path))
            {
                var source = LocalFileSource.Build(path, chunker, options);
                composite.AddSource(source);
            }
        }

        return composite;
    }

    /// <summary>
    /// Creates a composite chunk source from multiple local files asynchronously.
    /// </summary>
    public static async Task<CompositeChunkSource> FromFilesAsync(
        IEnumerable<string> filePaths,
        Common.Chunking.IChunker chunker,
        Common.Chunking.ChunkingOptions options,
        CancellationToken cancellationToken = default)
    {
        var composite = new CompositeChunkSource();
        var paths = filePaths.Where(File.Exists).ToList();

        // Build sources in parallel
        var tasks = paths.Select(path =>
            LocalFileSource.BuildAsync(path, chunker, options, cancellationToken));

        var sources = await Task.WhenAll(tasks);

        foreach (var source in sources)
        {
            composite.AddSource(source);
        }

        return composite;
    }
}
