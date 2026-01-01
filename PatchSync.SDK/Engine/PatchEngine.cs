using System.Collections.Concurrent;
using System.Security.Cryptography;
using PatchSync.Common.Chunking;
using PatchSync.Common.Manifest;
using PatchSync.Common.Signatures;
using PatchSync.Common.Storage;
using PatchSync.SDK.Assembly;
using PatchSync.SDK.Containers;
using PatchSync.SDK.Delta;
using PatchSync.SDK.Sources;

namespace PatchSync.SDK.Engine;

/// <summary>
/// Phased patch engine that safely handles delta patching with proper isolation.
///
/// Phases:
/// 1. Plan: Download signatures, calculate delta plans, determine download strategy
/// 2. Assemble: Download/copy chunks to temp files (parallelizable, reads from originals only)
/// 3. Commit: Atomically swap temp files to final locations
/// 4. Verify: Hash-verify all files, fallback to full download for failures
/// </summary>
public sealed class PatchEngine : IDisposable
{
    private readonly IStorageProvider _storage;
    private readonly IChunkerRegistry _chunkerRegistry;
    private readonly ContainerRegistry _containerRegistry;
    private readonly PatchEngineOptions _options;
    private readonly FileAssembler _assembler;
    private readonly ContainerAssembler _containerAssembler;
    private readonly DownloadStrategySelector _strategySelector;

    public PatchEngine(
        IStorageProvider storage,
        IChunkerRegistry? chunkerRegistry = null,
        ContainerRegistry? containerRegistry = null,
        PatchEngineOptions? options = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _chunkerRegistry = chunkerRegistry ?? ChunkerRegistry.Default;
        _containerRegistry = containerRegistry ?? ContainerRegistry.Default;
        _options = options ?? new PatchEngineOptions();
        _assembler = new FileAssembler(storage, _options.AssemblyOptions);
        _containerAssembler = new ContainerAssembler(storage, _options.AssemblyOptions, _containerRegistry);
        _strategySelector = new DownloadStrategySelector(_options.DeltaThreshold);
    }

    /// <summary>
    /// Executes a full patch operation using the phased approach.
    /// </summary>
    public async Task<PatchResult> PatchAsync(
        string localPath,
        GameManifest manifest,
        IProgress<PatchEngineProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new PatchResult();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Get chunker
        var chunker = GetChunker(manifest);

        // Phase 1: Plan
        progress?.Report(new PatchEngineProgress(PatchEnginePhase.Planning, 0, 0, 0, 0, 0, 0, "Calculating update plan..."));
        var plan = await PlanAsync(localPath, manifest, chunker, progress, cancellationToken);
        result.FilesPlanned = plan.FilesToProcess.Count;
        result.BytesToDownload = plan.TotalBytesToDownload;

        if (plan.FilesToProcess.Count == 0 && plan.FilesToDelete.Count == 0)
        {
            result.Success = true;
            result.Message = "Already up to date";
            return result;
        }

        var totalBytesToProcess = plan.TotalBytesToDownload + plan.TotalBytesToCopy;

        // Phase 2: Assemble (parallel)
        progress?.Report(new PatchEngineProgress(PatchEnginePhase.Assembling, 0, plan.FilesToProcess.Count, 0, totalBytesToProcess, 0, 0, "Downloading and assembling files..."));
        var assemblyResults = await AssembleAsync(localPath, plan, progress, cancellationToken);
        result.FilesAssembled = assemblyResults.Count(r => r.Success);
        result.BytesDownloaded = plan.TotalBytesToDownload;
        result.BytesCopied = plan.TotalBytesToCopy;

        // Phase 3: Commit (atomic swap)
        progress?.Report(new PatchEngineProgress(PatchEnginePhase.Committing, 0, assemblyResults.Count, 0, 0, 0, 0, "Applying changes..."));
        var commitResults = await CommitAsync(localPath, assemblyResults, plan.FilesToDelete, progress, cancellationToken);
        result.FilesCommitted = commitResults.Committed;

        // Phase 4: Verify (with fallback)
        progress?.Report(new PatchEngineProgress(PatchEnginePhase.Verifying, 0, plan.FilesToProcess.Count, 0, 0, 0, 0, "Verifying files..."));
        var verifyResults = await VerifyWithFallbackAsync(localPath, manifest, plan.FilesToProcess, progress, cancellationToken);
        result.FilesVerified = verifyResults.Passed;
        result.FilesFailed = verifyResults.Failed;

        result.Success = verifyResults.Failed == 0;
        result.ElapsedTime = sw.Elapsed;
        result.Message = result.Success
            ? $"Successfully updated {result.FilesCommitted} files"
            : $"Update completed with {result.FilesFailed} verification failures";

        progress?.Report(new PatchEngineProgress(
            PatchEnginePhase.Complete,
            result.FilesVerified, plan.FilesToProcess.Count,
            totalBytesToProcess, totalBytesToProcess,
            plan.TotalBytesToDownload, plan.TotalBytesToCopy,
            result.Message));

        return result;
    }

