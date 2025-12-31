using System.Security.Cryptography;
using System.Text.Json;
using PatchSync.Common.Chunking;
using PatchSync.Common.Manifest;
using PatchSync.Common.Signatures;

namespace PatchSync.SDK.Signatures;

/// <summary>
/// Generates signature files for patching.
/// </summary>
public sealed class SignatureGenerator
{
    private readonly IChunker _chunker;
    private readonly ChunkingOptions _options;

    public SignatureGenerator(IChunker chunker, ChunkingOptions? options = null)
    {
        _chunker = chunker ?? throw new ArgumentNullException(nameof(chunker));
        _options = options ?? new ChunkingOptions();
    }

    /// <summary>
    /// Generates a signature file for the given file.
    /// </summary>
    public SignatureFile GenerateSignature(string filePath)
    {
        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.SequentialScan);

        return GenerateSignature(stream);
    }

    /// <summary>
    /// Generates a signature file from a stream.
    /// </summary>
    public SignatureFile GenerateSignature(Stream input)
    {
        var chunks = _chunker.Chunk(input, _options);
        return SignatureFile.Create(_chunker.AlgorithmId, _options, chunks);
    }

    /// <summary>
    /// Generates a signature file and writes it to the output stream.
    /// </summary>
    public void GenerateSignature(string filePath, Stream output)
    {
        var signature = GenerateSignature(filePath);
        signature.Write(output);
    }

    /// <summary>
    /// Generates a signature file and saves it next to the source file.
    /// </summary>
    public void GenerateSignatureFile(string filePath, string? outputPath = null)
    {
        outputPath ??= filePath + ".sig";

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var output = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);

        GenerateSignature(filePath, output);
    }
}

/// <summary>
/// Generates game manifests from a directory of files.
/// </summary>
public sealed class ManifestGenerator
{
    private readonly IChunker _chunker;
    private readonly ChunkingOptions _options;

    public ManifestGenerator(IChunker chunker, ChunkingOptions? options = null)
    {
        _chunker = chunker ?? throw new ArgumentNullException(nameof(chunker));
        _options = options ?? new ChunkingOptions();
    }

    /// <summary>
    /// Generates a manifest for a directory of files.
    /// </summary>
    public async Task<GameManifest> GenerateManifestAsync(
        string sourceDirectory,
        string version,
        string baseUrl,
        ManifestGeneratorOptions? options = null,
        IProgress<ManifestGeneratorProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ManifestGeneratorOptions();
        var signatureGenerator = new SignatureGenerator(_chunker, _options);

        var allFiles = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(sourceDirectory, f).Replace('\\', '/'))
            .Where(f => !options.ExcludePatterns.Any(p => MatchPattern(f, p)))
            .ToList();

        var manifestFiles = new List<ManifestFile>();
        var processedCount = 0;

        foreach (var relativePath in allFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fullPath = Path.Combine(sourceDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var fileInfo = new FileInfo(fullPath);

            progress?.Report(new ManifestGeneratorProgress(
                relativePath, processedCount, allFiles.Count));

            // Compute hash
            var hash = await ComputeFileHashAsync(fullPath, cancellationToken);

            // Determine strategy
            var strategy = DetermineStrategy(relativePath, fileInfo.Length, options);

            // Generate signature if using delta strategy
            string? signaturePath = null;
            if (strategy == UpdateStrategy.Delta && options.GenerateSignatures)
            {
                var sigOutputPath = Path.Combine(
                    options.SignatureOutputDirectory ?? Path.Combine(sourceDirectory, "signatures"),
                    relativePath + ".sig");

                signatureGenerator.GenerateSignatureFile(fullPath, sigOutputPath);
                signaturePath = "signatures/" + relativePath + ".sig";
            }

            manifestFiles.Add(new ManifestFile
            {
                Path = relativePath,
                Size = fileInfo.Length,
                Hash = hash,
                Strategy = strategy,
                SignatureUrl = signaturePath,
                CompressedUrl = null, // TODO: Add compression support
                CompressedSize = 0
            });

            processedCount++;
        }

        progress?.Report(new ManifestGeneratorProgress(
            null, processedCount, allFiles.Count));

        return new GameManifest
        {
            Version = version,
            BuildDate = DateTime.UtcNow,
            SupportedAlgorithms = new[] { _chunker.AlgorithmId },
            PreferredAlgorithm = _chunker.AlgorithmId,
            BaseUrl = baseUrl,
            FallbackUrl = options.FallbackUrl,
            Files = manifestFiles
        };
    }

    /// <summary>
    /// Generates and saves a manifest to a file.
    /// </summary>
    public async Task GenerateManifestFileAsync(
        string sourceDirectory,
        string version,
        string baseUrl,
        string outputPath,
        ManifestGeneratorOptions? options = null,
        IProgress<ManifestGeneratorProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var manifest = await GenerateManifestAsync(
            sourceDirectory, version, baseUrl, options, progress, cancellationToken);

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await using var output = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);

        await JsonSerializer.SerializeAsync(
            output,
            manifest,
            ManifestJsonContext.Default.GameManifest,
            cancellationToken);
    }

    private static UpdateStrategy DetermineStrategy(string path, long size, ManifestGeneratorOptions options)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();

        // Check override patterns
        foreach (var (pattern, strategy) in options.StrategyOverrides)
        {
            if (MatchPattern(path, pattern))
                return strategy;
        }

        // Compressed media - delta is useless
        if (options.CompressedMediaExtensions.Contains(ext))
            return UpdateStrategy.AlwaysCompressed;

        // Small files - just hash check
        if (size < options.MinDeltaSize)
            return UpdateStrategy.HashCheck;

        // Default to delta
        return UpdateStrategy.Delta;
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
}

/// <summary>
/// Options for manifest generation.
/// </summary>
public sealed class ManifestGeneratorOptions
{
    /// <summary>
    /// Minimum file size for delta patching. Smaller files use hash check.
    /// Default: 64KB
    /// </summary>
    public long MinDeltaSize { get; init; } = 64 * 1024;

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
    /// Patterns to exclude from manifest.
    /// </summary>
    public IReadOnlyList<string> ExcludePatterns { get; init; } = new[]
    {
        "*.pdb", "*.log", ".git/**", ".gitignore", "*.tmp", "*.pstmp"
    };

    /// <summary>
    /// Override update strategy by pattern.
    /// </summary>
    public IReadOnlyList<(string Pattern, UpdateStrategy Strategy)> StrategyOverrides { get; init; } =
        Array.Empty<(string, UpdateStrategy)>();

    /// <summary>
    /// Whether to generate signature files.
    /// </summary>
    public bool GenerateSignatures { get; init; } = true;

    /// <summary>
    /// Output directory for signature files.
    /// </summary>
    public string? SignatureOutputDirectory { get; init; }

    /// <summary>
    /// Fallback URL for full download when delta fails.
    /// </summary>
    public string? FallbackUrl { get; init; }
}

/// <summary>
/// Progress information during manifest generation.
/// </summary>
public readonly record struct ManifestGeneratorProgress(
    string? CurrentFile,
    int FilesProcessed,
    int TotalFiles)
{
    public double Percentage => TotalFiles > 0 ? (double)FilesProcessed / TotalFiles : 0;
}
