using System.Buffers.Binary;
using System.Security.Cryptography;
using PatchSync.Common.Signatures;
using PatchSync.Common.Storage;
using PatchSync.SDK.Containers;
using PatchSync.SDK.Delta;
using PatchSync.SDK.Storage;

namespace PatchSync.SDK.Assembly;

/// <summary>
/// Assembles a container file from local entries/chunks and remote downloads.
/// </summary>
public sealed class ContainerAssembler
{
    private readonly IStorageProvider _storage;
    private readonly AssemblyOptions _options;
    private readonly ContainerRegistry _containerRegistry;
    private readonly RangeDownloader _rangeDownloader;

    public ContainerAssembler(
        IStorageProvider storage,
        AssemblyOptions? options = null,
        ContainerRegistry? containerRegistry = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _options = options ?? new AssemblyOptions();
        _containerRegistry = containerRegistry ?? ContainerRegistry.Default;
        _rangeDownloader = new RangeDownloader(storage, new RangeDownloaderOptions
        {
            MaxConcurrency = _options.MaxConcurrency,
            BufferSize = _options.BufferSize,
            MaxCoalesceGap = _options.MaxCoalesceGap
        });
    }

    /// <summary>
    /// Assembles a container according to the virtual delta plan.
    /// </summary>
    /// <param name="targetPath">Path where the assembled container will be written.</param>
    /// <param name="remotePath">Path to the remote container for byte-range requests.</param>
    /// <param name="plan">The virtual delta plan describing entries to copy/download.</param>
    /// <param name="localContainerPath">Path to local container (for copying entries).</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task AssembleAsync(
        string targetPath,
        string remotePath,
        VirtualDeltaPlan plan,
        string? localContainerPath,
        IProgress<ContainerAssemblyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var tempPath = targetPath + ".pstmp";

        try
        {
            // Ensure directory exists
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            await using var targetStream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: _options.BufferSize,
                FileOptions.Asynchronous);

            // Pre-allocate file size
            var layout = plan.Signature.Layout;
            if (layout.TotalSize > 0)
            {
                targetStream.SetLength(layout.TotalSize);
            }

            // Write the structural metadata from HeaderTemplate
            WriteContainerStructure(targetStream, plan.Signature.ContainerFormat, layout);

            var progressState = new ContainerAssemblyProgressState(plan, progress);

            // Open local container if available
            FileStream? localContainer = null;
            if (localContainerPath != null && File.Exists(localContainerPath))
            {
                localContainer = new FileStream(
                    localContainerPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: _options.BufferSize,
                    FileOptions.Asynchronous | FileOptions.RandomAccess);
            }

            try
            {
                // Phase 1: Batch download all remote ranges
                var downloadRanges = GetAllDownloadRanges(plan);
                if (downloadRanges.Count > 0)
                {
                    progressState.SetPhase(ContainerAssemblyPhase.Downloading);

                    var downloadProgress = new Progress<RangeDownloadProgress>(p =>
                    {
                        progressState.SetDownloadProgress(p.BytesDownloaded, p.BytesTotal, p.BytesDelta);
                    });

                    await _rangeDownloader.DownloadRangesToStreamAsync(
                        remotePath, downloadRanges, targetStream, downloadProgress, cancellationToken);
                }

                // Phase 2: Copy local entries/chunks
                progressState.SetPhase(ContainerAssemblyPhase.Assembling);

                foreach (var entry in plan.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progressState.SetCurrentEntry(entry.EntryId);

                    switch (entry.Method)
                    {
                        case EntryMethod.CopyLocal when localContainer != null && entry.LocalSource.HasValue:
                            await CopyLocalEntryAsync(
                                targetStream, localContainer, entry,
                                progressState, cancellationToken);
                            break;

                        case EntryMethod.DeltaChunks when entry.ChunkPlan != null && localContainer != null:
                            // Only copy the local chunks (downloads already done in batch)
                            await CopyLocalChunksAsync(
                                targetStream, localContainer, entry,
                                progressState, cancellationToken);
                            break;

                        // DownloadFull entries are already written by batch download
                    }

                    progressState.EntryComplete();
                }
            }
            finally
            {
                if (localContainer != null)
                    await localContainer.DisposeAsync();
            }