    #region Phase 1: Plan

    private async Task<PatchPlan> PlanAsync(
        string localPath,
        GameManifest manifest,
        IChunker chunker,
        IProgress<PatchEngineProgress>? progress,
        CancellationToken cancellationToken)
    {
        var filesToProcess = new List<FilePlan>();
        var filesToDelete = new List<string>();

        var files = manifest.Files.ToList();
        var scanned = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var localFilePath = Path.Combine(localPath, file.Path.Replace('/', Path.DirectorySeparatorChar));

            if (file.Strategy == UpdateStrategy.Delete)
            {
                if (File.Exists(localFilePath))
                {
                    filesToDelete.Add(localFilePath);
                }
                continue;
            }

            // CreateOnly: only download if file doesn't exist
            if (file.Strategy == UpdateStrategy.CreateOnly && File.Exists(localFilePath))
            {
                scanned++;
                continue; // File exists, don't overwrite
            }

            // Check if file is up to date
            if (File.Exists(localFilePath))
            {
                var localHash = await ComputeFileHashAsync(localFilePath, cancellationToken);
                if (string.Equals(localHash, file.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    scanned++;
                    continue; // Up to date
                }
            }

            // Need to update this file
            var filePlan = await CreateFilePlanAsync(localPath, file, chunker, manifest, cancellationToken);
            filesToProcess.Add(filePlan);

            scanned++;
            progress?.Report(new PatchEngineProgress(
                PatchEnginePhase.Planning,
                scanned, files.Count,
                0, 0, 0, 0,
                $"Planning: {file.Path}"));
        }

        return new PatchPlan
        {
            FilesToProcess = filesToProcess,
            FilesToDelete = filesToDelete,
            TotalBytesToDownload = filesToProcess.Sum(f => f.BytesToDownload),
            TotalBytesToCopy = filesToProcess.Sum(f => f.BytesToCopy)
        };
    }

