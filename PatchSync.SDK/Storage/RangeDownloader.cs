using PatchSync.Common.Storage;
using PatchSync.SDK.Delta;

namespace PatchSync.SDK.Storage;

/// <summary>
/// Downloads byte ranges efficiently using coalescing and parallel downloads.
/// </summary>
public sealed class RangeDownloader
{
    private readonly IStorageProvider _storage;
    private readonly RangeDownloaderOptions _options;

    public RangeDownloader(IStorageProvider storage, RangeDownloaderOptions? options = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _options = options ?? new RangeDownloaderOptions();
    }

    /// <summary>
    /// Downloads coalesced ranges in parallel and writes them to the target stream.
    /// </summary>
    /// <param name="remotePath">Path to remote file.</param>
    /// <param name="ranges">Coalesced byte ranges to download.</param>
    /// <param name="target">Target stream to write to (must support seeking).</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task DownloadRangesToStreamAsync(
        string remotePath,
        IReadOnlyList<ByteRange> ranges,
        Stream target,
        IProgress<RangeDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (ranges.Count == 0)
        {
            progress?.Report(new RangeDownloadProgress(0, 0, 0, 0, 0, true));
            return;
        }

        var totalBytes = ranges.Sum(r => r.Length);
        var downloadedBytes = 0L;
        var rangesComplete = 0;
        var writeLock = new SemaphoreSlim(1, 1);

        await Parallel.ForEachAsync(
            ranges,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _options.MaxConcurrency,
                CancellationToken = cancellationToken
            },
            async (range, ct) =>
            {
                // Download the range
                await using var remoteStream = await _storage.GetRangeAsync(
                    remotePath, range.Offset, (int)range.Length, ct);

                var buffer = new byte[Math.Min(range.Length, _options.BufferSize)];
                var remaining = range.Length;
                var rangeOffset = 0L;

                while (remaining > 0)
                {
                    var toRead = (int)Math.Min(remaining, buffer.Length);
                    var bytesRead = await remoteStream.ReadAsync(buffer.AsMemory(0, toRead), ct);

                    if (bytesRead == 0)
                        throw new EndOfStreamException($"Unexpected end of stream at offset {range.Offset + rangeOffset}");

                    // Write to target with lock (seeking required)
                    await writeLock.WaitAsync(ct);
                    try
                    {
                        target.Position = range.Offset + rangeOffset;
                        await target.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    }
                    finally
                    {
                        writeLock.Release();
                    }

                    remaining -= bytesRead;
                    rangeOffset += bytesRead;

                    var downloaded = Interlocked.Add(ref downloadedBytes, bytesRead);
                    progress?.Report(new RangeDownloadProgress(
                        downloaded, totalBytes, rangesComplete, ranges.Count, bytesRead, false));
                }

                Interlocked.Increment(ref rangesComplete);
            });

        progress?.Report(new RangeDownloadProgress(
            totalBytes, totalBytes, ranges.Count, ranges.Count, 0, true));
    }

    /// <summary>
    /// Coalesces adjacent/nearby ranges for more efficient downloading.
    /// </summary>
    /// <param name="ranges">Original ranges.</param>
    /// <param name="maxGap">Maximum gap between ranges to merge.</param>
    /// <returns>Coalesced ranges.</returns>
    public static IReadOnlyList<ByteRange> CoalesceRanges(
        IEnumerable<ByteRange> ranges,
        int maxGap = 4096)
    {
        var sorted = ranges.OrderBy(r => r.Offset).ToList();
        if (sorted.Count == 0)
            return Array.Empty<ByteRange>();

        var result = new List<ByteRange>();
        var currentStart = sorted[0].Offset;
        var currentEnd = sorted[0].End;

        for (int i = 1; i < sorted.Count; i++)
        {
            var next = sorted[i];
            if (next.Offset - currentEnd <= maxGap)
            {
                currentEnd = Math.Max(currentEnd, next.End);
            }
            else
            {
                result.Add(new ByteRange(currentStart, currentEnd - currentStart));
                currentStart = next.Offset;
                currentEnd = next.End;
            }
        }

        result.Add(new ByteRange(currentStart, currentEnd - currentStart));
        return result;
    }
}

/// <summary>
/// Options for range downloading.
/// </summary>
public sealed class RangeDownloaderOptions
{
    /// <summary>Maximum concurrent range downloads.</summary>
    public int MaxConcurrency { get; init; } = 4;

    /// <summary>Buffer size for reading/writing.</summary>
    public int BufferSize { get; init; } = 81920;

    /// <summary>Maximum gap between ranges to merge (bytes).</summary>
    public int MaxCoalesceGap { get; init; } = 65536; // 64KB default
}

/// <summary>
/// Progress during range downloading.
/// </summary>
public readonly record struct RangeDownloadProgress(
    long BytesDownloaded,
    long BytesTotal,
    int RangesComplete,
    int RangesTotal,
    long BytesDelta,
    bool IsComplete)
{
    public double Percentage => BytesTotal > 0 ? (double)BytesDownloaded / BytesTotal : 0;
}
