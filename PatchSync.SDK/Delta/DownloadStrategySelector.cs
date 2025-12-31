namespace PatchSync.SDK.Delta;

/// <summary>
/// Selects the optimal download strategy based on delta analysis.
/// Compares delta download size vs compressed file size to determine best approach.
/// </summary>
public sealed class DownloadStrategySelector
{
    private readonly double _deltaThreshold;

    /// <summary>
    /// Creates a new strategy selector.
    /// </summary>
    /// <param name="deltaThreshold">
    /// Threshold for choosing delta over compressed.
    /// Delta is used if deltaBytes &lt; compressedBytes * threshold.
    /// Default: 0.8 (delta must be at least 20% better than compressed).
    /// </param>
    public DownloadStrategySelector(double deltaThreshold = 0.8)
    {
        if (deltaThreshold <= 0 || deltaThreshold > 1)
            throw new ArgumentOutOfRangeException(nameof(deltaThreshold), "Must be between 0 and 1");

        _deltaThreshold = deltaThreshold;
    }

    /// <summary>
    /// Selects the best download strategy for a file.
    /// </summary>
    /// <param name="plan">The delta plan calculated for the file.</param>
    /// <param name="compressedSize">Size of compressed fallback file (0 if not available).</param>
    /// <param name="fullSize">Full uncompressed file size.</param>
    /// <returns>The recommended download decision.</returns>
    public DownloadDecision Select(DeltaPlan plan, long compressedSize, long fullSize)
    {
        var deltaBytes = plan.BytesToDownload;
        var hasCompressed = compressedSize > 0;
        var localReuse = plan.LocalReuseRatio;

        // Case 1: No local data available - fresh install
        if (plan.BytesToCopy == 0)
        {
            if (hasCompressed && compressedSize < fullSize)
            {
                return new DownloadDecision(
                    DownloadMethod.Compressed,
                    compressedSize,
                    "No local data available, using compressed download");
            }

            return new DownloadDecision(
                DownloadMethod.Full,
                fullSize,
                "No local data available, downloading full file");
        }

        // Case 2: Compare delta vs compressed
        if (hasCompressed && deltaBytes >= compressedSize * _deltaThreshold)
        {
            return new DownloadDecision(
                DownloadMethod.Compressed,
                compressedSize,
                $"Delta ({FormatBytes(deltaBytes)}) not significantly better than compressed ({FormatBytes(compressedSize)})");
        }

        // Case 3: Delta is the best choice
        var savings = fullSize - deltaBytes;
        return new DownloadDecision(
            DownloadMethod.Delta,
            deltaBytes,
            $"Delta saves {FormatBytes(savings)} ({localReuse:P1} reused locally, {plan.ChunksToCopy} chunks)");
    }

    /// <summary>
    /// Quick check if delta patching is worthwhile without full calculation.
    /// </summary>
    /// <param name="localFileExists">Whether a local file exists.</param>
    /// <param name="localFileSize">Size of local file.</param>
    /// <param name="remoteFileSize">Size of remote file.</param>
    /// <returns>True if delta patching should be attempted.</returns>
    public static bool ShouldAttemptDelta(bool localFileExists, long localFileSize, long remoteFileSize)
    {
        // Don't attempt delta if no local file
        if (!localFileExists)
            return false;

        // Don't attempt delta for very small files (overhead not worth it)
        if (remoteFileSize < 64 * 1024) // 64 KB
            return false;

        // Don't attempt delta if local file is vastly different size (likely completely different)
        var sizeRatio = (double)localFileSize / remoteFileSize;
        if (sizeRatio < 0.1 || sizeRatio > 10)
            return false;

        return true;
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
        };
    }
}

/// <summary>
/// The method to use for downloading a file.
/// </summary>
public enum DownloadMethod
{
    /// <summary>Use delta patching with byte-range requests.</summary>
    Delta,

    /// <summary>Download the pre-compressed file and decompress.</summary>
    Compressed,

    /// <summary>Download the full uncompressed file.</summary>
    Full
}

/// <summary>
/// The decision on how to download a file.
/// </summary>
public readonly record struct DownloadDecision(
    DownloadMethod Method,
    long EstimatedBytes,
    string Reason);
