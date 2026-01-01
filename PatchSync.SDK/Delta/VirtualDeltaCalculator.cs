using PatchSync.Common.Hashing;
using PatchSync.Common.Signatures;
using PatchSync.SDK.Sources;

namespace PatchSync.SDK.Delta;

/// <summary>
/// Method for obtaining an entry's data.
/// </summary>
public enum EntryMethod
{
    /// <summary>Copy entire entry from local container.</summary>
    CopyLocal,

    /// <summary>Use CDC delta - some chunks local, some downloaded.</summary>
    DeltaChunks,

    /// <summary>Download entire entry from remote.</summary>
    DownloadFull
}

/// <summary>
/// Plan for a single entry within a container.
/// </summary>
public sealed class EntryPlan
{
    /// <summary>Entry identifier.</summary>
    public required string EntryId { get; init; }

    /// <summary>Target offset in the container (where DATA starts, after header).</summary>
    public required long TargetOffset { get; init; }

    /// <summary>Entry data size (not including header).</summary>
    public required int Size { get; init; }

    /// <summary>Per-entry header size (bytes before data at TargetOffset - HeaderSize).</summary>
    public int HeaderSize { get; init; }

    /// <summary>Method for obtaining this entry.</summary>
    public required EntryMethod Method { get; init; }

    /// <summary>Local entry location (for CopyLocal).</summary>
    public EntryLocation? LocalSource { get; init; }

    /// <summary>Chunk-level plan (for DeltaChunks).</summary>
    public DeltaPlan? ChunkPlan { get; init; }

    /// <summary>Combined header + data size for full copy/download.</summary>
    public int TotalSize => HeaderSize + Size;

    /// <summary>Offset where header starts (TargetOffset - HeaderSize).</summary>
    public long HeaderOffset => TargetOffset - HeaderSize;

    /// <summary>Bytes to download for this entry.</summary>
    public long BytesToDownload => Method switch
    {
        EntryMethod.CopyLocal => HeaderSize, // Header downloaded, data copied
        EntryMethod.DownloadFull => TotalSize, // Header + data
        EntryMethod.DeltaChunks => HeaderSize + (ChunkPlan?.BytesToDownload ?? Size), // Header + delta chunks
        _ => TotalSize
    };

    /// <summary>Bytes to copy locally for this entry.</summary>
    public long BytesToCopy => Method switch
    {
        EntryMethod.CopyLocal => Size, // Only data copied (header is downloaded)
        EntryMethod.DownloadFull => 0,
        EntryMethod.DeltaChunks => ChunkPlan?.BytesToCopy ?? 0, // Only data chunks, header is downloaded
        _ => 0
    };
}

/// <summary>
/// Complete plan for reconstructing a container.
/// </summary>
public sealed class VirtualDeltaPlan
{
    /// <summary>The virtual signature this plan was calculated from.</summary>
    public required VirtualSignatureFile Signature { get; init; }

    /// <summary>Plans for each entry.</summary>
    public required IReadOnlyList<EntryPlan> Entries { get; init; }

    /// <summary>Total container size.</summary>
    public long TotalBytes => Signature.TotalSize;

    /// <summary>Bytes that need to be downloaded.</summary>
    public long BytesToDownload => Entries.Sum(e => e.BytesToDownload);

    /// <summary>Bytes that can be copied locally.</summary>
    public long BytesToCopy => Entries.Sum(e => e.BytesToCopy);

    /// <summary>Local reuse ratio (0.0 to 1.0).</summary>
    public double LocalReuseRatio => TotalBytes > 0 ? (double)BytesToCopy / TotalBytes : 0;

    /// <summary>Number of entries that match locally.</summary>
    public int EntriesLocalMatch => Entries.Count(e => e.Method == EntryMethod.CopyLocal);

    /// <summary>Number of entries using delta chunks.</summary>
    public int EntriesDeltaChunks => Entries.Count(e => e.Method == EntryMethod.DeltaChunks);

    /// <summary>Number of entries requiring full download.</summary>
    public int EntriesFullDownload => Entries.Count(e => e.Method == EntryMethod.DownloadFull);

    /// <summary>
    /// Gets all byte ranges that need to be downloaded from the container.
    /// </summary>
    /// <param name="maxGap">Maximum gap between ranges to merge.</param>
    public IReadOnlyList<ByteRange> GetDownloadRanges(int maxGap = 4096)
    {
        var allRanges = new List<ByteRange>();

        foreach (var entry in Entries)
        {
            switch (entry.Method)
            {
                case EntryMethod.DownloadFull:
                    allRanges.Add(new ByteRange(entry.TargetOffset, entry.Size));
                    break;

                case EntryMethod.DeltaChunks when entry.ChunkPlan != null:
                    // Get chunk-level ranges and adjust to container offset
                    foreach (var range in entry.ChunkPlan.GetDownloadRanges(maxGap))
                    {
                        // Chunk offsets are relative to entry; add entry offset
                        allRanges.Add(new ByteRange(entry.TargetOffset + range.Offset, range.Length));
                    }
                    break;
            }
        }

        // Sort and coalesce
        return CoalesceRanges(allRanges.OrderBy(r => r.Offset).ToList(), maxGap);
    }

