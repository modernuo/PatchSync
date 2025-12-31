namespace PatchSync.Common.Chunking;

/// <summary>
/// Registry for discovering and instantiating chunking algorithms.
/// Used for algorithm negotiation between client and server.
/// </summary>
public interface IChunkerRegistry
{
    /// <summary>
    /// List of algorithm IDs supported by this registry.
    /// </summary>
    IReadOnlyList<string> SupportedAlgorithms { get; }

    /// <summary>
    /// Attempts to get a chunker by its algorithm ID.
    /// </summary>
    /// <param name="algorithmId">The algorithm identifier (e.g., "fastcdc-v1").</param>
    /// <param name="chunker">The chunker instance if found.</param>
    /// <returns>True if the algorithm is supported; false otherwise.</returns>
    bool TryGetChunker(string algorithmId, out IChunker? chunker);

    /// <summary>
    /// Gets the preferred chunker from a list of available algorithms.
    /// Returns the first supported algorithm from the list.
    /// </summary>
    /// <param name="available">List of available algorithm IDs (from CDN manifest).</param>
    /// <returns>A chunker for the first supported algorithm.</returns>
    /// <exception cref="NotSupportedException">Thrown when no supported algorithm is found.</exception>
    IChunker GetPreferred(IEnumerable<string> available);

    /// <summary>
    /// Checks if any of the available algorithms are supported.
    /// </summary>
    /// <param name="available">List of available algorithm IDs.</param>
    /// <returns>True if at least one algorithm is supported.</returns>
    bool SupportsAny(IEnumerable<string> available);
}

/// <summary>
/// Default implementation of <see cref="IChunkerRegistry"/>.
/// AOT-compatible: uses static registration, no reflection.
/// </summary>
public sealed class ChunkerRegistry : IChunkerRegistry
{
    private readonly Dictionary<string, Func<IChunker>> _factories;

    /// <summary>
    /// Default registry with all built-in chunking algorithms.
    /// </summary>
    public static ChunkerRegistry Default { get; } = new();

    /// <summary>
    /// Creates a registry with the default set of chunkers.
    /// </summary>
    public ChunkerRegistry() : this(GetDefaultFactories())
    {
    }

    /// <summary>
    /// Creates a registry with custom chunker factories.
    /// </summary>
    /// <param name="factories">Dictionary mapping algorithm IDs to factory functions.</param>
    public ChunkerRegistry(Dictionary<string, Func<IChunker>> factories)
    {
        _factories = factories ?? throw new ArgumentNullException(nameof(factories));
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedAlgorithms => _factories.Keys.ToList();

    /// <inheritdoc />
    public bool TryGetChunker(string algorithmId, out IChunker? chunker)
    {
        if (_factories.TryGetValue(algorithmId, out var factory))
        {
            chunker = factory();
            return true;
        }
        chunker = null;
        return false;
    }

    /// <inheritdoc />
    public IChunker GetPreferred(IEnumerable<string> available)
    {
        foreach (var algorithmId in available)
        {
            if (TryGetChunker(algorithmId, out var chunker))
            {
                return chunker!;
            }
        }

        throw new NotSupportedException(
            $"No supported chunking algorithms found. Client supports: [{string.Join(", ", SupportedAlgorithms)}]. " +
            $"Available: [{string.Join(", ", available)}]. Please update your client.");
    }

    /// <inheritdoc />
    public bool SupportsAny(IEnumerable<string> available)
    {
        return available.Any(a => _factories.ContainsKey(a));
    }

    /// <summary>
    /// Registers an additional chunker factory.
    /// </summary>
    /// <param name="algorithmId">The algorithm identifier.</param>
    /// <param name="factory">Factory function to create the chunker.</param>
    public void Register(string algorithmId, Func<IChunker> factory)
    {
        _factories[algorithmId] = factory;
    }

    private static Dictionary<string, Func<IChunker>> GetDefaultFactories()
    {
        return new Dictionary<string, Func<IChunker>>
        {
            ["fastcdc-v1"] = static () => new FastCDCChunker(),
            // Future algorithms can be added here:
            // ["buzhash-casync-v1"] = static () => new BuzhashChunker(),
            // ["seqcdc-v1"] = static () => new SeqCDCChunker(),
        };
    }
}
