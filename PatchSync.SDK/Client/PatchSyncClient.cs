using System.Security.Cryptography;
using System.Text.Json;
using PatchSync.Common.Chunking;
using PatchSync.Common.Manifest;
using PatchSync.Common.Signatures;
using PatchSync.Common.Storage;
using PatchSync.SDK.Assembly;
using PatchSync.SDK.Delta;
using PatchSync.SDK.Engine;
using PatchSync.SDK.Sources;

namespace PatchSync.SDK.Client;

/// <summary>
/// Main client for applying patches to a game installation.
/// </summary>
public sealed class PatchSyncClient : IDisposable
{
    private readonly IStorageProvider _storage;
    private readonly IChunkerRegistry _chunkerRegistry;
    private readonly PatchClientOptions _options;
    private readonly FileAssembler _assembler;
    private readonly DownloadStrategySelector _strategySelector;
    private readonly PatchEngine _engine;

    public PatchSyncClient(
        IStorageProvider storage,
        IChunkerRegistry? chunkerRegistry = null,
        PatchClientOptions? options = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _chunkerRegistry = chunkerRegistry ?? ChunkerRegistry.Default;
        _options = options ?? new PatchClientOptions();
        _assembler = new FileAssembler(storage, _options.AssemblyOptions);
        _strategySelector = new DownloadStrategySelector(_options.DeltaThreshold);
        _engine = new PatchEngine(storage, chunkerRegistry, containerRegistry: null, new PatchEngineOptions
        {
            MaxConcurrency = _options.MaxConcurrency,
            DeltaThreshold = _options.DeltaThreshold,
            FallbackOnVerifyFailure = _options.VerifyAfterPatch,
            AssemblyOptions = _options.AssemblyOptions
        });
    }

    /// <summary>
    /// Fetches the manifest from the remote storage.
    /// </summary>
    public async Task<GameManifest> GetManifestAsync(
        string manifestPath = "manifest.json",
        CancellationToken cancellationToken = default)
    {
        await using var stream = await _storage.GetAsync(manifestPath, cancellationToken);
        var manifest = await JsonSerializer.DeserializeAsync(
            stream,
            ManifestJsonContext.Default.GameManifest,
            cancellationToken);

        return manifest ?? throw new InvalidDataException("Manifest was null");
    }

    /// <summary>
    /// Applies patches to bring the local installation up to date with the manifest.
    /// Uses a phased approach for safety:
    /// 1. Plan: Calculate delta plans for all files
    /// 2. Assemble: Download/copy chunks to temp files (parallel)
    /// 3. Commit: Atomically swap temp files to final locations
    /// 4. Verify: Hash-verify all files, fallback to full download for failures
    /// </summary>
    public async Task PatchAsync(
        string localPath,
        GameManifest manifest,
        IProgress<PatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Adapter to convert PatchEngineProgress to PatchProgress
        var engineProgress = progress != null
            ? new Progress<PatchEngineProgress>(p =>
            {
                var phase = p.Phase switch
                {
                    PatchEnginePhase.Planning => PatchPhase.Starting,
                    PatchEnginePhase.Assembling => PatchPhase.Processing,
                    PatchEnginePhase.Committing => PatchPhase.Processing,
                    PatchEnginePhase.Verifying => PatchPhase.Verifying,
                    PatchEnginePhase.Complete => PatchPhase.Complete,
                    _ => PatchPhase.Processing
                };

                progress.Report(new PatchProgress(
                    phase,
                    p.FilesComplete,
                    p.FilesTotal,
                    p.BytesComplete,
                    p.BytesTotal,
                    p.CurrentStatus,
                    p.Percentage));
            })
            : null;

        var result = await _engine.PatchAsync(localPath, manifest, engineProgress, cancellationToken);

        if (!result.Success && result.FilesFailed > 0)
        {
            throw new InvalidOperationException(
                $"Patch completed with {result.FilesFailed} verification failures. {result.Message}");
        }
    }

