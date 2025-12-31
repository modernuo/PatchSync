namespace PatchSync.Common.Storage;

/// <summary>
/// Abstraction for downloading files from remote storage (CDN, S3, etc.).
/// </summary>
public interface IStorageProvider
{
    /// <summary>
    /// Downloads a complete file.
    /// </summary>
    /// <param name="path">Relative path to the file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Stream containing the file contents.</returns>
    Task<Stream> GetAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a byte range from a file.
    /// </summary>
    /// <param name="path">Relative path to the file.</param>
    /// <param name="offset">Start byte offset (inclusive).</param>
    /// <param name="length">Number of bytes to download.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Stream containing the requested bytes.</returns>
    Task<Stream> GetRangeAsync(
        string path,
        long offset,
        long length,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads multiple byte ranges from a file in a single request (if supported).
    /// Falls back to sequential requests if multi-range not supported.
    /// </summary>
    /// <param name="path">Relative path to the file.</param>
    /// <param name="ranges">List of (offset, length) ranges to download.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Streams for each requested range, in order.</returns>
    Task<IReadOnlyList<Stream>> GetMultiRangeAsync(
        string path,
        IReadOnlyList<(long Offset, long Length)> ranges,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether this provider supports multi-range requests (RFC 7233).
    /// </summary>
    bool SupportsMultiRange { get; }

    /// <summary>
    /// Base URL for this storage provider.
    /// </summary>
    Uri BaseUrl { get; }
}

/// <summary>
/// Progress information for downloads.
/// </summary>
public readonly record struct DownloadProgress(
    long BytesDownloaded,
    long TotalBytes,
    double BytesPerSecond,
    string CurrentFile);