    private static IReadOnlyList<ByteRange> CoalesceRanges(List<ByteRange> sorted, int maxGap)
    {
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
/// Calculates delta plans for virtual container signatures.
/// </summary>
public sealed class VirtualDeltaCalculator
{
    private readonly double _minChunkReuseRatio;

    /// <summary>
    /// Creates a virtual delta calculator.
    /// </summary>
    /// <param name="minChunkReuseRatio">Minimum local chunk reuse ratio to use delta (default 20%).</param>
    public VirtualDeltaCalculator(double minChunkReuseRatio = 0.2)
    {
        _minChunkReuseRatio = minChunkReuseRatio;
    }

    /// <summary>
    /// Calculates a virtual delta plan by comparing target signature against local sources.
    /// </summary>
    /// <param name="targetSignature">The virtual signature from the server.</param>
    /// <param name="localSource">Local container source to search for entries/chunks.</param>
    /// <returns>A plan describing how to reconstruct the container.</returns>
    public VirtualDeltaPlan Calculate(VirtualSignatureFile targetSignature, VirtualContainerSource? localSource)
    {
        var entryPlans = new List<EntryPlan>(targetSignature.Entries.Count);

        // Build lookup for header sizes from layout
        var headerSizes = targetSignature.Layout.Entries.ToDictionary(e => e.EntryId, e => e.HeaderSize);

        foreach (var entry in targetSignature.Entries)
        {
            var entryHash = new Hash256(entry.StoredHash);
            var headerSize = headerSizes.GetValueOrDefault(entry.EntryId, 0);

            // 1. Try entry-level match first
            if (localSource != null && localSource.TryGetEntry(entryHash, out var entryLocation))
            {
                entryPlans.Add(new EntryPlan
                {
                    EntryId = entry.EntryId,
                    TargetOffset = entry.TargetOffset,
                    Size = entry.StoredSize,
                    HeaderSize = headerSize,
                    Method = EntryMethod.CopyLocal,
                    LocalSource = entryLocation
                });
                continue;
            }

            // 2. Try chunk-level matching for entries with CDC chunks
            if (entry.HasChunks && localSource != null)
            {
                var chunkPlan = CalculateChunkPlan(entry, localSource);

                // Use delta if we can reuse enough locally
                if (chunkPlan.LocalReuseRatio >= _minChunkReuseRatio)
                {
                    entryPlans.Add(new EntryPlan
                    {
                        EntryId = entry.EntryId,
                        TargetOffset = entry.TargetOffset,
                        Size = entry.StoredSize,
                        HeaderSize = headerSize,
                        Method = EntryMethod.DeltaChunks,
                        ChunkPlan = chunkPlan
                    });
                    continue;
                }
            }

            // 3. Fall back to full entry download
            entryPlans.Add(new EntryPlan
            {
                EntryId = entry.EntryId,
                TargetOffset = entry.TargetOffset,
                Size = entry.StoredSize,
                HeaderSize = headerSize,
                Method = EntryMethod.DownloadFull
            });
        }

        return new VirtualDeltaPlan
        {
            Signature = targetSignature,
            Entries = entryPlans
        };
    }

    /// <summary>
    /// Calculates a delta plan for fresh download (no local container).
    /// </summary>
    public VirtualDeltaPlan CalculateFullDownload(VirtualSignatureFile targetSignature)
    {
        return Calculate(targetSignature, null);
    }

    private DeltaPlan CalculateChunkPlan(EntrySignature entry, IChunkSource localSource)
    {
        var actions = new List<DeltaAction>(entry.Chunks!.Count);

        foreach (var chunk in entry.Chunks!)
        {
            var hash = new Hash256(chunk.Hash);

            if (localSource.TryGetChunk(hash, out var location))
            {
                actions.Add(new CopyLocal(chunk.Offset, chunk.Length, location, hash));
            }
            else
            {
                actions.Add(new DownloadRemote(chunk.Offset, chunk.Length, hash));
            }
        }

        // Create a temporary signature for the chunk plan
        var tempSignature = new SignatureFile
        {
            AlgorithmId = "fastcdc-v1",
            MinSize = 0,
            AverageSize = 0,
            MaxSize = 0,
            Chunks = entry.Chunks
        };

        return new DeltaPlan(actions, tempSignature);
    }
}