    private async Task<FilePlan> CreateFilePlanAsync(
        string localPath,
        ManifestFile file,
        IChunker chunker,
        GameManifest manifest,
        CancellationToken cancellationToken)
    {
        var localFilePath = Path.Combine(localPath, file.Path.Replace('/', Path.DirectorySeparatorChar));
        var tempPath = localFilePath + ".pstmp";

        DeltaPlan? deltaPlan = null;
        VirtualDeltaPlan? virtualDeltaPlan = null;
        IContainerHandler? containerHandler = null;
        DownloadMethod method = DownloadMethod.Full;
        long bytesToDownload = file.Size;
        long bytesToCopy = 0;

        // Handle VirtualDelta strategy for container files
        if (file.Strategy == UpdateStrategy.VirtualDelta &&
            !string.IsNullOrEmpty(file.ContainerFormat) &&
            !string.IsNullOrEmpty(file.VirtualSignatureUrl))
        {
            if (_containerRegistry.TryGetHandler(file.ContainerFormat, out containerHandler) && containerHandler != null)
            {
                try
                {
                    // Download virtual signature
                    await using var vsigStream = await _storage.GetAsync(file.VirtualSignatureUrl, cancellationToken);
                    var virtualSignature = VirtualSignatureFormat.Read(vsigStream);

                    // Build local virtual container source
                    VirtualContainerSource? localSource = null;
                    if (File.Exists(localFilePath))
                    {
                        localSource = VirtualContainerSource.Build(
                            localFilePath, containerHandler, chunker,
                            virtualSignature.GetChunkingOptions());
                    }

                    // Calculate virtual delta
                    var calculator = new VirtualDeltaCalculator();
                    virtualDeltaPlan = calculator.Calculate(virtualSignature, localSource);

                    method = DownloadMethod.VirtualDelta;
                    bytesToDownload = virtualDeltaPlan.BytesToDownload;
                    bytesToCopy = virtualDeltaPlan.BytesToCopy;
                }
                catch
                {
                    // Fall back to full download if virtual delta fails
                    method = DownloadMethod.Full;
                    bytesToDownload = file.Size;
                    virtualDeltaPlan = null;
                    containerHandler = null;
                }
            }
        }
        // Handle regular Delta strategy
        else if (file.Strategy == UpdateStrategy.Delta &&
                 DownloadStrategySelector.ShouldAttemptDelta(
                     File.Exists(localFilePath),
                     File.Exists(localFilePath) ? new FileInfo(localFilePath).Length : 0,
                     file.Size))
        {
            try
            {
                // Download signature
                var signaturePath = file.SignatureUrl ?? $"signatures/{file.Path}.sig";
                await using var sigStream = await _storage.GetAsync(signaturePath, cancellationToken);
                var signature = SignatureFile.Read(sigStream);

                // Build local chunk source
                IChunkSource localSource;
                if (File.Exists(localFilePath))
                {
                    localSource = LocalFileSource.Build(localFilePath, chunker, signature.GetChunkingOptions());
                }
                else
                {
                    localSource = new CompositeChunkSource();
                }

                // Calculate delta
                var calculator = new DeltaCalculator();
                deltaPlan = calculator.Calculate(signature, localSource);

                // Decide strategy
                var decision = _strategySelector.Select(deltaPlan, file.CompressedSize, file.Size);
                method = decision.Method;
                bytesToDownload = decision.EstimatedBytes;
                if (method == DownloadMethod.Delta)
                {
                    bytesToCopy = deltaPlan.BytesToCopy;
                }
            }
            catch
            {
                // Fall back to full download if delta fails
                method = DownloadMethod.Full;
                bytesToDownload = file.Size;
            }
        }
        else if (file.CompressedSize > 0 && file.CompressedSize < file.Size)
        {
            method = DownloadMethod.Compressed;
            bytesToDownload = file.CompressedSize;
        }

        return new FilePlan
        {
            ManifestFile = file,
            LocalPath = localFilePath,
            TempPath = tempPath,
            Method = method,
            DeltaPlan = deltaPlan,
            VirtualDeltaPlan = virtualDeltaPlan,
            ContainerHandler = containerHandler,
            BytesToDownload = bytesToDownload,
            BytesToCopy = bytesToCopy
        };
    }

    #endregion

    #region Phase 2: Assemble