            // Verify final hash (unless skipped for debugging)
            if (!_options.SkipVerification)
            {
                progressState.SetPhase(ContainerAssemblyPhase.Verifying);
                targetStream.Position = 0;
                var actualHash = await ComputeHashAsync(targetStream, cancellationToken);
                var expectedHash = Convert.ToHexString(plan.Signature.ContainerHash).ToLowerInvariant();

                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Container hash mismatch after assembly. Expected: {expectedHash}, Actual: {actualHash}");
                }
            }

            // Close stream before rename
            await targetStream.DisposeAsync();

            // Atomic rename
            if (File.Exists(targetPath))
                File.Delete(targetPath);
            File.Move(tempPath, targetPath);

            progressState.ReportComplete();
        }
        catch
        {
            // Clean up temp file on failure (unless preserving for debug)
            if (!_options.PreserveTempOnFailure)
            {
                try { File.Delete(tempPath); } catch { }
            }
            throw;
        }
    }

    /// <summary>
    /// Collects all byte ranges that need to be downloaded from the remote container.
    /// </summary>
    private IReadOnlyList<ByteRange> GetAllDownloadRanges(VirtualDeltaPlan plan)
    {
        var ranges = new List<ByteRange>();

        foreach (var entry in plan.Entries)
        {
            switch (entry.Method)
            {
                case EntryMethod.DownloadFull:
                    // Download header + data together
                    ranges.Add(new ByteRange(entry.HeaderOffset, entry.TotalSize));
                    break;

                case EntryMethod.CopyLocal:
                    // Download only the header from remote (data is copied from local)
                    // Headers contain position-specific data that differs between source and target
                    if (entry.HeaderSize > 0)
                    {
                        ranges.Add(new ByteRange(entry.HeaderOffset, entry.HeaderSize));
                    }
                    break;

                case EntryMethod.DeltaChunks when entry.ChunkPlan != null:
                    // Download entry header
                    if (entry.HeaderSize > 0)
                    {
                        ranges.Add(new ByteRange(entry.HeaderOffset, entry.HeaderSize));
                    }

                    // Download remote chunks (offsets relative to entry, convert to absolute)
                    foreach (var action in entry.ChunkPlan.Actions.OfType<DownloadRemote>())
                    {
                        var absoluteOffset = entry.TargetOffset + action.TargetOffset;
                        ranges.Add(new ByteRange(absoluteOffset, action.Length));
                    }
                    break;
            }
        }

        // Add inter-entry gaps (structural data between entries)
        // UOP files may have alignment padding, block tables, or other metadata between entries
        var sortedEntries = plan.Entries.OrderBy(e => e.HeaderOffset).ToList();
        for (int i = 0; i < sortedEntries.Count - 1; i++)
        {
            var current = sortedEntries[i];
            var next = sortedEntries[i + 1];

            var currentEnd = current.HeaderOffset + current.TotalSize;
            var nextStart = next.HeaderOffset;

            if (nextStart > currentEnd)
            {
                // There's a gap between entries - download it
                ranges.Add(new ByteRange(currentEnd, nextStart - currentEnd));
            }
        }

        // Add gap after last entry (if file extends beyond last entry)
        if (sortedEntries.Count > 0)
        {
            var lastEntry = sortedEntries[^1];
            var lastEntryEnd = lastEntry.HeaderOffset + lastEntry.TotalSize;
            var totalSize = plan.Signature.TotalSize;

            if (totalSize > lastEntryEnd)
            {
                ranges.Add(new ByteRange(lastEntryEnd, totalSize - lastEntryEnd));
            }
        }

        // Coalesce ranges with configured gap tolerance
        return RangeDownloader.CoalesceRanges(ranges, _options.MaxCoalesceGap);
    }

    private async Task CopyLocalEntryAsync(
        FileStream target,
        FileStream localContainer,
        EntryPlan entry,
        ContainerAssemblyProgressState progress,
        CancellationToken cancellationToken)
    {
        var source = entry.LocalSource!.Value;

        // Copy only the DATA portion from local (header is downloaded from remote)
        // Headers contain position-specific data that differs between source and target
        var dataSize = entry.Size; // Just the data, not including header

        target.Position = entry.TargetOffset; // Start at data position (after header)
        localContainer.Position = source.Offset; // source.Offset points to data, not header

        var buffer = new byte[Math.Min(dataSize, _options.BufferSize)];
        int remaining = dataSize;

        while (remaining > 0)
        {
            int toRead = Math.Min(remaining, buffer.Length);
            int bytesRead = await localContainer.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);

            if (bytesRead == 0)
                throw new EndOfStreamException($"Unexpected end of local container at entry {entry.EntryId}");

            await target.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            remaining -= bytesRead;
            progress.AddBytesCopied(bytesRead);
        }
    }

    private async Task CopyLocalChunksAsync(
        FileStream target,
        FileStream localContainer,
        EntryPlan entry,
        ContainerAssemblyProgressState progress,
        CancellationToken cancellationToken)
    {
        // Only copy local chunks - remote chunks already downloaded in batch
        var localChunks = entry.ChunkPlan!.Actions.OfType<CopyLocal>();

        foreach (var chunk in localChunks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Calculate absolute target offset
            var absoluteOffset = entry.TargetOffset + chunk.TargetOffset;
            target.Position = absoluteOffset;
            localContainer.Position = chunk.Source.Offset;

            var buffer = new byte[Math.Min(chunk.Length, _options.BufferSize)];
            int remaining = chunk.Length;

            while (remaining > 0)
            {
                int toRead = Math.Min(remaining, buffer.Length);
                int bytesRead = await localContainer.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);

                if (bytesRead == 0)
                    throw new EndOfStreamException($"Unexpected end of local container while copying chunk for entry {entry.EntryId}");

                await target.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                remaining -= bytesRead;
                progress.AddBytesCopied(bytesRead);
            }
        }
    }

    private static async Task<string> ComputeHashAsync(Stream stream, CancellationToken cancellationToken)
    {
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Writes the container structure from the compact HeaderTemplate format.
    /// </summary>
    private void WriteContainerStructure(FileStream output, string containerFormat, ContainerLayout layout)
    {
        using var templateStream = new MemoryStream(layout.HeaderTemplate);

        // UOP format: [FileHeader: 28][PreEntryGapLen: 4][PreEntryGap: N][BlockCount: 4][BlockOffset: 8 + BlockData: N]...
        if (containerFormat == "uop-v1")
        {
            const int UopHeaderSize = 28;
            const int UopEntrySize = 34;

            // Read and write file header
            var fileHeader = new byte[UopHeaderSize];
            templateStream.ReadExactly(fileHeader);
            output.Position = 0;
            output.Write(fileHeader);

            // Read and write pre-entry gap
            Span<byte> gapLenBuffer = stackalloc byte[4];
            templateStream.ReadExactly(gapLenBuffer);
            int preEntryGapLen = BinaryPrimitives.ReadInt32LittleEndian(gapLenBuffer);

            if (preEntryGapLen > 0)
            {
                var gapData = new byte[preEntryGapLen];
                templateStream.ReadExactly(gapData);
                output.Position = UopHeaderSize;
                output.Write(gapData);
            }

            // Read block count
            Span<byte> countBuffer = stackalloc byte[4];
            templateStream.ReadExactly(countBuffer);
            int blockCount = BinaryPrimitives.ReadInt32LittleEndian(countBuffer);

            // Write each block at its original offset
            Span<byte> offsetBuffer = stackalloc byte[8];
            Span<byte> blockHeader = stackalloc byte[12];

            for (int i = 0; i < blockCount; i++)
            {
                templateStream.ReadExactly(offsetBuffer);
                long blockOffset = BinaryPrimitives.ReadInt64LittleEndian(offsetBuffer);

                // Read block header to determine entry count
                templateStream.ReadExactly(blockHeader);
                int entryCount = BinaryPrimitives.ReadInt32LittleEndian(blockHeader[0..4]);

                // Write block header at its offset
                output.Position = blockOffset;
                output.Write(blockHeader);

                // Read and write entry metadata
                var entryData = new byte[entryCount * UopEntrySize];
                templateStream.ReadExactly(entryData);
                output.Write(entryData);
            }

            // Entry headers are now downloaded/copied with entry data, not stored in layout
        }
        else
        {
            // Generic fallback: write HeaderTemplate as-is
            output.Position = 0;
            output.Write(layout.HeaderTemplate);
        }
    }

    private sealed class ContainerAssemblyProgressState
    {
        private readonly VirtualDeltaPlan _plan;
        private readonly IProgress<ContainerAssemblyProgress>? _progress;
        private readonly System.Diagnostics.Stopwatch _stopwatch;
        private long _bytesDownloaded;
        private long _bytesCopied;
        private int _entriesComplete;
        private string? _currentEntry;
        private ContainerAssemblyPhase _phase;

        public ContainerAssemblyProgressState(VirtualDeltaPlan plan, IProgress<ContainerAssemblyProgress>? progress)
        {
            _plan = plan;
            _progress = progress;
            _stopwatch = System.Diagnostics.Stopwatch.StartNew();
            _phase = ContainerAssemblyPhase.Assembling;
        }

        public void SetCurrentEntry(string entryId)
        {
            _currentEntry = entryId;
        }

        public void SetPhase(ContainerAssemblyPhase phase)
        {
            _phase = phase;
            Report();
        }

        public void EntryComplete()
        {
            _entriesComplete++;
            Report();
        }

        public void SetDownloadProgress(long totalDownloaded, long bytesTotal, long delta)
        {
            _bytesDownloaded = totalDownloaded;
            Report();
        }

        public void AddBytesCopied(int bytes)
        {
            _bytesCopied += bytes;
            Report();
        }

        public void ReportComplete()
        {
            _phase = ContainerAssemblyPhase.Complete;
            Report();
        }

        private void Report()
        {
            if (_progress == null) return;

            var complete = _bytesDownloaded + _bytesCopied;
            var elapsed = _stopwatch.Elapsed.TotalSeconds;
            var speed = elapsed > 0.001 ? complete / elapsed : 0;

            _progress.Report(new ContainerAssemblyProgress(
                Phase: _phase,
                BytesComplete: complete,
                BytesTotal: _plan.TotalBytes,
                BytesDownloaded: _bytesDownloaded,
                BytesCopied: _bytesCopied,
                BytesPerSecond: speed,
                EntriesComplete: _entriesComplete,
                EntriesTotal: _plan.Entries.Count,
                CurrentEntry: _currentEntry));
        }
    }
}