    /// <summary>
    /// Scans the local installation against the manifest to determine what needs updating.
    /// Does not download or modify any files.
    /// </summary>
    public async Task<ScanResult> ScanAsync(
        string localPath,
        GameManifest manifest,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var upToDate = new List<FileStatus>();
        var needsUpdate = new List<FileStatus>();
        var missing = new List<FileStatus>();
        var toDelete = new List<FileStatus>();

        var files = manifest.Files.ToList();
        var scanned = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new ScanProgress(scanned, files.Count, file.Path));

            var localFilePath = Path.Combine(localPath, file.Path.Replace('/', Path.DirectorySeparatorChar));

            if (file.Strategy == UpdateStrategy.Delete)
            {
                if (File.Exists(localFilePath))
                {
                    toDelete.Add(new FileStatus(file.Path, file.Size, FileStatusType.ToDelete, null, file.Hash, file.Strategy));
                }
            }
            else if (!File.Exists(localFilePath))
            {
                missing.Add(new FileStatus(file.Path, file.Size, FileStatusType.Missing, null, file.Hash, file.Strategy));
            }
            else
            {
                var localHash = await ComputeFileHashAsync(localFilePath, cancellationToken);
                if (string.Equals(localHash, file.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    upToDate.Add(new FileStatus(file.Path, file.Size, FileStatusType.UpToDate, localHash, file.Hash, file.Strategy));
                }
                else
                {
                    needsUpdate.Add(new FileStatus(file.Path, file.Size, FileStatusType.NeedsUpdate, localHash, file.Hash, file.Strategy));
                }
            }

            scanned++;
        }

        progress?.Report(new ScanProgress(scanned, files.Count, null));