    private async Task<List<AssemblyResult>> AssembleAsync(
        string localPath,
        PatchPlan plan,
        IProgress<PatchEngineProgress>? progress,
        CancellationToken cancellationToken)
    {
        var results = new ConcurrentBag<AssemblyResult>();
        var completedFiles = 0;
        var downloadedBytes = 0L;
        var copiedBytes = 0L;
        var totalBytes = plan.TotalBytesToDownload + plan.TotalBytesToCopy;
        var totalFiles = plan.FilesToProcess.Count;
        var lastReportTime = DateTime.UtcNow;
        var reportLock = new object();

        void ReportProgress(string currentFile, int currentFileNumber)
        {
            var now = DateTime.UtcNow;
            lock (reportLock)
            {
                // Throttle to max 10 reports/sec to avoid flooding
                if ((now - lastReportTime).TotalMilliseconds < 100)
                    return;
                lastReportTime = now;
            }

            var downloaded = Interlocked.Read(ref downloadedBytes);
            var copied = Interlocked.Read(ref copiedBytes);

            progress?.Report(new PatchEngineProgress(
                PatchEnginePhase.Assembling,
                completedFiles, totalFiles,
                downloaded + copied, totalBytes,
                downloaded, copied,
                currentFile));
        }

        await Parallel.ForEachAsync(
            plan.FilesToProcess,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _options.MaxConcurrency,
                CancellationToken = cancellationToken
            },
            async (filePlan, ct) =>
            {
                var fileStart = DateTime.UtcNow;
                var fileName = Path.GetFileName(filePlan.ManifestFile.Path);

                // Create a progress handler that tracks bytes in real-time
                var fileProgress = new Progress<FileAssemblyProgress>(p =>
                {
                    // Add delta bytes atomically
                    if (p.BytesDownloadedDelta > 0)
                        Interlocked.Add(ref downloadedBytes, p.BytesDownloadedDelta);
                    if (p.BytesCopiedDelta > 0)
                        Interlocked.Add(ref copiedBytes, p.BytesCopiedDelta);

                    ReportProgress($"{fileName} ({p.Percentage:P0})", completedFiles);
                });

                try
                {
                    await AssembleFileAsync(filePlan, fileProgress, ct);
                    results.Add(new AssemblyResult(filePlan, true, null, DateTime.UtcNow - fileStart));
                }
                catch (Exception ex)
                {
                    results.Add(new AssemblyResult(filePlan, false, ex.Message, DateTime.UtcNow - fileStart));
                }

                var completed = Interlocked.Increment(ref completedFiles);
                var elapsed = DateTime.UtcNow - fileStart;

                progress?.Report(new PatchEngineProgress(
                    PatchEnginePhase.Assembling,
                    completed, totalFiles,
                    Interlocked.Read(ref downloadedBytes) + Interlocked.Read(ref copiedBytes), totalBytes,
                    Interlocked.Read(ref downloadedBytes), Interlocked.Read(ref copiedBytes),
                    $"Completed: {fileName} ({elapsed.TotalSeconds:F1}s)"));
            });

