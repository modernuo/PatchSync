using PatchSync.Common.Hashing;
using PatchSync.Common.Signatures;
using PatchSync.SDK.Sources;

namespace PatchSync.SDK.Delta;

/// <summary>
/// Calculates delta between remote signature and local chunk sources.
/// </summary>
public sealed class DeltaCalculator
{
    /// <summary>
    /// Calculates a delta plan by comparing remote chunks against local sources.
    /// </summary>
    /// <param name="remoteSignature">The signature file from the remote (target) version.</param>
    /// <param name="localSources">Local chunk sources to search for existing chunks.</param>
    /// <returns>A plan describing which chunks to copy locally and which to download.</returns>
    public DeltaPlan Calculate(SignatureFile remoteSignature, IChunkSource localSources)
    {
        var actions = new List<DeltaAction>(remoteSignature.Chunks.Count);

        foreach (var chunk in remoteSignature.Chunks)
        {
            var hash = new Hash256(chunk.Hash);

            if (localSources.TryGetChunk(hash, out var location))
            {
                // Found locally - can copy from existing file
                actions.Add(new CopyLocal(chunk.Offset, chunk.Length, location, hash));
            }
            else
            {
                // Not found locally - need to download
                actions.Add(new DownloadRemote(chunk.Offset, chunk.Length, hash));
            }
        }

        return new DeltaPlan(actions, remoteSignature);
    }

    /// <summary>
    /// Calculates a delta plan when no local file exists (fresh download).
    /// All chunks will be marked for download.
    /// </summary>
    public DeltaPlan CalculateFullDownload(SignatureFile remoteSignature)
    {
        var actions = new List<DeltaAction>(remoteSignature.Chunks.Count);

        foreach (var chunk in remoteSignature.Chunks)
        {
            var hash = new Hash256(chunk.Hash);
            actions.Add(new DownloadRemote(chunk.Offset, chunk.Length, hash));
        }

        return new DeltaPlan(actions, remoteSignature);
    }
}

/// <summary>
/// Represents an action to take for a chunk.
/// </summary>
public abstract record DeltaAction(long TargetOffset, int Length, Hash256 Hash);

/// <summary>
/// Copy a chunk from a local file.
/// </summary>
public sealed record CopyLocal(
    long TargetOffset,
    int Length,
    ChunkLocation Source,
    Hash256 Hash
) : DeltaAction(TargetOffset, Length, Hash);

/// <summary>
/// Download a chunk from the remote server.
/// </summary>
public sealed record DownloadRemote(
    long TargetOffset,
    int Length,
    Hash256 Hash
) : DeltaAction(TargetOffset, Length, Hash);

/// <summary>
/// A plan describing how to reconstruct a file from local and remote chunks.
/// </summary>
public sealed class DeltaPlan
{
    public DeltaPlan(IReadOnlyList<DeltaAction> actions, SignatureFile signature)
    {
        Actions = actions;
        Signature = signature;
    }

    /// <summary>
    /// The ordered list of actions to reconstruct the file.
    /// </summary>
    public IReadOnlyList<DeltaAction> Actions { get; }

    /// <summary>
    /// The signature file this plan was calculated from.
    /// </summary>
    public SignatureFile Signature { get; }

    /// <summary>
    /// Total file size in bytes.
    /// </summary>
    public long TotalBytes => Actions.Sum(a => (long)a.Length);

    /// <summary>
    /// Bytes that need to be downloaded from remote.
    /// </summary>
    public long BytesToDownload => Actions.OfType<DownloadRemote>().Sum(a => (long)a.Length);

    /// <summary>
    /// Bytes that can be copied from local sources.
    /// </summary>
    public long BytesToCopy => Actions.OfType<CopyLocal>().Sum(a => (long)a.Length);

    /// <summary>
    /// Number of chunks to download.
    /// </summary>
    public int ChunksToDownload => Actions.OfType<DownloadRemote>().Count();

    /// <summary>
    /// Number of chunks that can be copied locally.
    /// </summary>
    public int ChunksToCopy => Actions.OfType<CopyLocal>().Count();

    /// <summary>
    /// Percentage of data that can be reused from local sources (0.0 to 1.0).
    /// </summary>
    public double LocalReuseRatio => TotalBytes > 0 ? (double)BytesToCopy / TotalBytes : 0;

    /// <summary>
    /// Gets all the byte ranges that need to be downloaded, coalesced for efficiency.
    /// </summary>
    /// <param name="maxGap">Maximum gap between ranges to merge (default 4KB).</param>
    /// <returns>Coalesced byte ranges to download.</returns>
    public IReadOnlyList<ByteRange> GetDownloadRanges(int maxGap = 4096)
    {
        var downloads = Actions
            .OfType<DownloadRemote>()
            .OrderBy(d => d.TargetOffset)
            .ToList();

        if (downloads.Count == 0)
            return Array.Empty<ByteRange>();

        var ranges = new List<ByteRange>();
        var currentStart = downloads[0].TargetOffset;
        var currentEnd = currentStart + downloads[0].Length;

        for (int i = 1; i < downloads.Count; i++)
        {
            var nextStart = downloads[i].TargetOffset;
            var nextEnd = nextStart + downloads[i].Length;

            // Merge if gap is small enough
            if (nextStart - currentEnd <= maxGap)
            {
                currentEnd = nextEnd;
            }
            else
            {
                ranges.Add(new ByteRange(currentStart, currentEnd - currentStart));
                currentStart = nextStart;
                currentEnd = nextEnd;
            }
        }

        ranges.Add(new ByteRange(currentStart, currentEnd - currentStart));
        return ranges;
    }
}

/// <summary>
/// Represents a byte range in a file.
/// </summary>
public readonly record struct ByteRange(long Offset, long Length)
{
    public long End => Offset + Length;
}