        return new ScanResult
        {
            UpToDate = upToDate,
            NeedsUpdate = needsUpdate,
            Missing = missing,
            ToDelete = toDelete
        };
    }

    /// <summary>
    /// Verifies all local files against the manifest.
    /// Returns detailed results about which files pass/fail verification.
    /// </summary>
    public async Task<VerifyResult> VerifyAsync(
        string localPath,
        GameManifest manifest,
        IProgress<VerifyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var passed = new List<FileStatus>();
        var failed = new List<FileStatus>();

        var files = manifest.Files
            .Where(f => f.Strategy != UpdateStrategy.Delete)
            .ToList();

        var verified = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new VerifyProgress(
                verified, files.Count,
                passed.Count, failed.Count,
                file.Path));

            var localFilePath = Path.Combine(localPath, file.Path.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(localFilePath))
            {
                failed.Add(new FileStatus(file.Path, file.Size, FileStatusType.Missing, null, file.Hash));
            }
            else
            {
                var localHash = await ComputeFileHashAsync(localFilePath, cancellationToken);
                if (string.Equals(localHash, file.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    passed.Add(new FileStatus(file.Path, file.Size, FileStatusType.UpToDate, localHash, file.Hash));
                }
                else
                {
                    failed.Add(new FileStatus(file.Path, file.Size, FileStatusType.NeedsUpdate, localHash, file.Hash));
                }
            }

            verified++;
        }

        progress?.Report(new VerifyProgress(
            verified, files.Count,
            passed.Count, failed.Count,
            null));

        return new VerifyResult
        {
            Passed = passed,
            Failed = failed
        };
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
        _engine.Dispose();
        if (_storage is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

/// <summary>
/// Options for patch client behavior.
/// </summary>
public sealed class PatchClientOptions
{
    /// <summary>
    /// Threshold for delta vs compressed download decision.
    /// If delta size >= compressed size * threshold, use compressed.
    /// Default: 0.8 (use compressed if delta is 80%+ of compressed size)
    /// </summary>
    public double DeltaThreshold { get; init; } = 0.8;

    /// <summary>
    /// Options for file assembly.
    /// </summary>
    public AssemblyOptions AssemblyOptions { get; init; } = new();

    /// <summary>
    /// Maximum concurrent file operations.
    /// </summary>
    public int MaxConcurrency { get; init; } = 4;

    /// <summary>
    /// Whether to verify file hashes after patching.
    /// </summary>
    public bool VerifyAfterPatch { get; init; } = true;
}

/// <summary>
/// Progress information during patching.
/// </summary>
public readonly record struct PatchProgress(
    PatchPhase Phase,
    int FilesComplete,
    int FilesTotal,
    long BytesComplete,
    long BytesTotal,
    string? CurrentFile,
    double CurrentFileProgress)
{
    /// <summary>
    /// Overall completion percentage (0.0 to 1.0).
    /// </summary>
    public double OverallPercentage => BytesTotal > 0 ? (double)BytesComplete / BytesTotal : 0;
}

/// <summary>
/// Current phase of patching.
/// </summary>
public enum PatchPhase
{
    /// <summary>Starting patch operation.</summary>
    Starting,
    /// <summary>Processing files.</summary>
    Processing,
    /// <summary>Verifying files.</summary>
    Verifying,
    /// <summary>Patch complete.</summary>
    Complete
}

/// <summary>
/// Progress information during scanning.
/// </summary>
public readonly record struct ScanProgress(
    int FilesScanned,
    int FilesTotal,
    string? CurrentFile)
{
    /// <summary>
    /// Completion percentage (0.0 to 1.0).
    /// </summary>
    public double Percentage => FilesTotal > 0 ? (double)FilesScanned / FilesTotal : 0;
}

/// <summary>
/// Result of scanning a single file.
/// </summary>
public readonly record struct FileStatus(
    string Path,
    long Size,
    FileStatusType Status,
    string? LocalHash,
    string ExpectedHash,
    UpdateStrategy Strategy = UpdateStrategy.Delta);

/// <summary>
/// Status type for a file.
/// </summary>
public enum FileStatusType
{
    /// <summary>File matches manifest - up to date.</summary>
    UpToDate,
    /// <summary>File exists but hash differs - needs update.</summary>
    NeedsUpdate,
    /// <summary>File missing locally - needs download.</summary>
    Missing,
    /// <summary>File exists locally but not in manifest - orphaned.</summary>
    Orphaned,
    /// <summary>File should be deleted per manifest.</summary>
    ToDelete
}

/// <summary>
/// Result of scanning an installation.
/// </summary>
public sealed class ScanResult
{
    /// <summary>Files that are up to date.</summary>
    public IReadOnlyList<FileStatus> UpToDate { get; init; } = Array.Empty<FileStatus>();

    /// <summary>Files that need updating.</summary>
    public IReadOnlyList<FileStatus> NeedsUpdate { get; init; } = Array.Empty<FileStatus>();

    /// <summary>Files that are missing.</summary>
    public IReadOnlyList<FileStatus> Missing { get; init; } = Array.Empty<FileStatus>();

    /// <summary>Files to delete.</summary>
    public IReadOnlyList<FileStatus> ToDelete { get; init; } = Array.Empty<FileStatus>();

    /// <summary>
    /// Total worst-case bytes that need to be downloaded (if all files were downloaded fully).
    /// For actual delta-aware estimates, use <see cref="GetStrategyBreakdown"/>.
    /// </summary>
    public long BytesToDownload => NeedsUpdate.Sum(f => f.Size) + Missing.Sum(f => f.Size);

    /// <summary>True if the installation is fully up to date.</summary>
    public bool IsUpToDate => NeedsUpdate.Count == 0 && Missing.Count == 0 && ToDelete.Count == 0;

    /// <summary>Total number of files in manifest.</summary>
    public int TotalFiles => UpToDate.Count + NeedsUpdate.Count + Missing.Count;

    /// <summary>
    /// Gets a breakdown of files by update strategy, with estimated download bytes.
    /// </summary>
    /// <remarks>
    /// Estimates are based on typical delta ratios:
    /// - Delta: ~20% of file size for existing files, 100% for missing
    /// - VirtualDelta: ~10% of file size for existing files, 100% for missing
    /// - HashCheck/AlwaysCompressed: 100% of file size
    /// Actual savings depend on file content and local file state.
    /// </remarks>
    public ScanStrategyBreakdown GetStrategyBreakdown()
    {
        var allFiles = NeedsUpdate.Concat(Missing).ToList();

        var deltaFiles = allFiles.Where(f => f.Strategy == UpdateStrategy.Delta).ToList();
        var virtualDeltaFiles = allFiles.Where(f => f.Strategy == UpdateStrategy.VirtualDelta).ToList();
        var fullDownloadFiles = allFiles.Where(f =>
            f.Strategy == UpdateStrategy.HashCheck ||
            f.Strategy == UpdateStrategy.AlwaysCompressed ||
            f.Strategy == UpdateStrategy.CreateOnly).ToList();

        // Estimate delta savings: existing files can use delta, missing files need full download
        long deltaWorstCase = deltaFiles.Sum(f => f.Size);
        long deltaEstimated = deltaFiles.Sum(f =>
            f.Status == FileStatusType.NeedsUpdate
                ? (long)(f.Size * 0.20) // ~20% for updates
                : f.Size);              // 100% for missing

        long virtualDeltaWorstCase = virtualDeltaFiles.Sum(f => f.Size);
        long virtualDeltaEstimated = virtualDeltaFiles.Sum(f =>
            f.Status == FileStatusType.NeedsUpdate
                ? (long)(f.Size * 0.10) // ~10% for container updates
                : f.Size);              // 100% for missing

        long fullDownloadTotal = fullDownloadFiles.Sum(f => f.Size);

        return new ScanStrategyBreakdown(
            DeltaFileCount: deltaFiles.Count,
            DeltaWorstCaseBytes: deltaWorstCase,
            DeltaEstimatedBytes: deltaEstimated,
            VirtualDeltaFileCount: virtualDeltaFiles.Count,
            VirtualDeltaWorstCaseBytes: virtualDeltaWorstCase,
            VirtualDeltaEstimatedBytes: virtualDeltaEstimated,
            FullDownloadFileCount: fullDownloadFiles.Count,
            FullDownloadBytes: fullDownloadTotal,
            TotalWorstCaseBytes: BytesToDownload,
            TotalEstimatedBytes: deltaEstimated + virtualDeltaEstimated + fullDownloadTotal);
    }
}

/// <summary>
/// Breakdown of files by update strategy with download estimates.
/// </summary>
public readonly record struct ScanStrategyBreakdown(
    int DeltaFileCount,
    long DeltaWorstCaseBytes,
    long DeltaEstimatedBytes,
    int VirtualDeltaFileCount,
    long VirtualDeltaWorstCaseBytes,
    long VirtualDeltaEstimatedBytes,
    int FullDownloadFileCount,
    long FullDownloadBytes,
    long TotalWorstCaseBytes,
    long TotalEstimatedBytes)
{
    /// <summary>
    /// Estimated savings from delta patching.
    /// </summary>
    public long EstimatedSavings => TotalWorstCaseBytes - TotalEstimatedBytes;

    /// <summary>
    /// Estimated savings percentage (0.0 to 1.0).
    /// </summary>
    public double SavingsPercentage => TotalWorstCaseBytes > 0
        ? (double)EstimatedSavings / TotalWorstCaseBytes
        : 0;
}

/// <summary>
/// Progress information during verification.
/// </summary>
public readonly record struct VerifyProgress(
    int FilesVerified,
    int FilesTotal,
    int FilesPassed,
    int FilesFailed,
    string? CurrentFile)
{
    /// <summary>
    /// Completion percentage (0.0 to 1.0).
    /// </summary>
    public double Percentage => FilesTotal > 0 ? (double)FilesVerified / FilesTotal : 0;
}

/// <summary>
/// Result of verification.
/// </summary>
public sealed class VerifyResult
{
    /// <summary>Files that passed verification.</summary>
    public IReadOnlyList<FileStatus> Passed { get; init; } = Array.Empty<FileStatus>();

    /// <summary>Files that failed verification (hash mismatch or missing).</summary>
    public IReadOnlyList<FileStatus> Failed { get; init; } = Array.Empty<FileStatus>();

    /// <summary>True if all files passed verification.</summary>
    public bool Success => Failed.Count == 0;

    /// <summary>Total files verified.</summary>
    public int TotalFiles => Passed.Count + Failed.Count;
}