        return results.ToList();
    }

    private async Task AssembleFileAsync(FilePlan plan, IProgress<FileAssemblyProgress>? fileProgress, CancellationToken cancellationToken)
    {
        // Ensure directory exists
        var dir = Path.GetDirectoryName(plan.TempPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var totalBytes = plan.BytesToDownload + plan.BytesToCopy;
        var lastDownloaded = 0L;
        var lastCopied = 0L;

        switch (plan.Method)
        {
            case DownloadMethod.Delta when plan.DeltaPlan != null:
                // Create progress adapter for file assembler
                var deltaProgress = fileProgress != null
                    ? new Progress<AssemblyProgress>(p =>
                    {
                        var downloadDelta = p.BytesDownloaded - lastDownloaded;
                        var copyDelta = p.BytesCopied - lastCopied;
                        lastDownloaded = p.BytesDownloaded;
                        lastCopied = p.BytesCopied;

                        fileProgress.Report(new FileAssemblyProgress(
                            p.BytesDownloaded + p.BytesCopied,
                            totalBytes,
                            downloadDelta,
                            copyDelta));
                    })
                    : null;

                await _assembler.AssembleAsync(
                    plan.LocalPath,  // Target path (assembler writes to .pstmp internally)
                    plan.ManifestFile.Path,
                    plan.DeltaPlan,
                    plan.ManifestFile.Hash,
                    deltaProgress,
                    cancellationToken);
                break;

            case DownloadMethod.VirtualDelta when plan.VirtualDeltaPlan != null:
                // Create progress adapter for container assembler
                var containerProgress = fileProgress != null
                    ? new Progress<ContainerAssemblyProgress>(p =>
                    {
                        var downloadDelta = p.BytesDownloaded - lastDownloaded;
                        var copyDelta = p.BytesCopied - lastCopied;
                        lastDownloaded = p.BytesDownloaded;
                        lastCopied = p.BytesCopied;

                        fileProgress.Report(new FileAssemblyProgress(
                            p.BytesComplete,
                            p.BytesTotal,
                            downloadDelta,
                            copyDelta));
                    })
                    : null;

                await _containerAssembler.AssembleAsync(
                    plan.TempPath,  // Write directly to temp path
                    plan.ManifestFile.FileUrl ?? $"files/{plan.ManifestFile.Path}",
                    plan.VirtualDeltaPlan,
                    plan.LocalPath,  // Local container path for copying entries
                    containerProgress,
                    cancellationToken);
                break;

            case DownloadMethod.Compressed:
                await DownloadCompressedAsync(plan, fileProgress, cancellationToken);
                break;

            case DownloadMethod.Full:
            default:
                await DownloadFullAsync(plan, fileProgress, cancellationToken);
                break;
        }
    }

    private async Task DownloadFullAsync(FilePlan plan, IProgress<FileAssemblyProgress>? progress, CancellationToken cancellationToken)
    {
        await using var remoteStream = await _storage.GetAsync(plan.ManifestFile.Path, cancellationToken);
        await using var fileStream = new FileStream(
            plan.TempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous);

        var buffer = new byte[81920];
        var totalBytes = plan.ManifestFile.Size;
        var bytesWritten = 0L;
        int bytesRead;

        while ((bytesRead = await remoteStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            bytesWritten += bytesRead;

            progress?.Report(new FileAssemblyProgress(
                bytesWritten,
                totalBytes,
                bytesRead,
                0));
        }
    }

    private async Task DownloadCompressedAsync(FilePlan plan, IProgress<FileAssemblyProgress>? progress, CancellationToken cancellationToken)
    {
        // TODO: Implement Zstandard decompression when ready
        // For now, fall back to full download
        var compressedPath = plan.ManifestFile.CompressedUrl ?? $"compressed/{plan.ManifestFile.Path}.zst";

        // Placeholder: just download full for now
        await DownloadFullAsync(plan, progress, cancellationToken);
    }

    #endregion

    #region Phase 3: Commit

    private async Task<CommitResult> CommitAsync(
        string localPath,
        List<AssemblyResult> assemblyResults,
        List<string> filesToDelete,
        IProgress<PatchEngineProgress>? progress,
        CancellationToken cancellationToken)
    {
        var committed = 0;
        var failed = 0;

        // Only commit successfully assembled files
        var successfulAssemblies = assemblyResults.Where(r => r.Success).ToList();

        foreach (var result in successfulAssemblies)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var finalPath = result.Plan.LocalPath;
                var tempPath = result.Plan.TempPath;

                if (File.Exists(tempPath))
                {
                    // Atomic swap: delete original, move temp
                    if (File.Exists(finalPath))
                        File.Delete(finalPath);
                    File.Move(tempPath, finalPath);
                    committed++;
                }
            }
            catch
            {
                failed++;
            }

            progress?.Report(new PatchEngineProgress(
                PatchEnginePhase.Committing,
                committed, successfulAssemblies.Count,
                0, 0, 0, 0,
                $"Committed: {result.Plan.ManifestFile.Path}"));
        }

        // Delete files marked for deletion
        foreach (var filePath in filesToDelete)
        {
            try
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }
            catch
            {
                // Ignore delete failures
            }
        }

        // Clean up any remaining temp files from failed assemblies
        foreach (var result in assemblyResults.Where(r => !r.Success))
        {
            try
            {
                if (File.Exists(result.Plan.TempPath))
                    File.Delete(result.Plan.TempPath);
            }
            catch
            {
                // Ignore cleanup failures
            }
        }

        return new CommitResult(committed, failed);

        await Task.CompletedTask; // Satisfy async signature
    }

    #endregion

    #region Phase 4: Verify

    private async Task<VerifyWithFallbackResult> VerifyWithFallbackAsync(
        string localPath,
        GameManifest manifest,
        List<FilePlan> filePlans,
        IProgress<PatchEngineProgress>? progress,
        CancellationToken cancellationToken)
    {
        var passed = 0;
        var failed = 0;
        var repaired = 0;
        var verified = 0;

        foreach (var plan in filePlans)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = plan.ManifestFile;
            var localFilePath = plan.LocalPath;

            progress?.Report(new PatchEngineProgress(
                PatchEnginePhase.Verifying,
                verified, filePlans.Count,
                0, 0, 0, 0,
                $"Verifying: {file.Path}"));

            bool isValid = false;

            if (File.Exists(localFilePath))
            {
                var localHash = await ComputeFileHashAsync(localFilePath, cancellationToken);
                isValid = string.Equals(localHash, file.Hash, StringComparison.OrdinalIgnoreCase);
            }

            if (isValid)
            {
                passed++;
            }
            else
            {
                // Fallback: try full download
                if (_options.FallbackOnVerifyFailure)
                {
                    try
                    {
                        await FallbackDownloadAsync(plan, cancellationToken);

                        // Re-verify
                        var reHash = await ComputeFileHashAsync(localFilePath, cancellationToken);
                        if (string.Equals(reHash, file.Hash, StringComparison.OrdinalIgnoreCase))
                        {
                            repaired++;
                            passed++;
                        }
                        else
                        {
                            failed++;
                        }
                    }
                    catch
                    {
                        failed++;
                    }
                }
                else
                {
                    failed++;
                }
            }

            verified++;
        }

        return new VerifyWithFallbackResult(passed, failed, repaired);
    }

    private async Task FallbackDownloadAsync(FilePlan plan, CancellationToken cancellationToken)
    {
        var tempPath = plan.TempPath;
        var finalPath = plan.LocalPath;

        try
        {
            // Try compressed first if available
            if (plan.ManifestFile.CompressedSize > 0 && !string.IsNullOrEmpty(plan.ManifestFile.CompressedUrl))
            {
                // TODO: Implement compressed download when Zstd is ready
            }

            // Full download fallback
            await using var remoteStream = await _storage.GetAsync(plan.ManifestFile.Path, cancellationToken);
            await using var fileStream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous);

            await remoteStream.CopyToAsync(fileStream, cancellationToken);
            await fileStream.FlushAsync(cancellationToken);
            await fileStream.DisposeAsync();

            // Commit
            if (File.Exists(finalPath))
                File.Delete(finalPath);
            File.Move(tempPath, finalPath);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    #endregion

    #region Helpers

    private IChunker GetChunker(GameManifest manifest)
    {
        if (_chunkerRegistry.TryGetChunker(manifest.PreferredAlgorithm, out var chunker))
            return chunker;

        var supported = manifest.SupportedAlgorithms
            .FirstOrDefault(a => _chunkerRegistry.SupportedAlgorithms.Contains(a));

        if (supported == null)
        {
            throw new NotSupportedException(
                $"No supported chunking algorithms. Server: {string.Join(", ", manifest.SupportedAlgorithms)}. " +
                $"Client: {string.Join(", ", _chunkerRegistry.SupportedAlgorithms)}");
        }

        return _chunkerRegistry.GetPreferred(new[] { supported });
    }

    private static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void Dispose()
    {
        // Nothing to dispose currently
    }

    #endregion
}

