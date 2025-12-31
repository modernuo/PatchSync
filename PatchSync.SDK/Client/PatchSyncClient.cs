using System.Security.Cryptography;
using System.Text.Json;
using PatchSync.Common.Chunking;
using PatchSync.Common.Manifest;
using PatchSync.Common.Signatures;
using PatchSync.Common.Storage;
using PatchSync.SDK.Assembly;
using PatchSync.SDK.Delta;
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
    /// </summary>
    public async Task PatchAsync(
        string localPath,
        GameManifest manifest,
        IProgress<PatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Validate we support at least one algorithm
        if (!_chunkerRegistry.TryGetChunker(manifest.PreferredAlgorithm, out var chunker))
        {
            var supported = manifest.SupportedAlgorithms
                .FirstOrDefault(a => _chunkerRegistry.SupportedAlgorithms.Contains(a));

            if (supported == null)
            {
                throw new NotSupportedException(
                    $"No supported chunking algorithms. Server supports: {string.Join(", ", manifest.SupportedAlgorithms)}. " +
                    $"Client supports: {string.Join(", ", _chunkerRegistry.SupportedAlgorithms)}");
            }

            chunker = _chunkerRegistry.GetPreferred(new[] { supported });
        }

        var filesToProcess = manifest.Files
            .Where(f => f.Strategy != UpdateStrategy.Delete)
            .ToList();

        var totalBytes = filesToProcess.Sum(f => f.Size);
        var processedBytes = 0L;
        var processedFiles = 0;

        progress?.Report(new PatchProgress(
            PatchPhase.Starting,
            0, filesToProcess.Count,
            0, totalBytes,
            null, 0));

        foreach (var file in filesToProcess)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var localFilePath = Path.Combine(localPath, file.Path.Replace('/', Path.DirectorySeparatorChar));
            var fileProgress = new FileProgress(file.Path);

            progress?.Report(new PatchProgress(
                PatchPhase.Processing,
                processedFiles, filesToProcess.Count,
                processedBytes, totalBytes,
                file.Path, 0));

            await ProcessFileAsync(
                localFilePath, file, chunker, manifest,
                p => progress?.Report(new PatchProgress(
                    PatchPhase.Processing,
                    processedFiles, filesToProcess.Count,
                    processedBytes + (long)(file.Size * p), totalBytes,
                    file.Path, p)),
                cancellationToken);

            processedBytes += file.Size;
            processedFiles++;
        }

        // Handle deletions
        var filesToDelete = manifest.Files
            .Where(f => f.Strategy == UpdateStrategy.Delete)
            .ToList();

        foreach (var file in filesToDelete)
        {
            var localFilePath = Path.Combine(localPath, file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(localFilePath))
            {
                File.Delete(localFilePath);
            }
        }

        progress?.Report(new PatchProgress(
            PatchPhase.Complete,
            processedFiles, filesToProcess.Count,
            totalBytes, totalBytes,
            null, 1.0));
    }

    private async Task ProcessFileAsync(
        string localFilePath,
        ManifestFile file,
        IChunker chunker,
        GameManifest manifest,
        Action<double>? progressCallback,
        CancellationToken cancellationToken)
    {
        // Check if file exists and matches hash
        if (File.Exists(localFilePath))
        {
            var localHash = await ComputeFileHashAsync(localFilePath, cancellationToken);
            if (string.Equals(localHash, file.Hash, StringComparison.OrdinalIgnoreCase))
            {
                progressCallback?.Invoke(1.0);
                return; // File is up to date
            }
        }

        // Determine update strategy
        switch (file.Strategy)
        {
            case UpdateStrategy.HashCheck:
            case UpdateStrategy.AlwaysCompressed:
                await DownloadFullFileAsync(localFilePath, file, manifest, cancellationToken);
                break;

            case UpdateStrategy.CreateOnly:
                if (!File.Exists(localFilePath))
                {
                    await DownloadFullFileAsync(localFilePath, file, manifest, cancellationToken);
                }
                break;

            case UpdateStrategy.Delta:
            default:
                await DeltaPatchFileAsync(localFilePath, file, chunker, manifest, progressCallback, cancellationToken);
                break;
        }

        progressCallback?.Invoke(1.0);
    }

    private async Task DeltaPatchFileAsync(
        string localFilePath,
        ManifestFile file,
        IChunker chunker,
        GameManifest manifest,
        Action<double>? progressCallback,
        CancellationToken cancellationToken)
    {
        // Download signature
        var signaturePath = file.SignatureUrl ?? $"signatures/{file.Path}.sig";
        await using var sigStream = await _storage.GetAsync(signaturePath, cancellationToken);
        var signature = SignatureFile.Read(sigStream);

        // Build local chunk source if file exists
        IChunkSource localSource;
        if (File.Exists(localFilePath))
        {
            localSource = LocalFileSource.Build(
                localFilePath,
                chunker,
                signature.GetChunkingOptions());
        }
        else
        {
            localSource = new CompositeChunkSource();
        }

        // Calculate delta
        var calculator = new DeltaCalculator();
        var plan = calculator.Calculate(signature, localSource);

        // Decide strategy
        var decision = _strategySelector.Select(
            plan,
            file.CompressedSize,
            file.Size);

        if (decision.Method == DownloadMethod.Compressed && !string.IsNullOrEmpty(file.CompressedUrl))
        {
            await DownloadCompressedFileAsync(localFilePath, file, manifest, cancellationToken);
            return;
        }

        if (decision.Method == DownloadMethod.Full)
        {
            await DownloadFullFileAsync(localFilePath, file, manifest, cancellationToken);
            return;
        }

        // Delta patch
        var remotePath = file.Path;
        var assemblyProgress = new Progress<AssemblyProgress>(p =>
            progressCallback?.Invoke(p.Percentage));

        await _assembler.AssembleAsync(
            localFilePath,
            remotePath,
            plan,
            file.Hash,
            assemblyProgress,
            cancellationToken);
    }

    private async Task DownloadFullFileAsync(
        string localFilePath,
        ManifestFile file,
        GameManifest manifest,
        CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(localFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tempPath = localFilePath + ".pstmp";

        try
        {
            await using var remoteStream = await _storage.GetAsync(file.Path, cancellationToken);
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

            // Verify hash
            var hash = await ComputeFileHashAsync(tempPath, cancellationToken);
            if (!string.Equals(hash, file.Hash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Hash mismatch for {file.Path}. Expected: {file.Hash}, Actual: {hash}");
            }

            // Atomic rename
            if (File.Exists(localFilePath))
                File.Delete(localFilePath);
            File.Move(tempPath, localFilePath);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }
    }

    private async Task DownloadCompressedFileAsync(
        string localFilePath,
        ManifestFile file,
        GameManifest manifest,
        CancellationToken cancellationToken)
    {
        // For now, compressed downloads decompress in memory
        // Could be improved with streaming decompression
        var compressedPath = file.CompressedUrl ?? $"compressed/{file.Path}.zst";
        var dir = Path.GetDirectoryName(localFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tempPath = localFilePath + ".pstmp";

        try
        {
            await using var compressedStream = await _storage.GetAsync(compressedPath, cancellationToken);

            // TODO: Add Zstandard decompression
            // For now, fall back to full download
            await DownloadFullFileAsync(localFilePath, file, manifest, cancellationToken);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }
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
/// Progress for an individual file.
/// </summary>
internal sealed class FileProgress
{
    public string Path { get; }
    public long BytesComplete { get; set; }
    public long BytesTotal { get; set; }

    public FileProgress(string path) => Path = path;
}
