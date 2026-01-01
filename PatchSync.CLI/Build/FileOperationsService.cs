using System.Security.Cryptography;
using System.Text.Json;
using PatchSync.CLI.Workspace;
using PatchSync.Common.Manifest;

namespace PatchSync.CLI.Build;

/// <summary>
/// Result of a file operation.
/// </summary>
public sealed class FileOperationResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Path { get; init; }
    public UpdateStrategy? PreviousStrategy { get; init; }
    public UpdateStrategy? NewStrategy { get; init; }
}

/// <summary>
/// Batch operation result for multiple files.
/// </summary>
public sealed class BatchOperationResult
{
    public int SuccessCount { get; init; }
    public int FailedCount { get; init; }
    public List<FileOperationResult> Results { get; init; } = [];
}

/// <summary>
/// File information with strategy and override status.
/// </summary>
public sealed class VersionFileInfo
{
    public required string Path { get; init; }
    public required long Size { get; init; }
    public required string Hash { get; init; }
    public required UpdateStrategy Strategy { get; init; }
    public bool IsOverride { get; init; }
    public string? OverrideReason { get; init; }
    public DateTime? OverrideSetAt { get; init; }
    public string? BaseHash { get; init; }
}

/// <summary>
/// Service for managing files within a built version.
/// Handles adding, removing, and modifying file strategies.
/// </summary>
public sealed class FileOperationsService
{
    private readonly WorkspaceManager _workspace;
    private readonly IProgress<string>? _progress;

    public FileOperationsService(WorkspaceManager workspace, IProgress<string>? progress = null)
    {
        _workspace = workspace;
        _progress = progress;
    }

    /// <summary>
    /// Lists all files in a version with their current strategies.
    /// </summary>
    public async Task<List<VersionFileInfo>> ListFilesAsync(
        string channel,
        string version,
        CancellationToken cancellationToken = default)
    {
        var manifestPath = _workspace.GetManifestPath(channel, version);
        if (!File.Exists(manifestPath))
            throw new WorkspaceException($"Manifest not found for {channel}/{version}");

        await using var stream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync(
            stream,
            ManifestJsonContext.Default.GameManifest,
            cancellationToken)
            ?? throw new WorkspaceException("Failed to load manifest");

        var metadata = await _workspace.LoadVersionMetadataAsync(channel, version, cancellationToken);
        var overrides = metadata?.FileOverrides ?? new Dictionary<string, FileOverride>();

        var files = new List<VersionFileInfo>();
        foreach (var file in manifest.Files)
        {
            var normalizedPath = NormalizePath(file.Path);
            overrides.TryGetValue(normalizedPath, out var fileOverride);

            files.Add(new VersionFileInfo
            {
                Path = file.Path,
                Size = file.Size,
                Hash = file.Hash,
                Strategy = fileOverride?.Strategy ?? file.Strategy,
                IsOverride = file.IsStrategyOverride || fileOverride != null,
                OverrideReason = fileOverride?.Reason,
                OverrideSetAt = fileOverride?.SetAt,
                BaseHash = file.BaseHash
            });
        }

        return files;
    }

    /// <summary>
    /// Sets the update strategy for a specific file.
    /// </summary>
    public async Task<FileOperationResult> SetStrategyAsync(
        string channel,
        string version,
        string filePath,
        UpdateStrategy newStrategy,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(filePath);

        // Load manifest to verify file exists
        var manifestPath = _workspace.GetManifestPath(channel, version);
        if (!File.Exists(manifestPath))
            return new FileOperationResult
            {
                Success = false,
                Message = $"Manifest not found for {channel}/{version}",
                Path = filePath
            };

        await using var stream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync(
            stream,
            ManifestJsonContext.Default.GameManifest,
            cancellationToken);

        if (manifest == null)
            return new FileOperationResult
            {
                Success = false,
                Message = "Failed to load manifest",
                Path = filePath
            };

        var manifestFile = manifest.Files.FirstOrDefault(f =>
            NormalizePath(f.Path).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));

