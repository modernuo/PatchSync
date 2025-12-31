using PatchSync.Common.Storage;

namespace PatchSync.SDK.Storage;

/// <summary>
/// Storage provider backed by local file system.
/// Useful for testing and offline scenarios.
/// </summary>
public sealed class LocalStorageProvider : IStorageProvider
{
    private readonly string _basePath;

    /// <summary>
    /// Creates a local storage provider.
    /// </summary>
    /// <param name="basePath">Base directory for all file paths.</param>
    public LocalStorageProvider(string basePath)
    {
        _basePath = Path.GetFullPath(basePath);
        BaseUrl = new Uri("file:///" + _basePath.Replace('\\', '/'));
    }

    /// <inheritdoc />
    public Uri BaseUrl { get; }

    /// <inheritdoc />
    public bool SupportsMultiRange => true;

    /// <inheritdoc />
    public Task<Stream> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = GetFullPath(path);
        var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return Task.FromResult<Stream>(stream);
    }

    /// <inheritdoc />
    public Task<Stream> GetRangeAsync(
        string path,
        long offset,
        long length,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = GetFullPath(path);

        // Read the range into memory
        using var fs = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        fs.Position = offset;

        var buffer = new byte[length];
        int totalRead = 0;

        while (totalRead < length)
        {
            int bytesRead = fs.Read(buffer, totalRead, (int)(length - totalRead));
            if (bytesRead == 0)
                throw new EndOfStreamException($"Unexpected end of file at offset {offset + totalRead}");
            totalRead += bytesRead;
        }

        return Task.FromResult<Stream>(new MemoryStream(buffer, writable: false));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Stream>> GetMultiRangeAsync(
        string path,
        IReadOnlyList<(long Offset, long Length)> ranges,
        CancellationToken cancellationToken = default)
    {
        var streams = new List<Stream>(ranges.Count);

        foreach (var (offset, length) in ranges)
        {
            var stream = await GetRangeAsync(path, offset, length, cancellationToken);
            streams.Add(stream);
        }

        return streams;
    }

    private string GetFullPath(string relativePath)
    {
        // Normalize path separators
        relativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);

        // Remove leading separator if present
        if (relativePath.StartsWith(Path.DirectorySeparatorChar))
            relativePath = relativePath[1..];

        var fullPath = Path.GetFullPath(Path.Combine(_basePath, relativePath));

        // Security check: ensure path is within base path
        if (!fullPath.StartsWith(_basePath, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"Path escapes base directory: {relativePath}");

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"File not found: {relativePath}", fullPath);

        return fullPath;
    }
}