#region Types

/// <summary>
/// Options for the patch engine.
/// </summary>
public sealed class PatchEngineOptions
{
    /// <summary>Maximum concurrent file operations.</summary>
    public int MaxConcurrency { get; init; } = Environment.ProcessorCount;

    /// <summary>Threshold for delta vs compressed decision (0.0-1.0).</summary>
    public double DeltaThreshold { get; init; } = 0.8;

    /// <summary>Whether to fall back to full download on verification failure.</summary>
    public bool FallbackOnVerifyFailure { get; init; } = true;

    /// <summary>Assembly options.</summary>
    public AssemblyOptions AssemblyOptions { get; init; } = new();
}

/// <summary>
/// Current phase of the patch engine.
/// </summary>
public enum PatchEnginePhase
{
    /// <summary>Calculating delta plans.</summary>
    Planning,
    /// <summary>Downloading and assembling files to temp locations.</summary>
    Assembling,
    /// <summary>Committing temp files to final locations.</summary>
    Committing,
    /// <summary>Verifying file integrity.</summary>
    Verifying,
    /// <summary>Patch operation complete.</summary>
    Complete
}

/// <summary>
/// Progress information from the patch engine.
/// </summary>
public readonly record struct PatchEngineProgress(
    PatchEnginePhase Phase,
    int FilesComplete,
    int FilesTotal,
    long BytesComplete,
    long BytesTotal,
    long BytesDownloaded,
    long BytesCopied,
    string CurrentStatus)
{
    public double Percentage => FilesTotal > 0 ? (double)FilesComplete / FilesTotal : 0;

    /// <summary>
    /// Savings from delta patching (bytes copied locally instead of downloaded).
    /// </summary>
    public long BytesSaved => BytesCopied;

    /// <summary>
    /// Percentage of bytes that came from local copy vs download.
    /// </summary>
    public double LocalReusePercentage => BytesComplete > 0 ? (double)BytesCopied / BytesComplete : 0;
}

