using System.Security.Cryptography;
using PatchSync.Common.Chunking;
using PatchSync.Common.Signatures;
using PatchSync.SDK.Containers;

namespace PatchSync.SDK.Signatures;

/// <summary>
/// Options for virtual signature generation.
/// </summary>
public sealed class VirtualSignatureGeneratorOptions
{
    /// <summary>
    /// Minimum entry size for CDC sub-chunking.
    /// Entries smaller than this use entry-level hashing only.
    /// Default: 64KB
    /// </summary>
    public int CdcThreshold { get; init; } = 65536;

    /// <summary>
    /// Whether to generate CDC chunks for large uncompressed entries.
    /// If false, all entries use entry-level hashing only.
    /// </summary>
    public bool EnableCdc { get; init; } = true;

    /// <summary>
    /// Maximum allowed ratio of estimated VSIG overhead to file size.
    /// If the estimated VSIG would exceed this ratio of the file size,
    /// virtual delta is skipped and the file should use standard CDC instead.
    /// Default: 0.20 (20%)
    /// </summary>
    public double MaxOverheadRatio { get; init; } = 0.20;

    /// <summary>
    /// Estimated per-entry overhead in bytes for VSIG file.
    /// Used to calculate whether virtual delta is efficient.
    /// Default: 100 bytes (entry metadata + layout metadata)
    /// </summary>
    public int EstimatedPerEntryOverhead { get; init; } = 100;
}

/// <summary>
/// Result of checking whether virtual delta is efficient for a container.
/// </summary>
public readonly record struct VirtualDeltaEfficiencyResult(
    bool IsEfficient,
    int EntryCount,
    long FileSize,
    long EstimatedOverhead,
    double OverheadRatio,
    string? SkipReason)
{
    public static VirtualDeltaEfficiencyResult Efficient(int entryCount, long fileSize, long estimatedOverhead) =>
        new(true, entryCount, fileSize, estimatedOverhead, fileSize > 0 ? (double)estimatedOverhead / fileSize : 0, null);

    public static VirtualDeltaEfficiencyResult Inefficient(int entryCount, long fileSize, long estimatedOverhead, string reason) =>
        new(false, entryCount, fileSize, estimatedOverhead, fileSize > 0 ? (double)estimatedOverhead / fileSize : 0, reason);
}

/// <summary>
/// Progress information during virtual signature generation.
/// </summary>
public readonly record struct VirtualSignatureProgress(
    string? CurrentEntry,
    int EntriesProcessed,
    int TotalEntries,
    int ChunksGenerated)
{
    public double Percentage => TotalEntries > 0 ? (double)EntriesProcessed / TotalEntries : 0;
}

/// <summary>
/// Generates virtual signature files for container formats.
/// </summary>
public sealed class VirtualSignatureGenerator
{
    private readonly IChunker _chunker;
    private readonly ChunkingOptions _chunkingOptions;

    public VirtualSignatureGenerator(IChunker chunker, ChunkingOptions? chunkingOptions = null)
    {
        _chunker = chunker ?? throw new ArgumentNullException(nameof(chunker));
        _chunkingOptions = chunkingOptions ?? ChunkingOptions.Default;
    }

    /// <summary>
    /// Checks whether virtual delta would be efficient for a container file.
    /// </summary>
    /// <param name="containerPath">Path to the container file.</param>
    /// <param name="handler">Container handler for this format.</param>
    /// <param name="options">Generation options (for overhead thresholds).</param>
    /// <returns>Result indicating whether virtual delta is efficient and why.</returns>
    public VirtualDeltaEfficiencyResult CheckEfficiency(
        string containerPath,
        IContainerHandler handler,
        VirtualSignatureGeneratorOptions? options = null)
    {
        options ??= new VirtualSignatureGeneratorOptions();

        using var stream = new FileStream(
            containerPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920);

        var fileSize = stream.Length;
        var containerInfo = handler.Parse(stream);
        var entryCount = containerInfo.Entries.Count;

        // Estimate VSIG overhead:
        // - Per-entry overhead (entry metadata + layout entry)
        // - HeaderTemplate (we can't know exactly without extracting, estimate as ~1% of file for UOP)
        var perEntryOverhead = (long)entryCount * options.EstimatedPerEntryOverhead;
        var headerTemplateEstimate = (long)(fileSize * 0.02); // ~2% for block tables
        var estimatedOverhead = perEntryOverhead + headerTemplateEstimate;

        var overheadRatio = fileSize > 0 ? (double)estimatedOverhead / fileSize : 0;

        if (overheadRatio > options.MaxOverheadRatio)
        {
            return VirtualDeltaEfficiencyResult.Inefficient(
                entryCount, fileSize, estimatedOverhead,
                $"Estimated VSIG overhead ({overheadRatio:P1}) exceeds threshold ({options.MaxOverheadRatio:P0}). " +
                $"Container has {entryCount:N0} entries. Use standard CDC chunking instead.");
        }

        return VirtualDeltaEfficiencyResult.Efficient(entryCount, fileSize, estimatedOverhead);
    }

    /// <summary>
    /// Tries to generate a virtual signature for a container file.
    /// Returns null if virtual delta would be inefficient for this file.
    /// </summary>
    /// <param name="containerPath">Path to the container file.</param>
    /// <param name="handler">Container handler for this format.</param>
    /// <param name="options">Generation options.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="efficiencyResult">Result of the efficiency check.</param>
    /// <returns>The generated virtual signature, or null if inefficient.</returns>
    public VirtualSignatureFile? TryGenerateSignature(
        string containerPath,
        IContainerHandler handler,
        VirtualSignatureGeneratorOptions? options,
        IProgress<VirtualSignatureProgress>? progress,
        out VirtualDeltaEfficiencyResult efficiencyResult)
    {
        options ??= new VirtualSignatureGeneratorOptions();

        efficiencyResult = CheckEfficiency(containerPath, handler, options);
        if (!efficiencyResult.IsEfficient)
            return null;

        return GenerateSignature(containerPath, handler, options, progress);
    }

