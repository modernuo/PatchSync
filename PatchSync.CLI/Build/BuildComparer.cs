using System.Security.Cryptography;
using PatchSync.Common.Manifest;

namespace PatchSync.CLI.Build;

/// <summary>
/// Type of change detected for a file.
/// </summary>
public enum FileChangeType
{
    /// <summary>File exists in new version but not in base</summary>
    New,

    /// <summary>File exists in both but content differs</summary>
    Modified,

    /// <summary>File exists in base but not in new version</summary>
    Deleted,

    /// <summary>File exists in both with identical content</summary>
    Unchanged
}

/// <summary>
/// Comparison entry for a single file.
/// </summary>
public sealed class FileComparisonEntry
{
    /// <summary>Relative file path</summary>
    public required string Path { get; init; }

    /// <summary>Size in current version (null if deleted)</summary>
    public long? CurrentSize { get; init; }

    /// <summary>Size in base version (null if new)</summary>
    public long? BaseSize { get; init; }

    /// <summary>Hash in current version (null if deleted)</summary>
    public string? CurrentHash { get; init; }

    /// <summary>Hash in base version (null if new)</summary>
    public string? BaseHash { get; init; }

    /// <summary>Type of change detected</summary>
    public FileChangeType ChangeType { get; init; }

    /// <summary>Strategy from base version (null if new)</summary>
    public UpdateStrategy? BaseStrategy { get; init; }

    /// <summary>Suggested strategy for new version</summary>
    public UpdateStrategy SuggestedStrategy { get; init; }
}

/// <summary>
/// Result of comparing input files against a base version.
/// </summary>
public sealed class BuildComparisonResult
{
    /// <summary>Files that are new in this version</summary>
    public List<FileComparisonEntry> NewFiles { get; init; } = [];

    /// <summary>Files that were modified from base version</summary>
    public List<FileComparisonEntry> ModifiedFiles { get; init; } = [];

    /// <summary>Files that were deleted from base version</summary>
    public List<FileComparisonEntry> DeletedFiles { get; init; } = [];

    /// <summary>Files that are unchanged from base version</summary>
    public List<FileComparisonEntry> UnchangedFiles { get; init; } = [];

    /// <summary>Total number of files in the comparison</summary>
    public int TotalFiles => NewFiles.Count + ModifiedFiles.Count + DeletedFiles.Count + UnchangedFiles.Count;

    /// <summary>Base version channel</summary>
    public string? BaseChannel { get; init; }

    /// <summary>Base version string</summary>
    public string? BaseVersion { get; init; }

    /// <summary>When the base version was built</summary>
    public DateTime? BaseBuiltAt { get; init; }
}

/// <summary>
/// Compares input files against a base version's manifest.
/// </summary>
public sealed class BuildComparer
{
    private readonly IProgress<string>? _progress;

    public BuildComparer(IProgress<string>? progress = null)
    {
        _progress = progress;
    }

