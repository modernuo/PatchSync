namespace PatchSync.SDK.Containers;

/// <summary>
/// Registry for discovering and instantiating container handlers.
/// AOT-compatible: uses static registration, no reflection.
/// </summary>
public sealed class ContainerRegistry
{
    private readonly Dictionary<string, Func<IContainerHandler>> _formatFactories;
    private readonly Dictionary<string, string> _extensionToFormat;

    /// <summary>
    /// Default registry with all built-in container handlers.
    /// </summary>
    public static ContainerRegistry Default { get; } = new();

    /// <summary>
    /// Creates a registry with the default set of handlers.
    /// </summary>
    public ContainerRegistry() : this(GetDefaultFactories())
    {
    }

    /// <summary>
    /// Creates a registry with custom handler factories.
    /// </summary>
    /// <param name="factories">Dictionary mapping format IDs to factory functions.</param>
    public ContainerRegistry(Dictionary<string, Func<IContainerHandler>> factories)
    {
        _formatFactories = factories ?? throw new ArgumentNullException(nameof(factories));
        _extensionToFormat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Build extension lookup
        foreach (var (formatId, factory) in _formatFactories)
        {
            var handler = factory();
            foreach (var ext in handler.SupportedExtensions)
            {
                _extensionToFormat[ext] = formatId;
            }
        }
    }

    /// <summary>
    /// List of format IDs supported by this registry.
    /// </summary>
    public IReadOnlyList<string> SupportedFormats => _formatFactories.Keys.ToList();

    /// <summary>
    /// List of file extensions that have registered handlers.
    /// </summary>
    public IReadOnlyList<string> SupportedExtensions => _extensionToFormat.Keys.ToList();

    /// <summary>
    /// Attempts to get a handler by its format ID.
    /// </summary>
    /// <param name="formatId">The format identifier (e.g., "uop-v1").</param>
    /// <param name="handler">The handler instance if found.</param>
    /// <returns>True if the format is supported; false otherwise.</returns>
    public bool TryGetHandler(string formatId, out IContainerHandler? handler)
    {
        if (_formatFactories.TryGetValue(formatId, out var factory))
        {
            handler = factory();
            return true;
        }
        handler = null;
        return false;
    }

    /// <summary>
    /// Attempts to get a handler for a file extension.
    /// </summary>
    /// <param name="extension">The file extension (e.g., ".uop").</param>
    /// <param name="handler">The handler instance if found.</param>
    /// <returns>True if a handler is registered for this extension.</returns>
    public bool TryGetHandlerForExtension(string extension, out IContainerHandler? handler)
    {
        if (_extensionToFormat.TryGetValue(extension, out var formatId))
        {
            return TryGetHandler(formatId, out handler);
        }
        handler = null;
        return false;
    }

    /// <summary>
    /// Attempts to detect and get a handler for a stream by examining its content.
    /// Tries each registered handler's CanHandle method.
    /// </summary>
    /// <param name="stream">The stream to examine. Position will be reset.</param>
    /// <param name="handler">The handler instance if found.</param>
    /// <returns>True if a handler can parse this stream.</returns>
    public bool TryDetectHandler(Stream stream, out IContainerHandler? handler)
    {
        var originalPosition = stream.Position;

        foreach (var factory in _formatFactories.Values)
        {
            stream.Position = originalPosition;
            var candidate = factory();

            try
            {
                if (candidate.CanHandle(stream))
                {
                    stream.Position = originalPosition;
                    handler = candidate;
                    return true;
                }
            }
            catch
            {
                // Handler threw during detection - skip it
            }
        }

        stream.Position = originalPosition;
        handler = null;
        return false;
    }

    /// <summary>
    /// Gets a handler for a file, trying extension first, then content detection.
    /// </summary>
    /// <param name="filePath">Path to the container file.</param>
    /// <returns>A handler for the file.</returns>
    /// <exception cref="NotSupportedException">No handler found for this file.</exception>
    public IContainerHandler GetHandler(string filePath)
    {
        var ext = Path.GetExtension(filePath);

        // Try extension first
        if (TryGetHandlerForExtension(ext, out var handler))
        {
            return handler!;
        }

        // Try content detection
        using var stream = File.OpenRead(filePath);
        if (TryDetectHandler(stream, out handler))
        {
            return handler!;
        }

        throw new NotSupportedException(
            $"No container handler found for '{filePath}'. " +
            $"Supported extensions: {string.Join(", ", SupportedExtensions)}");
    }

    /// <summary>
    /// Checks if a file extension has a registered handler.
    /// </summary>
    /// <param name="extension">The file extension (e.g., ".uop").</param>
    /// <returns>True if supported.</returns>
    public bool IsSupported(string extension)
    {
        return _extensionToFormat.ContainsKey(extension);
    }

    /// <summary>
    /// Registers an additional handler factory.
    /// </summary>
    /// <param name="formatId">The format identifier.</param>
    /// <param name="factory">Factory function to create the handler.</param>
    public void Register(string formatId, Func<IContainerHandler> factory)
    {
        _formatFactories[formatId] = factory;

        // Update extension lookup
        var handler = factory();
        foreach (var ext in handler.SupportedExtensions)
        {
            _extensionToFormat[ext] = formatId;
        }
    }

    private static Dictionary<string, Func<IContainerHandler>> GetDefaultFactories()
    {
        return new Dictionary<string, Func<IContainerHandler>>
        {
            ["uop-v1"] = static () => new Handlers.UopHandler(),

            // Future handlers:
            // ["zip-v1"] = static () => new ZipHandler(),
            // ["bsa-v1"] = static () => new BsaHandler(),
        };
    }
}