    /// <summary>
    /// Generates a virtual signature for a container file.
    /// Note: Does not check efficiency. Use TryGenerateSignature or CheckEfficiency first
    /// for containers with potentially many small entries.
    /// </summary>
    /// <param name="containerPath">Path to the container file.</param>
    /// <param name="handler">Container handler for this format.</param>
    /// <param name="options">Generation options.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <returns>The generated virtual signature.</returns>
    public VirtualSignatureFile GenerateSignature(
        string containerPath,
        IContainerHandler handler,
        VirtualSignatureGeneratorOptions? options = null,
        IProgress<VirtualSignatureProgress>? progress = null)
    {
        options ??= new VirtualSignatureGeneratorOptions();

        using var stream = new FileStream(
            containerPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.SequentialScan);

        // Parse container
        var containerInfo = handler.Parse(stream);

        var entrySignatures = new List<EntrySignature>(containerInfo.Entries.Count);
        int chunksGenerated = 0;

        // Process each entry
        for (int i = 0; i < containerInfo.Entries.Count; i++)
        {
            var entry = containerInfo.Entries[i];

            progress?.Report(new VirtualSignatureProgress(
                entry.EntryId,
                i,
                containerInfo.Entries.Count,
                chunksGenerated));

            // Compute stored hash
            stream.Position = entry.Offset;
            var storedHash = ComputeHash(stream, entry.StoredSize);

            // Determine if we should CDC this entry
            IReadOnlyList<SignatureChunk>? chunks = null;
            if (options.EnableCdc && entry.IsLargeUncompressed && entry.StoredSize >= options.CdcThreshold)
            {
                stream.Position = entry.Offset;
                var boundedStream = new BoundedStream(stream, entry.StoredSize);

                chunks = _chunker.Chunk(boundedStream, _chunkingOptions)
                    .Select(c => new SignatureChunk(c.Offset, c.Length, c.Hash))
                    .ToList();

                chunksGenerated += chunks.Count;
            }

            entrySignatures.Add(new EntrySignature
            {
                EntryId = entry.EntryId,
                TargetOffset = entry.Offset,
                StoredSize = entry.StoredSize,
                Compression = entry.Compression,
                StoredHash = storedHash,
                Chunks = chunks
            });
        }

        // Extract layout for reconstruction
        var layout = handler.ExtractLayout(stream, containerInfo);

        // Compute container hash
        stream.Position = 0;
        var containerHash = ComputeFullHash(stream);

        progress?.Report(new VirtualSignatureProgress(
            null,
            containerInfo.Entries.Count,
            containerInfo.Entries.Count,
            chunksGenerated));

        return new VirtualSignatureFile
        {
            ContainerFormat = handler.FormatId,
            AlgorithmId = _chunker.AlgorithmId,
            MinSize = _chunkingOptions.MinSize,
            AverageSize = _chunkingOptions.AverageSize,
            MaxSize = _chunkingOptions.MaxSize,
            TotalSize = stream.Length,
            ContainerHash = containerHash,
            Layout = layout,
            Entries = entrySignatures
        };
    }

    /// <summary>
    /// Generates a virtual signature and writes it to a file.
    /// </summary>
    public void GenerateSignatureFile(
        string containerPath,
        IContainerHandler handler,
        string? outputPath = null,
        VirtualSignatureGeneratorOptions? options = null,
        IProgress<VirtualSignatureProgress>? progress = null)
    {
        outputPath ??= containerPath + ".vsig";

        var signature = GenerateSignature(containerPath, handler, options, progress);

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var output = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);

        VirtualSignatureFormat.Write(output, signature);
    }

    /// <summary>
    /// Generates a virtual signature and writes it to a stream.
    /// </summary>
    public void GenerateSignature(
        string containerPath,
        IContainerHandler handler,
        Stream output,
        VirtualSignatureGeneratorOptions? options = null,
        IProgress<VirtualSignatureProgress>? progress = null)
    {
        var signature = GenerateSignature(containerPath, handler, options, progress);
        VirtualSignatureFormat.Write(output, signature);
    }

    private static byte[] ComputeHash(Stream stream, int length)
    {
        using var sha256 = SHA256.Create();
        var buffer = new byte[Math.Min(length, 81920)];
        int remaining = length;

        while (remaining > 0)
        {
            int toRead = Math.Min(remaining, buffer.Length);
            int bytesRead = stream.Read(buffer, 0, toRead);
            if (bytesRead == 0)
                throw new EndOfStreamException("Unexpected end of stream while hashing");

            sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
            remaining -= bytesRead;
        }

        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return sha256.Hash!;
    }

    private static byte[] ComputeFullHash(Stream stream)
    {
        using var sha256 = SHA256.Create();
        var buffer = new byte[81920];
        int bytesRead;

        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
        }

        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return sha256.Hash!;
    }

    /// <summary>
    /// A stream that limits reads to a specified length.
    /// </summary>
    private sealed class BoundedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _length;
        private long _position;

        public BoundedStream(Stream inner, long length)
        {
            _inner = inner;
            _length = length;
            _position = 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            long remaining = _length - _position;
            if (remaining <= 0)
                return 0;

            int toRead = (int)Math.Min(count, remaining);
            int bytesRead = _inner.Read(buffer, offset, toRead);
            _position += bytesRead;
            return bytesRead;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
