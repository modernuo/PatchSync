using System.Security.Cryptography;
using PatchSync.Common.Hashing;
using PatchSync.Common.Storage;
using PatchSync.SDK.Delta;

namespace PatchSync.SDK.Assembly;

/// <summary>
/// Assembles a file from local chunks and remote downloads.
/// </summary>
public sealed class FileAssembler
{
    private readonly IStorageProvider _storage;
    private readonly AssemblyOptions _options;

    public FileAssembler(IStorageProvider storage, AssemblyOptions? options = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _options = options ?? new AssemblyOptions();
    }

    /// <summary>
    /// Assembles a file according to the delta plan.
    /// </summary>
    /// <param name="targetPath">Path where the assembled file will be written.</param>
    /// <param name="remotePath">Path to the remote file for byte-range requests.</param>
    /// <param name="plan">The delta plan describing chunks to copy/download.</param>
    /// <param name="expectedHash">Expected SHA256 hash of the final file (lowercase hex).</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task AssembleAsync(
        string targetPath,
        string remotePath,
        DeltaPlan plan,
        string expectedHash,
        IProgress<AssemblyProgress>? progress = null,
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
                bufferSize: 81920,
                FileOptions.Asynchronous);

            // Pre-allocate file size for better performance
            var totalSize = plan.TotalBytes;
            if (totalSize > 0)
            {
                targetStream.SetLength(totalSize);
            }

            var progressState = new AssemblyProgressState(plan, progress);

            // Process actions in order (they're already sorted by target offset)
            foreach (var action in plan.Actions)
            {
                cancellationToken.ThrowIfCancellationRequested();

                switch (action)
                {
                    case CopyLocal copyLocal:
                        await CopyLocalChunkAsync(targetStream, copyLocal, progressState, cancellationToken);
                        break;

                    case DownloadRemote downloadRemote:
                        await DownloadChunkAsync(targetStream, remotePath, downloadRemote, progressState, cancellationToken);
                        break;
                }
            }

            // Verify final hash
            targetStream.Position = 0;
            var actualHash = await ComputeHashAsync(targetStream, cancellationToken);

            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Hash mismatch after assembly. Expected: {expectedHash}, Actual: {actualHash}");
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
            // Clean up temp file on failure
            try { File.Delete(tempPath); } catch { }
            throw;
        }
    }

    private async Task CopyLocalChunkAsync(
        FileStream target,
        CopyLocal action,
        AssemblyProgressState progress,
        CancellationToken cancellationToken)
    {
        target.Position = action.TargetOffset;

        await using var sourceStream = new FileStream(
            action.Source.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        sourceStream.Position = action.Source.Offset;

        var buffer = new byte[Math.Min(action.Length, 81920)];
        int remaining = action.Length;

        while (remaining > 0)
        {
            int toRead = Math.Min(remaining, buffer.Length);
            int bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);

            if (bytesRead == 0)
                throw new EndOfStreamException($"Unexpected end of source file: {action.Source.FilePath}");

            await target.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            remaining -= bytesRead;
            progress.AddBytesCopied(bytesRead);
        }
    }

    private async Task DownloadChunkAsync(
        FileStream target,
        string remotePath,
        DownloadRemote action,
        AssemblyProgressState progress,
        CancellationToken cancellationToken)
    {
        target.Position = action.TargetOffset;

        await using var remoteStream = await _storage.GetRangeAsync(
            remotePath, action.TargetOffset, action.Length, cancellationToken);

        var buffer = new byte[Math.Min(action.Length, 81920)];
        int remaining = action.Length;

        while (remaining > 0)
        {
            int toRead = Math.Min(remaining, buffer.Length);
            int bytesRead = await remoteStream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);

            if (bytesRead == 0)
                throw new EndOfStreamException($"Unexpected end of remote stream at offset {action.TargetOffset}");

            await target.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            remaining -= bytesRead;
            progress.AddBytesDownloaded(bytesRead);
        }
    }

    private static async Task<string> ComputeHashAsync(Stream stream, CancellationToken cancellationToken)
    {
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class AssemblyProgressState
    {
        private readonly DeltaPlan _plan;
        private readonly IProgress<AssemblyProgress>? _progress;
        private readonly System.Diagnostics.Stopwatch _stopwatch;
        private long _bytesDownloaded;
        private long _bytesCopied;

        public AssemblyProgressState(DeltaPlan plan, IProgress<AssemblyProgress>? progress)
        {
            _plan = plan;
            _progress = progress;
            _stopwatch = System.Diagnostics.Stopwatch.StartNew();
        }

        public void AddBytesDownloaded(int bytes)
        {
            _bytesDownloaded += bytes;
            Report();
        }

        public void AddBytesCopied(int bytes)
        {
            _bytesCopied += bytes;
            Report();
        }

        public void ReportComplete()
        {
            _progress?.Report(new AssemblyProgress(
                Phase: AssemblyPhase.Complete,
                BytesComplete: _plan.TotalBytes,
                BytesTotal: _plan.TotalBytes,
                BytesDownloaded: _bytesDownloaded,
                BytesCopied: _bytesCopied,
                BytesPerSecond: _plan.TotalBytes / Math.Max(0.001, _stopwatch.Elapsed.TotalSeconds)));
        }

        private void Report()
        {
            if (_progress == null) return;

            var complete = _bytesDownloaded + _bytesCopied;
            var elapsed = _stopwatch.Elapsed.TotalSeconds;
            var speed = elapsed > 0.001 ? complete / elapsed : 0;

            _progress.Report(new AssemblyProgress(
                Phase: AssemblyPhase.Assembling,
                BytesComplete: complete,
                BytesTotal: _plan.TotalBytes,
                BytesDownloaded: _bytesDownloaded,
                BytesCopied: _bytesCopied,
                BytesPerSecond: speed));
        }
    }
}

/// <summary>
/// Options for file assembly.
/// </summary>
public sealed class AssemblyOptions
{
    /// <summary>
    /// Maximum concurrent downloads (not yet implemented - reserved for future).
    /// </summary>
    public int MaxConcurrency { get; init; } = 4;

    /// <summary>
    /// Whether to verify chunk hashes during assembly.
    /// </summary>
    public bool VerifyChunks { get; init; } = false;

    /// <summary>
    /// Buffer size for copy operations.
    /// </summary>
    public int BufferSize { get; init; } = 81920;
}

/// <summary>
/// Progress information during file assembly.
/// </summary>
public readonly record struct AssemblyProgress(
    AssemblyPhase Phase,
    long BytesComplete,
    long BytesTotal,
    long BytesDownloaded,
    long BytesCopied,
    double BytesPerSecond)
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
/// Current phase of assembly.
/// </summary>
public enum AssemblyPhase
{
    /// <summary>Preparing to assemble.</summary>
    Preparing,
    /// <summary>Actively copying/downloading chunks.</summary>
    Assembling,
    /// <summary>Verifying final hash.</summary>
    Verifying,
    /// <summary>Assembly complete.</summary>
    Complete
}