        if (manifestFile == null)
            return new FileOperationResult
            {
                Success = false,
                Message = $"File not found in manifest: {filePath}",
                Path = filePath
            };

        var previousStrategy = manifestFile.Strategy;

        // Load or create metadata
        var metadata = await _workspace.LoadVersionMetadataAsync(channel, version, cancellationToken)
            ?? throw new WorkspaceException($"Version metadata not found for {channel}/{version}");

        metadata.FileOverrides ??= new Dictionary<string, FileOverride>();

        // Add or update override
        var needsRescan = RequiresRescan(previousStrategy, newStrategy);
        metadata.FileOverrides[normalizedPath] = new FileOverride
        {
            Strategy = newStrategy,
            SetAt = DateTime.UtcNow,
            Reason = reason,
            NeedsRescan = needsRescan
        };

        await _workspace.SaveVersionMetadataAsync(channel, version, metadata, cancellationToken);

        _progress?.Report($"Set strategy for {filePath}: {previousStrategy} -> {newStrategy}");

        return new FileOperationResult
        {
            Success = true,
            Message = needsRescan
                ? $"Strategy changed. Rescan required for {filePath}"
                : $"Strategy changed for {filePath}",
            Path = filePath,
            PreviousStrategy = previousStrategy,
            NewStrategy = newStrategy
        };
    }

    /// <summary>
    /// Sets the update strategy for multiple files matching a pattern.
    /// </summary>
    public async Task<BatchOperationResult> SetStrategyBatchAsync(
        string channel,
        string version,
        string pattern,
        UpdateStrategy newStrategy,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var files = await ListFilesAsync(channel, version, cancellationToken);
        var matchingFiles = files.Where(f => MatchesPattern(f.Path, pattern)).ToList();

        var results = new List<FileOperationResult>();
        foreach (var file in matchingFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await SetStrategyAsync(channel, version, file.Path, newStrategy, reason, cancellationToken);
            results.Add(result);
        }

        return new BatchOperationResult
        {
            SuccessCount = results.Count(r => r.Success),
            FailedCount = results.Count(r => !r.Success),
            Results = results
        };
    }

    /// <summary>
    /// Removes a strategy override, reverting to auto-determined strategy.
    /// </summary>
    public async Task<FileOperationResult> RemoveOverrideAsync(
        string channel,
        string version,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(filePath);

        var metadata = await _workspace.LoadVersionMetadataAsync(channel, version, cancellationToken);
        if (metadata?.FileOverrides == null || !metadata.FileOverrides.ContainsKey(normalizedPath))
        {
            return new FileOperationResult
            {
                Success = false,
                Message = $"No override exists for {filePath}",
                Path = filePath
            };
        }

        var previousOverride = metadata.FileOverrides[normalizedPath];
        metadata.FileOverrides.Remove(normalizedPath);

        await _workspace.SaveVersionMetadataAsync(channel, version, metadata, cancellationToken);

        _progress?.Report($"Removed strategy override for {filePath}");

        return new FileOperationResult
        {
            Success = true,
            Message = $"Override removed. Strategy will be auto-determined on next build.",
            Path = filePath,
            PreviousStrategy = previousOverride.Strategy
        };
    }

    /// <summary>
    /// Adds a new file to an existing version.
    /// </summary>
    public async Task<FileOperationResult> AddFileAsync(
        string channel,
        string version,
        string sourcePath,
        string? targetPath = null,
        UpdateStrategy? strategy = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
        {
            return new FileOperationResult
            {
                Success = false,
                Message = $"Source file not found: {sourcePath}",
                Path = sourcePath
            };
        }

        var relativePath = targetPath ?? Path.GetFileName(sourcePath);
        var normalizedPath = NormalizePath(relativePath);

        // Load manifest
        var manifestPath = _workspace.GetManifestPath(channel, version);
        if (!File.Exists(manifestPath))
        {
            return new FileOperationResult
            {
                Success = false,
                Message = $"Manifest not found for {channel}/{version}",
                Path = relativePath
            };
        }

        GameManifest manifest;
        {
            await using var manifestStream = File.OpenRead(manifestPath);
            var loadedManifest = await JsonSerializer.DeserializeAsync(
                manifestStream,
                ManifestJsonContext.Default.GameManifest,
                cancellationToken);

            if (loadedManifest == null)
            {
                return new FileOperationResult
                {
                    Success = false,
                    Message = "Failed to load manifest",
                    Path = relativePath
                };
            }
            manifest = loadedManifest;
        }

        // Check if file already exists
        if (manifest.Files.Any(f => NormalizePath(f.Path).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase)))
        {
            return new FileOperationResult
            {
                Success = false,
                Message = $"File already exists in manifest: {relativePath}",
                Path = relativePath
            };
        }

        // Compute hash
        var sourceFileInfo = new System.IO.FileInfo(sourcePath);
        var hash = await ComputeHashAsync(sourcePath, cancellationToken);

        // Determine strategy
        var selectedStrategy = strategy ?? SuggestStrategy(relativePath, sourceFileInfo.Length);

        // Copy file to version directory
        var versionPath = _workspace.GetVersionPath(channel, version);
        var filesDir = Path.Combine(versionPath, "files");
        Directory.CreateDirectory(filesDir);

        var targetFilePath = Path.Combine(filesDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(targetFilePath)!);
        File.Copy(sourcePath, targetFilePath, overwrite: false);

        // Add to manifest
        var newFiles = manifest.Files.ToList();
        newFiles.Add(new ManifestFile
        {
            Path = relativePath,
            Size = sourceFileInfo.Length,
            Hash = hash,
            Strategy = selectedStrategy,
            IsStrategyOverride = strategy.HasValue
        });

        // Save updated manifest
        var updatedManifest = new GameManifest
        {
            Version = manifest.Version,
            BuildDate = manifest.BuildDate,
            SupportedAlgorithms = manifest.SupportedAlgorithms,
            PreferredAlgorithm = manifest.PreferredAlgorithm,
            FallbackUrl = manifest.FallbackUrl,
            BaseUrl = manifest.BaseUrl,
            Files = newFiles,
            Metadata = manifest.Metadata
        };

        await SaveManifestAsync(manifestPath, updatedManifest, cancellationToken);

        // Update metadata
        var metadata = await _workspace.LoadVersionMetadataAsync(channel, version, cancellationToken);
        if (metadata != null)
        {
            metadata.Output.SignatureCount = newFiles.Count(f =>
                f.Strategy == UpdateStrategy.Delta || f.Strategy == UpdateStrategy.VirtualDelta);
            metadata.Input.FileCount = newFiles.Count;
            metadata.Input.TotalSize = newFiles.Sum(f => f.Size);
            await _workspace.SaveVersionMetadataAsync(channel, version, metadata, cancellationToken);
        }

        _progress?.Report($"Added file: {relativePath} ({FormatSize(sourceFileInfo.Length)})");

        return new FileOperationResult
        {
            Success = true,
            Message = $"File added successfully",
            Path = relativePath,
            NewStrategy = selectedStrategy
        };
    }

    /// <summary>
    /// Removes a file from an existing version.
    /// </summary>
    public async Task<FileOperationResult> RemoveFileAsync(
        string channel,
        string version,
        string filePath,
        bool deletePhysicalFile = true,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(filePath);

        // Load manifest
        var manifestPath = _workspace.GetManifestPath(channel, version);
        if (!File.Exists(manifestPath))
        {
            return new FileOperationResult
            {
                Success = false,
                Message = $"Manifest not found for {channel}/{version}",
                Path = filePath
            };
        }

        GameManifest manifest;
        ManifestFile? existingFile;
        {
            await using var stream = File.OpenRead(manifestPath);
            var loadedManifest = await JsonSerializer.DeserializeAsync(
                stream,
                ManifestJsonContext.Default.GameManifest,
                cancellationToken);

            if (loadedManifest == null)
            {
                return new FileOperationResult
                {
                    Success = false,
                    Message = "Failed to load manifest",
                    Path = filePath
                };
            }
            manifest = loadedManifest;
            existingFile = manifest.Files.FirstOrDefault(f =>
                NormalizePath(f.Path).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));
        }

        if (existingFile == null)
        {
            return new FileOperationResult
            {
                Success = false,
                Message = $"File not found in manifest: {filePath}",
                Path = filePath
            };
        }

        // Remove from manifest
        var newFiles = manifest.Files.Where(f =>
            !NormalizePath(f.Path).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase)).ToList();

        var updatedManifest = new GameManifest
        {
            Version = manifest.Version,
            BuildDate = manifest.BuildDate,
            SupportedAlgorithms = manifest.SupportedAlgorithms,
            PreferredAlgorithm = manifest.PreferredAlgorithm,
            FallbackUrl = manifest.FallbackUrl,
            BaseUrl = manifest.BaseUrl,
            Files = newFiles,
            Metadata = manifest.Metadata
        };

        await SaveManifestAsync(manifestPath, updatedManifest, cancellationToken);

        // Delete physical file if requested
        if (deletePhysicalFile)
        {
            var versionPath = _workspace.GetVersionPath(channel, version);
            var physicalPath = Path.Combine(versionPath, "files", filePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(physicalPath))
            {
                File.Delete(physicalPath);
            }

            // Also delete signature if exists
            var signaturePath = Path.Combine(_workspace.GetSignaturesPath(channel, version), filePath + ".sig");
            if (File.Exists(signaturePath))
            {
                File.Delete(signaturePath);
            }
        }

        // Remove any override
        var metadata = await _workspace.LoadVersionMetadataAsync(channel, version, cancellationToken);
        if (metadata?.FileOverrides != null && metadata.FileOverrides.ContainsKey(normalizedPath))
        {
            metadata.FileOverrides.Remove(normalizedPath);
            await _workspace.SaveVersionMetadataAsync(channel, version, metadata, cancellationToken);
        }

        _progress?.Report($"Removed file: {filePath}");

        return new FileOperationResult
        {
            Success = true,
            Message = deletePhysicalFile
                ? $"File removed from manifest and deleted"
                : $"File removed from manifest (physical file retained)",
            Path = filePath,
            PreviousStrategy = existingFile.Strategy
        };
    }

    /// <summary>
    /// Gets files that need signature regeneration due to strategy changes.
    /// </summary>
    public async Task<List<string>> GetFilesNeedingRescanAsync(
        string channel,
        string version,
        CancellationToken cancellationToken = default)
    {
        var metadata = await _workspace.LoadVersionMetadataAsync(channel, version, cancellationToken);
        if (metadata?.FileOverrides == null)
            return [];

        return metadata.FileOverrides
            .Where(kvp => kvp.Value.NeedsRescan)
            .Select(kvp => kvp.Key)
            .ToList();
    }

    /// <summary>
    /// Clears the rescan flag for specified files.
    /// </summary>
    public async Task ClearRescanFlagsAsync(
        string channel,
        string version,
        IEnumerable<string>? files = null,
        CancellationToken cancellationToken = default)
    {
        var metadata = await _workspace.LoadVersionMetadataAsync(channel, version, cancellationToken);
        if (metadata?.FileOverrides == null)
            return;

        var filesToClear = files?.Select(NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, fileOverride) in metadata.FileOverrides)
        {
            if (filesToClear == null || filesToClear.Contains(path))
            {
                fileOverride.NeedsRescan = false;
            }
        }

        await _workspace.SaveVersionMetadataAsync(channel, version, metadata, cancellationToken);
    }

    /// <summary>
    /// Applies all pending file overrides to the manifest.
    /// Call this before publishing to ensure manifest reflects current strategy settings.
    /// </summary>
    public async Task ApplyOverridesToManifestAsync(
        string channel,
        string version,
        CancellationToken cancellationToken = default)
    {
        var metadata = await _workspace.LoadVersionMetadataAsync(channel, version, cancellationToken);
        if (metadata?.FileOverrides == null || metadata.FileOverrides.Count == 0)
            return;

        var manifestPath = _workspace.GetManifestPath(channel, version);
        GameManifest manifest;
        {
            await using var stream = File.OpenRead(manifestPath);
            var loadedManifest = await JsonSerializer.DeserializeAsync(
                stream,
                ManifestJsonContext.Default.GameManifest,
                cancellationToken);

            if (loadedManifest == null)
                return;
            manifest = loadedManifest;
        }

        var updatedFiles = new List<ManifestFile>();
        foreach (var file in manifest.Files)
        {
            var normalizedPath = NormalizePath(file.Path);
            if (metadata.FileOverrides.TryGetValue(normalizedPath, out var fileOverride))
            {
                updatedFiles.Add(new ManifestFile
                {
                    Path = file.Path,
                    Size = file.Size,
                    Hash = file.Hash,
                    Strategy = fileOverride.Strategy,
                    SignatureUrl = file.SignatureUrl,
                    CompressedSize = file.CompressedSize,
                    CompressedUrl = file.CompressedUrl,
                    FileUrl = file.FileUrl,
                    ContainerFormat = file.ContainerFormat,
                    VirtualSignatureUrl = file.VirtualSignatureUrl,
                    BaseHash = file.BaseHash,
                    IsStrategyOverride = true
                });
            }
            else
            {
                updatedFiles.Add(file);
            }
        }

        var updatedManifest = new GameManifest
        {
            Version = manifest.Version,
            BuildDate = manifest.BuildDate,
            SupportedAlgorithms = manifest.SupportedAlgorithms,
            PreferredAlgorithm = manifest.PreferredAlgorithm,
            FallbackUrl = manifest.FallbackUrl,
            BaseUrl = manifest.BaseUrl,
            Files = updatedFiles,
            Metadata = manifest.Metadata
        };

        await SaveManifestAsync(manifestPath, updatedManifest, cancellationToken);
        _progress?.Report($"Applied {metadata.FileOverrides.Count} strategy overrides to manifest");
    }

    private static bool RequiresRescan(UpdateStrategy previous, UpdateStrategy newStrategy)
    {
        // Changing to/from Delta or VirtualDelta requires signature regeneration
        var deltaStrategies = new[] { UpdateStrategy.Delta, UpdateStrategy.VirtualDelta };
        var wasDelata = deltaStrategies.Contains(previous);
        var isNowDelta = deltaStrategies.Contains(newStrategy);
        return wasDelata != isNowDelta;
    }

    private static bool MatchesPattern(string path, string pattern)
    {
        // Simple glob-like matching
        if (pattern == "*")
            return true;

        if (pattern.StartsWith("*."))
        {
            var ext = pattern[1..];
            return path.EndsWith(ext, StringComparison.OrdinalIgnoreCase);
        }

        if (pattern.EndsWith("/*"))
        {
            var dir = pattern[..^2];
            return path.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase);
        }

        return path.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
               path.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static UpdateStrategy SuggestStrategy(string path, long size)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();

        // Compressed media
        if (ext is ".mp3" or ".ogg" or ".flac" or ".mp4" or ".webm" or ".avi" or ".mkv"
            or ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp"
            or ".zip" or ".7z" or ".rar" or ".gz" or ".zst" or ".xz")
            return UpdateStrategy.AlwaysCompressed;

        // Container formats
        if (ext is ".uop" or ".pak" or ".vpk" or ".bsa" or ".ba2" or ".rpf"
            or ".u" or ".upk" or ".uasset" or ".ucas" or ".utoc")
            return UpdateStrategy.VirtualDelta;

        // Small files
        if (size < 64 * 1024)
            return UpdateStrategy.HashCheck;

        return UpdateStrategy.Delta;
    }

    private static async Task<string> ComputeHashAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task SaveManifestAsync(string path, GameManifest manifest, CancellationToken cancellationToken)
    {
        var tempPath = path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, manifest, ManifestJsonContext.Default.GameManifest, cancellationToken);
        }
        File.Move(tempPath, path, overwrite: true);
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').ToLowerInvariant();
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