    /// <summary>
    /// Compare input directory against a base manifest.
    /// </summary>
    public async Task<BuildComparisonResult> CompareAsync(
        string inputDirectory,
        GameManifest baseManifest,
        CancellationToken cancellationToken = default)
    {
        _progress?.Report("Scanning input directory...");

        var result = new BuildComparisonResult
        {
            BaseVersion = baseManifest.Version,
            BaseBuiltAt = baseManifest.BuildDate
        };

        // Build lookup of base files
        var baseFiles = baseManifest.Files.ToDictionary(
            f => NormalizePath(f.Path),
            f => f,
            StringComparer.OrdinalIgnoreCase);

        // Scan input directory
        var inputFiles = Directory.GetFiles(inputDirectory, "*", SearchOption.AllDirectories);
        var processedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var inputFile in inputFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = GetRelativePath(inputDirectory, inputFile);
            var normalizedPath = NormalizePath(relativePath);
            processedPaths.Add(normalizedPath);

            var fileInfo = new System.IO.FileInfo(inputFile);
            var currentHash = await ComputeHashAsync(inputFile, cancellationToken);

            if (baseFiles.TryGetValue(normalizedPath, out var baseFile))
            {
                // File exists in both versions
                if (currentHash == baseFile.Hash)
                {
                    result.UnchangedFiles.Add(new FileComparisonEntry
                    {
                        Path = relativePath,
                        CurrentSize = fileInfo.Length,
                        BaseSize = baseFile.Size,
                        CurrentHash = currentHash,
                        BaseHash = baseFile.Hash,
                        ChangeType = FileChangeType.Unchanged,
                        BaseStrategy = baseFile.Strategy,
                        SuggestedStrategy = baseFile.Strategy
                    });
                }
                else
                {
                    result.ModifiedFiles.Add(new FileComparisonEntry
                    {
                        Path = relativePath,
                        CurrentSize = fileInfo.Length,
                        BaseSize = baseFile.Size,
                        CurrentHash = currentHash,
                        BaseHash = baseFile.Hash,
                        ChangeType = FileChangeType.Modified,
                        BaseStrategy = baseFile.Strategy,
                        SuggestedStrategy = SuggestStrategy(relativePath, fileInfo.Length, baseFile.Strategy)
                    });
                }
            }
            else
            {
                // New file
                result.NewFiles.Add(new FileComparisonEntry
                {
                    Path = relativePath,
                    CurrentSize = fileInfo.Length,
                    BaseSize = null,
                    CurrentHash = currentHash,
                    BaseHash = null,
                    ChangeType = FileChangeType.New,
                    BaseStrategy = null,
                    SuggestedStrategy = SuggestStrategyForNew(relativePath, fileInfo.Length)
                });
            }
        }

        // Find deleted files (in base but not in input)
        foreach (var baseFile in baseManifest.Files)
        {
            var normalizedPath = NormalizePath(baseFile.Path);
            if (!processedPaths.Contains(normalizedPath))
            {
                result.DeletedFiles.Add(new FileComparisonEntry
                {
                    Path = baseFile.Path,
                    CurrentSize = null,
                    BaseSize = baseFile.Size,
                    CurrentHash = null,
                    BaseHash = baseFile.Hash,
                    ChangeType = FileChangeType.Deleted,
                    BaseStrategy = baseFile.Strategy,
                    SuggestedStrategy = UpdateStrategy.Delete
                });
            }
        }

        _progress?.Report($"Comparison complete: {result.NewFiles.Count} new, {result.ModifiedFiles.Count} modified, {result.DeletedFiles.Count} deleted, {result.UnchangedFiles.Count} unchanged");

        return result;
    }

    /// <summary>
    /// Suggests an update strategy for a modified file.
    /// </summary>
    private static UpdateStrategy SuggestStrategy(string path, long size, UpdateStrategy? baseStrategy)
    {
        // Preserve certain strategies from base
        if (baseStrategy == UpdateStrategy.CreateOnly ||
            baseStrategy == UpdateStrategy.UpdateIfNotModified)
        {
            return baseStrategy.Value;
        }

        return SuggestStrategyForNew(path, size);
    }

    /// <summary>
    /// Suggests an update strategy for a new file.
    /// </summary>
    private static UpdateStrategy SuggestStrategyForNew(string path, long size)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();

        // Compressed media - always download compressed
        if (IsCompressedMedia(ext))
            return UpdateStrategy.AlwaysCompressed;

        // Container formats - virtual delta
        if (IsContainerFormat(ext))
            return UpdateStrategy.VirtualDelta;

        // Small files - hash check
        if (size < 64 * 1024)
            return UpdateStrategy.HashCheck;

        // Default to delta
        return UpdateStrategy.Delta;
    }

    private static bool IsCompressedMedia(string ext)
    {
        return ext is ".mp3" or ".ogg" or ".flac" or ".mp4" or ".webm" or ".avi" or ".mkv"
            or ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp"
            or ".zip" or ".7z" or ".rar" or ".gz" or ".zst" or ".xz";
    }

    private static bool IsContainerFormat(string ext)
    {
        return ext is ".uop" or ".pak" or ".vpk" or ".bsa" or ".ba2" or ".rpf"
            or ".u" or ".upk" or ".uasset" or ".ucas" or ".utoc";
    }

    private static async Task<string> ComputeHashAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GetRelativePath(string basePath, string fullPath)
    {
        var relative = Path.GetRelativePath(basePath, fullPath);
        return relative.Replace('\\', '/');
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').ToLowerInvariant();
    }
}