/// <summary>
/// Result of a patch operation.
/// </summary>
public sealed class PatchResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public int FilesPlanned { get; set; }
    public int FilesAssembled { get; set; }
    public int FilesCommitted { get; set; }
    public int FilesVerified { get; set; }
    public int FilesFailed { get; set; }
    public long BytesToDownload { get; set; }
    public long BytesDownloaded { get; set; }
    public long BytesCopied { get; set; }
    public TimeSpan ElapsedTime { get; set; }

    /// <summary>
    /// Total bytes processed (downloaded + copied).
    /// </summary>
    public long BytesTotal => BytesDownloaded + BytesCopied;

    /// <summary>
    /// Savings from delta patching.
    /// </summary>
    public long BytesSaved => BytesCopied;

    /// <summary>
    /// Percentage of bytes that came from local copy.
    /// </summary>
    public double LocalReusePercentage => BytesTotal > 0 ? (double)BytesCopied / BytesTotal : 0;
}

/// <summary>
/// Internal: Plan for the entire patch operation.
/// </summary>
internal sealed class PatchPlan
{
    public required List<FilePlan> FilesToProcess { get; init; }
    public required List<string> FilesToDelete { get; init; }
    public long TotalBytesToDownload { get; init; }
    public long TotalBytesToCopy { get; init; }
}

/// <summary>
/// Internal: Plan for a single file.
/// </summary>
internal sealed class FilePlan
{
    public required ManifestFile ManifestFile { get; init; }
    public required string LocalPath { get; init; }
    public required string TempPath { get; init; }
    public required DownloadMethod Method { get; init; }
    public DeltaPlan? DeltaPlan { get; init; }
    public VirtualDeltaPlan? VirtualDeltaPlan { get; init; }
    public IContainerHandler? ContainerHandler { get; init; }
    public long BytesToDownload { get; init; }
    public long BytesToCopy { get; init; }
}

/// <summary>
/// Internal: Result of assembling a single file.
/// </summary>
internal readonly record struct AssemblyResult(FilePlan Plan, bool Success, string? Error, TimeSpan? Elapsed = null);

/// <summary>
/// Progress for a single file during assembly (used for aggregating per-file progress).
/// </summary>
internal readonly record struct FileAssemblyProgress(
    long BytesComplete,
    long BytesTotal,
    long BytesDownloadedDelta,
    long BytesCopiedDelta)
{
    public double Percentage => BytesTotal > 0 ? (double)BytesComplete / BytesTotal : 0;
}

/// <summary>
/// Internal: Result of commit phase.
/// </summary>
internal readonly record struct CommitResult(int Committed, int Failed);

/// <summary>
/// Internal: Result of verify phase.
/// </summary>
internal readonly record struct VerifyWithFallbackResult(int Passed, int Failed, int Repaired);

#endregion