/// <summary>
/// Progress information during container assembly.
/// </summary>
public readonly record struct ContainerAssemblyProgress(
    ContainerAssemblyPhase Phase,
    long BytesComplete,
    long BytesTotal,
    long BytesDownloaded,
    long BytesCopied,
    double BytesPerSecond,
    int EntriesComplete,
    int EntriesTotal,
    string? CurrentEntry)
{
    /// <summary>
    /// Completion percentage (0.0 to 1.0).
    /// </summary>
    public double Percentage => BytesTotal > 0 ? (double)BytesComplete / BytesTotal : 0;

    /// <summary>
    /// Estimated time remaining.
    /// </summary>
    public TimeSpan EstimatedRemaining =>
        BytesPerSecond > 0 && BytesComplete < BytesTotal
            ? TimeSpan.FromSeconds((BytesTotal - BytesComplete) / BytesPerSecond)
            : TimeSpan.Zero;
}

/// <summary>
/// Current phase of container assembly.
/// </summary>
public enum ContainerAssemblyPhase
{
    /// <summary>Preparing to assemble.</summary>
    Preparing,
    /// <summary>Downloading remote ranges in batch.</summary>
    Downloading,
    /// <summary>Copying local entries/chunks.</summary>
    Assembling,
    /// <summary>Verifying final hash.</summary>
    Verifying,
    /// <summary>Assembly complete.</summary>
    Complete
}
