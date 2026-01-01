using System.Collections.Concurrent;
using System.Security.Cryptography;
using PatchSync.Common.Chunking;
using PatchSync.Common.Manifest;
using PatchSync.SDK.Containers;
using PatchSync.SDK.Signatures;

namespace PatchSync.CLI.Build;

/// <summary>
/// Progress information during parallel build.
/// </summary>
public readonly record struct ParallelBuildProgress(
    int SlotIndex,
    string? FileName,
    double FileProgress,
    int FilesComplete,
    int FilesTotal,
    long BytesProcessed,
    long BytesTotal,
    int SignaturesGenerated,
    long FileSize = 0,
    bool HasSignature = false,
    bool IsCompletion = false)
{
    public double OverallPercentage => FilesTotal > 0 ? (double)FilesComplete / FilesTotal : 0;
}

/// <summary>
/// Options for parallel signature building.
/// </summary>
public sealed class ParallelBuildOptions
{
    /// <summary>
    /// Minimum file size for delta patching. Smaller files use hash check.
    /// </summary>
    public long MinDeltaSize { get; init; } = 64 * 1024;

    /// <summary>
    /// Whether to generate signature files.
    /// </summary>
    public bool GenerateSignatures { get; init; } = true;

    /// <summary>
    /// Output directory for signature files.
    /// </summary>
    public string? SignatureOutputDirectory { get; init; }

    /// <summary>
    /// Fallback URL for full download.
    /// </summary>
    public string? FallbackUrl { get; init; }

    /// <summary>
    /// File extensions that are compressed media (delta is useless).
    /// </summary>
    public IReadOnlySet<string> CompressedMediaExtensions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".ogg", ".mp4", ".webm", ".m4a", ".aac",
        ".jpg", ".jpeg", ".png", ".webp", ".gif",
        ".zip", ".gz", ".bz2", ".xz", ".zst"
    };

    /// <summary>
    /// Container/archive file extensions where delta patching is ineffective.
    /// These formats have internal offset tables that cause chunk boundaries to shift
    /// when content changes, defeating CDC algorithms.
    /// </summary>
    public IReadOnlySet<string> ContainerFileExtensions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".uop",   // UO MYP container format
        ".pak",   // Generic package files
        ".vpk",   // Valve package format
        ".bsa",   // Bethesda archive
        ".ba2",   // Bethesda archive v2
        ".rpf",   // Rockstar package format
        ".u",     // Unreal Engine packages
        ".upk",   // Unreal Engine 3 packages
        ".uasset" // Unreal Engine 4+ assets
    };

    /// <summary>
    /// Patterns to exclude from manifest.
    /// </summary>
    public IReadOnlyList<string> ExcludePatterns { get; init; } = new[]
    {
        "*.pdb", "*.log", ".git/**", ".gitignore", "*.tmp", "*.pstmp"
    };
}

/// <summary>
/// Parallel signature and manifest builder for the CLI.
/// Uses Parallel.ForEachAsync for work-stealing parallelism.
/// </summary>
public sealed class ParallelSignatureBuilder
{
    private readonly IChunker _chunker;
    private readonly ChunkingOptions _options;
    private readonly ContainerRegistry _containerRegistry;

    /// <summary>
    /// Maximum concurrent file processing. Defaults to ProcessorCount - 1.
    /// </summary>
    public int MaxConcurrency { get; init; } = Math.Max(1, Environment.ProcessorCount - 1);

    public ParallelSignatureBuilder(
        IChunker chunker,
        ChunkingOptions? options = null,
        ContainerRegistry? containerRegistry = null)
    {
        _chunker = chunker ?? throw new ArgumentNullException(nameof(chunker));
        _options = options ?? new ChunkingOptions();
        _containerRegistry = containerRegistry ?? ContainerRegistry.Default;
    }

    /// <summary>
    /// Builds a manifest with parallel file processing.
    /// </summary>
    public async Task<GameManifest> BuildAsync(
        string sourceDirectory,
        string version,
        string baseUrl,
        ParallelBuildOptions? options = null,
        IProgress<ParallelBuildProgress>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new ParallelBuildOptions();

        // Enumerate files
        var allFiles = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .Where(f => !MatchesExcludePattern(
                Path.GetRelativePath(sourceDirectory, f.FullName).Replace('\\', '/'),
                options.ExcludePatterns))
            .OrderBy(f => f.FullName) // Consistent ordering
            .ToList();

        var totalBytes = allFiles.Sum(f => f.Length);
        var results = new ConcurrentBag<ManifestFile>();

        // Thread-safe counters
        var filesComplete = 0;
        var bytesProcessed = 0L;
        var signaturesGenerated = 0;

        // Slot tracking for UI (maps thread to slot index)
        var slotAssignments = new ConcurrentDictionary<int, int>();
        var nextSlot = 0;

        int GetSlotIndex()
        {
            var threadId = Environment.CurrentManagedThreadId;
            return slotAssignments.GetOrAdd(threadId, _ => Interlocked.Increment(ref nextSlot) - 1);
        }

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxConcurrency,
            CancellationToken = ct
        };

        var signatureGenerator = new SignatureGenerator(_chunker, _options);
        var virtualSignatureGenerator = new VirtualSignatureGenerator(_chunker, _options);

        await Parallel.ForEachAsync(allFiles, parallelOptions, async (fileInfo, token) =>
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, fileInfo.FullName).Replace('\\', '/');
            var slotIndex = GetSlotIndex();

            // Report start
            progress?.Report(new ParallelBuildProgress(
                slotIndex,
                relativePath,
                0,
                Volatile.Read(ref filesComplete),
                allFiles.Count,
                Volatile.Read(ref bytesProcessed),
                totalBytes,
                Volatile.Read(ref signaturesGenerated)));

            // Compute hash
            var hash = await ComputeFileHashAsync(fileInfo.FullName, token);

            // Check if this is a supported container file
            var ext = Path.GetExtension(relativePath);
            IContainerHandler? containerHandler = null;
            if (options.ContainerFileExtensions.Contains(ext))
            {
                _containerRegistry.TryGetHandlerForExtension(ext, out containerHandler);
            }

            // Determine strategy
            var strategy = DetermineStrategy(relativePath, fileInfo.Length, options, containerHandler);

            // Generate signature if needed
            string? signaturePath = null;
            string? virtualSignaturePath = null;
            string? containerFormat = null;

            if (strategy == UpdateStrategy.Delta && options.GenerateSignatures)
            {
                var sigOutputPath = Path.Combine(
                    options.SignatureOutputDirectory ?? Path.Combine(sourceDirectory, "signatures"),
                    relativePath + ".sig");

                signatureGenerator.GenerateSignatureFile(fileInfo.FullName, sigOutputPath);
                signaturePath = "signatures/" + relativePath + ".sig";
                Interlocked.Increment(ref signaturesGenerated);
            }
            else if (strategy == UpdateStrategy.VirtualDelta && options.GenerateSignatures && containerHandler != null)
            {
                // Check if virtual delta is efficient for this container
                var efficiencyResult = virtualSignatureGenerator.CheckEfficiency(
                    fileInfo.FullName, containerHandler);

                if (efficiencyResult.IsEfficient)
                {
                    // Generate virtual signature
                    var vsigOutputPath = Path.Combine(
                        options.SignatureOutputDirectory ?? Path.Combine(sourceDirectory, "signatures"),
                        relativePath + ".vsig");

                    virtualSignatureGenerator.GenerateSignatureFile(fileInfo.FullName, containerHandler, vsigOutputPath);
                    virtualSignaturePath = "signatures/" + relativePath + ".vsig";
                    containerFormat = containerHandler.FormatId;
                    Interlocked.Increment(ref signaturesGenerated);
                }
                else
                {
                    // Fall back to regular Delta (CDC chunking) for inefficient containers
                    strategy = UpdateStrategy.Delta;
                    var sigOutputPath = Path.Combine(
                        options.SignatureOutputDirectory ?? Path.Combine(sourceDirectory, "signatures"),
                        relativePath + ".sig");

                    signatureGenerator.GenerateSignatureFile(fileInfo.FullName, sigOutputPath);
                    signaturePath = "signatures/" + relativePath + ".sig";
                    Interlocked.Increment(ref signaturesGenerated);
                }
            }

            // Add result
            results.Add(new ManifestFile
            {
                Path = relativePath,
                Size = fileInfo.Length,
                Hash = hash,
                Strategy = strategy,
                SignatureUrl = signaturePath,
                VirtualSignatureUrl = virtualSignaturePath,
                ContainerFormat = containerFormat,
                CompressedUrl = null,
                CompressedSize = 0
            });

            // Update counters
            Interlocked.Increment(ref filesComplete);
            Interlocked.Add(ref bytesProcessed, fileInfo.Length);

            // Report completion with file info for streaming display
            progress?.Report(new ParallelBuildProgress(
                slotIndex,
                relativePath,
                1.0,
                Volatile.Read(ref filesComplete),
                allFiles.Count,
                Volatile.Read(ref bytesProcessed),
                totalBytes,
                Volatile.Read(ref signaturesGenerated),
                FileSize: fileInfo.Length,
                HasSignature: signaturePath != null || virtualSignaturePath != null,
                IsCompletion: true));
        });

        // Final progress report to ensure 100% is displayed
        progress?.Report(new ParallelBuildProgress(
            SlotIndex: -1,
            FileName: null,
            FileProgress: 1.0,
            FilesComplete: allFiles.Count,
            FilesTotal: allFiles.Count,
            BytesProcessed: totalBytes,
            BytesTotal: totalBytes,
            SignaturesGenerated: Volatile.Read(ref signaturesGenerated)));

        // Build manifest from results (sorted for consistency)
        var sortedFiles = results.OrderBy(f => f.Path).ToList();

        return new GameManifest
        {
            Version = version,
            BuildDate = DateTime.UtcNow,
            SupportedAlgorithms = new[] { _chunker.AlgorithmId },
            PreferredAlgorithm = _chunker.AlgorithmId,
            BaseUrl = baseUrl,
            FallbackUrl = options.FallbackUrl,
            Files = sortedFiles
        };
    }

    private static UpdateStrategy DetermineStrategy(
        string path,
        long size,
        ParallelBuildOptions options,
        IContainerHandler? containerHandler)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();

        // Compressed media - delta is useless
        if (options.CompressedMediaExtensions.Contains(ext))
            return UpdateStrategy.AlwaysCompressed;

        // Container files - use VirtualDelta if handler available, otherwise HashCheck
        if (options.ContainerFileExtensions.Contains(ext))
        {
            return containerHandler != null
                ? UpdateStrategy.VirtualDelta
                : UpdateStrategy.HashCheck;
        }

        // Small files - just hash check
        if (size < options.MinDeltaSize)
            return UpdateStrategy.HashCheck;

        // Default to delta
        return UpdateStrategy.Delta;
    }

    private static bool MatchesExcludePattern(string path, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (MatchPattern(path, pattern))
                return true;
        }
        return false;
    }

    private static bool MatchPattern(string path, string pattern)
    {
        // Simple glob matching (supports * and **)
        var parts = pattern.Split('*');
        var pos = 0;

        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part))
                continue;

            var index = path.IndexOf(part, pos, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return false;

            pos = index + part.Length;
        }

        return true;
    }

    private static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
