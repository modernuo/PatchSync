using System.Text.Json;
using PatchSync.CLI.Build;
using PatchSync.CLI.Workspace;
using PatchSync.Common.Manifest;

namespace PatchSync.CLI.Tests;

/// <summary>
/// Tests for FileOperationsService which manages files within built versions.
/// </summary>
public class FileOperationsServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WorkspaceManager _workspace;
    private readonly FileOperationsService _service;

    public FileOperationsServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"patchsync_fileops_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _workspace = WorkspaceManager.ForPath(_tempDir);
        _workspace.CreateDirectoryStructure();
        _service = new FileOperationsService(_workspace);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    #region ListFilesAsync Tests

    [Fact]
    public async Task ListFilesAsync_ReturnsAllFiles()
    {
        // Arrange
        await SetupVersionWithFilesAsync("prod", "1.0.0",
            ("file1.txt", 100, "hash1", UpdateStrategy.Delta),
            ("file2.dll", 5000, "hash2", UpdateStrategy.HashCheck),
            ("data.pak", 100000, "hash3", UpdateStrategy.VirtualDelta));

        // Act
        var files = await _service.ListFilesAsync("prod", "1.0.0");

        // Assert
        Assert.Equal(3, files.Count);
        Assert.Contains(files, f => f.Path == "file1.txt" && f.Strategy == UpdateStrategy.Delta);
        Assert.Contains(files, f => f.Path == "file2.dll" && f.Strategy == UpdateStrategy.HashCheck);
        Assert.Contains(files, f => f.Path == "data.pak" && f.Strategy == UpdateStrategy.VirtualDelta);
    }

    [Fact]
    public async Task ListFilesAsync_ReflectsOverrides()
    {
        // Arrange
        await SetupVersionWithFilesAsync("prod", "1.0.0",
            ("config.ini", 100, "hash1", UpdateStrategy.Delta));

        // Apply an override
        await _service.SetStrategyAsync("prod", "1.0.0", "config.ini", UpdateStrategy.CreateOnly, "User setting");

        // Act
        var files = await _service.ListFilesAsync("prod", "1.0.0");

        // Assert
        var configFile = files.Single(f => f.Path == "config.ini");
        Assert.Equal(UpdateStrategy.CreateOnly, configFile.Strategy);
        Assert.True(configFile.IsOverride);
        Assert.Equal("User setting", configFile.OverrideReason);
    }

    [Fact]
    public async Task ListFilesAsync_ThrowsWhenManifestNotFound()
    {
        // Arrange
        _workspace.CreateChannelStructure("prod");
        _workspace.CreateVersionStructure("prod", "1.0.0");
        // Don't create manifest

        // Act & Assert
        await Assert.ThrowsAsync<WorkspaceException>(() =>
            _service.ListFilesAsync("prod", "1.0.0"));
    }

    #endregion

    #region SetStrategyAsync Tests

    [Fact]
    public async Task SetStrategyAsync_ChangesStrategy()
    {
        // Arrange
        await SetupVersionWithFilesAsync("prod", "1.0.0",
            ("game.exe", 10000, "hash1", UpdateStrategy.Delta));

        // Act
        var result = await _service.SetStrategyAsync("prod", "1.0.0", "game.exe", UpdateStrategy.AlwaysCompressed);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(UpdateStrategy.Delta, result.PreviousStrategy);
        Assert.Equal(UpdateStrategy.AlwaysCompressed, result.NewStrategy);
    }

    [Fact]
    public async Task SetStrategyAsync_CreatesOverrideInMetadata()
    {
        // Arrange
        await SetupVersionWithFilesAsync("prod", "1.0.0",
            ("file.txt", 100, "hash1", UpdateStrategy.Delta));

        // Act
        await _service.SetStrategyAsync("prod", "1.0.0", "file.txt", UpdateStrategy.HashCheck, "Performance");

        // Assert
        var metadata = await _workspace.LoadVersionMetadataAsync("prod", "1.0.0");
        Assert.NotNull(metadata?.FileOverrides);
        Assert.Contains("file.txt", metadata.FileOverrides.Keys);
        Assert.Equal(UpdateStrategy.HashCheck, metadata.FileOverrides["file.txt"].Strategy);
        Assert.Equal("Performance", metadata.FileOverrides["file.txt"].Reason);
    }

    [Fact]
    public async Task SetStrategyAsync_SetsNeedsRescanForDeltaChange()
    {
        // Arrange
        await SetupVersionWithFilesAsync("prod", "1.0.0",
            ("data.bin", 10000, "hash1", UpdateStrategy.Delta));

        // Act - Change from Delta to non-Delta
        await _service.SetStrategyAsync("prod", "1.0.0", "data.bin", UpdateStrategy.AlwaysCompressed);

        // Assert
        var metadata = await _workspace.LoadVersionMetadataAsync("prod", "1.0.0");
        Assert.True(metadata!.FileOverrides!["data.bin"].NeedsRescan);
    }

    [Fact]
    public async Task SetStrategyAsync_NoRescanForNonDeltaChange()
    {
        // Arrange
        await SetupVersionWithFilesAsync("prod", "1.0.0",
            ("small.txt", 100, "hash1", UpdateStrategy.HashCheck));

        // Act - Change between non-Delta strategies
        await _service.SetStrategyAsync("prod", "1.0.0", "small.txt", UpdateStrategy.CreateOnly);

        // Assert
        var metadata = await _workspace.LoadVersionMetadataAsync("prod", "1.0.0");
        Assert.False(metadata!.FileOverrides!["small.txt"].NeedsRescan);
    }

    [Fact]
    public async Task SetStrategyAsync_FailsForMissingFile()
    {
        // Arrange
        await SetupVersionWithFilesAsync("prod", "1.0.0",
            ("existing.txt", 100, "hash1", UpdateStrategy.Delta));

        // Act
        var result = await _service.SetStrategyAsync("prod", "1.0.0", "nonexistent.txt", UpdateStrategy.HashCheck);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("not found", result.Message);
    }

    [Fact]
    public async Task SetStrategyAsync_CaseInsensitivePathMatch()
    {
        // Arrange
        await SetupVersionWithFilesAsync("prod", "1.0.0",
            ("Data/Config.ini", 100, "hash1", UpdateStrategy.Delta));

        // Act
        var result = await _service.SetStrategyAsync("prod", "1.0.0", "data/config.ini", UpdateStrategy.CreateOnly);

        // Assert
        Assert.True(result.Success);
    }

    #endregion

    #region SetStrategyBatchAsync Tests

    [Fact]
    public async Task SetStrategyBatchAsync_ChangesMultipleFiles()
    {
        // Arrange
        await SetupVersionWithFilesAsync("prod", "1.0.0",
            ("textures/tex1.png", 1000, "hash1", UpdateStrategy.Delta),
            ("textures/tex2.png", 2000, "hash2", UpdateStrategy.Delta),
            ("models/model.obj", 5000, "hash3", UpdateStrategy.Delta));

        // Act - Change all png files
        var result = await _service.SetStrategyBatchAsync("prod", "1.0.0", "*.png", UpdateStrategy.AlwaysCompressed);

        // Assert
        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(0, result.FailedCount);
    }

    [Fact]
    public async Task SetStrategyBatchAsync_DirectoryPattern()
    {
        // Arrange
        await SetupVersionWithFilesAsync("prod", "1.0.0",
            ("textures/tex1.png", 1000, "hash1", UpdateStrategy.Delta),
            ("textures/tex2.jpg", 2000, "hash2", UpdateStrategy.Delta),
            ("models/model.obj", 5000, "hash3", UpdateStrategy.Delta));

        // Act - Change all files in textures directory
        var result = await _service.SetStrategyBatchAsync("prod", "1.0.0", "textures/*", UpdateStrategy.AlwaysCompressed);

        // Assert
        Assert.Equal(2, result.SuccessCount);
    }

    #endregion

    #region RemoveOverrideAsync Tests

    [Fact]
    public async Task RemoveOverrideAsync_RemovesExistingOverride()
    {
        // Arrange
        await SetupVersionWithFilesAsync("prod", "1.0.0",
            ("file.txt", 100, "hash1", UpdateStrategy.Delta));
        await _service.SetStrategyAsync("prod", "1.0.0", "file.txt", UpdateStrategy.CreateOnly);

        // Act
        var result = await _service.RemoveOverrideAsync("prod", "1.0.0", "file.txt");

        // Assert
        Assert.True(result.Success);
        Assert.Equal(UpdateStrategy.CreateOnly, result.PreviousStrategy);
    }

    [Fact]
    public async Task RemoveOverrideAsync_FailsIfNoOverride()
    {
        // Arrange
        await SetupVersionWithFilesAsync("prod", "1.0.0",
            ("file.txt", 100, "hash1", UpdateStrategy.Delta));

        // Act
        var result = await _service.RemoveOverrideAsync("prod", "1.0.0", "file.txt");

        // Assert
        Assert.False(result.Success);
        Assert.Contains("No override", result.Message);
    }

    #endregion

    #region AddFileAsync Tests

    [Fact]
    public async Task AddFileAsync_AddsNewFileToManifest()
    {
        // Arrange
        await SetupVersionWithFilesAsync("prod", "1.0.0",
            ("existing.txt", 100, "hash1", UpdateStrategy.Delta));

        var sourceFile = Path.Combine(_tempDir, "source", "newfile.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        await File.WriteAllTextAsync(sourceFile, "new file content");

        // Act
        var result = await _service.AddFileAsync("prod", "1.0.0", sourceFile, "newfile.txt");

        // Assert
        Assert.True(result.Success);

        var files = await _service.ListFilesAsync("prod", "1.0.0");
        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => f.Path == "newfile.txt");
    }

    [Fact]
    public async Task AddFileAsync_CopiesFileToVersionDirectory()
    {
        // Arrange
        await SetupVersionWithFilesAsync("prod", "1.0.0");

        var sourceFile = Path.Combine(_tempDir, "source", "copy.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        await File.WriteAllTextAsync(sourceFile, "content to copy");

        // Act
        await _service.AddFileAsync("prod", "1.0.0", sourceFile, "copy.txt");

        // Assert
        var versionPath = _workspace.GetVersionPath("prod", "1.0.0");
        var copiedFile = Path.Combine(versionPath, "files", "copy.txt");
        Assert.True(File.Exists(copiedFile));
    }

    [Fact]
    public async Task AddFileAsync_FailsIfFileAlreadyExists()
    {
        // Arrange
        await SetupVersionWithFilesAsync("prod", "1.0.0",
            ("existing.txt", 100, "hash1", UpdateStrategy.Delta));

        var sourceFile = Path.Combine(_tempDir, "source", "existing.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        await File.WriteAllTextAsync(sourceFile, "new content");

        // Act
        var result = await _service.AddFileAsync("prod", "1.0.0", sourceFile, "existing.txt");

        // Assert
        Assert.False(result.Success);
        Assert.Contains("already exists", result.Message);
    }

    [Fact]
    public async Task AddFileAsync_FailsIfSourceNotFound()
    {
        // Arrange
        await SetupVersionWithFilesAsync("prod", "1.0.0");

        // Act
        var result = await _service.AddFileAsync("prod", "1.0.0", "/nonexistent/file.txt");

        // Assert
        Assert.False(result.Success);
        Assert.Contains("not found", result.Message);
    }

    [Fact]
    public async Task AddFileAsync_UsesSpecifiedStrategy()
    {
        // Arrange
        await SetupVersionWithFilesAsync("prod", "1.0.0");

        var sourceFile = Path.Combine(_tempDir, "source", "newfile.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        await File.WriteAllTextAsync(sourceFile, "content");

        // Act
        await _service.AddFileAsync("prod", "1.0.0", sourceFile, "newfile.txt", UpdateStrategy.CreateOnly);

        // Assert
        var files = await _service.ListFilesAsync("prod", "1.0.0");
        var newFile = files.Single(f => f.Path == "newfile.txt");
        Assert.Equal(UpdateStrategy.CreateOnly, newFile.Strategy);
    }

    #endregion

    #region RemoveFileAsync Tests

    [Fact]
    public async Task RemoveFileAsync_RemovesFromManifest()
    {
        // Arrange
        await SetupVersionWithFilesAsync("prod", "1.0.0",
            ("keep.txt", 100, "hash1", UpdateStrategy.Delta),
            ("remove.txt", 200, "hash2", UpdateStrategy.Delta));

        // Act
        var result = await _service.RemoveFileAsync("prod", "1.0.0", "remove.txt", deletePhysicalFile: false);

        // Assert
        Assert.True(result.Success);

        var files = await _service.ListFilesAsync("prod", "1.0.0");
        Assert.Single(files);
        Assert.DoesNotContain(files, f => f.Path == "remove.txt");
    }

    [Fact]
    public async Task RemoveFileAsync_DeletesPhysicalFileWhenRequested()
    {
        // Arrange
        await SetupVersionWithFilesAsync("prod", "1.0.0",
            ("toremove.txt", 100, "hash1", UpdateStrategy.Delta));

        var versionPath = _workspace.GetVersionPath("prod", "1.0.0");
        var filesDir = Path.Combine(versionPath, "files");
        Directory.CreateDirectory(filesDir);
        var physicalFile = Path.Combine(filesDir, "toremove.txt");
        await File.WriteAllTextAsync(physicalFile, "content");

        // Act
        await _service.RemoveFileAsync("prod", "1.0.0", "toremove.txt", deletePhysicalFile: true);

        // Assert
        Assert.False(File.Exists(physicalFile));
    }

    [Fact]
    public async Task RemoveFileAsync_RemovesOverride()
    {
        // Arrange
        await SetupVersionWithFilesAsync("prod", "1.0.0",
            ("file.txt", 100, "hash1", UpdateStrategy.Delta));
        await _service.SetStrategyAsync("prod", "1.0.0", "file.txt", UpdateStrategy.CreateOnly);

        // Act
        await _service.RemoveFileAsync("prod", "1.0.0", "file.txt", deletePhysicalFile: false);

        // Assert
        var metadata = await _workspace.LoadVersionMetadataAsync("prod", "1.0.0");
        Assert.DoesNotContain("file.txt", metadata!.FileOverrides!.Keys);
    }

    [Fact]
    public async Task RemoveFileAsync_FailsForNonexistentFile()
    {
        // Arrange
        await SetupVersionWithFilesAsync("prod", "1.0.0",
            ("existing.txt", 100, "hash1", UpdateStrategy.Delta));

        // Act
        var result = await _service.RemoveFileAsync("prod", "1.0.0", "nonexistent.txt");

        // Assert
        Assert.False(result.Success);
        Assert.Contains("not found", result.Message);
    }

    #endregion

    #region GetFilesNeedingRescanAsync Tests

    [Fact]
    public async Task GetFilesNeedingRescanAsync_ReturnsFilesWithRescanFlag()
    {
        // Arrange
        await SetupVersionWithFilesAsync("prod", "1.0.0",
            ("delta1.dat", 10000, "hash1", UpdateStrategy.Delta),
            ("delta2.dat", 20000, "hash2", UpdateStrategy.Delta),
            ("small.txt", 100, "hash3", UpdateStrategy.HashCheck));

        // Change delta files to non-delta (should need rescan)
        await _service.SetStrategyAsync("prod", "1.0.0", "delta1.dat", UpdateStrategy.AlwaysCompressed);

        // Act
        var needsRescan = await _service.GetFilesNeedingRescanAsync("prod", "1.0.0");

        // Assert
        Assert.Single(needsRescan);
        Assert.Contains("delta1.dat", needsRescan);
    }

    #endregion

    #region ApplyOverridesToManifestAsync Tests

    [Fact]
    public async Task ApplyOverridesToManifestAsync_UpdatesManifestWithOverrides()
    {
        // Arrange
        await SetupVersionWithFilesAsync("prod", "1.0.0",
            ("file1.txt", 100, "hash1", UpdateStrategy.Delta),
            ("file2.txt", 200, "hash2", UpdateStrategy.Delta));

        await _service.SetStrategyAsync("prod", "1.0.0", "file1.txt", UpdateStrategy.CreateOnly);
        await _service.SetStrategyAsync("prod", "1.0.0", "file2.txt", UpdateStrategy.HashCheck);

        // Act
        await _service.ApplyOverridesToManifestAsync("prod", "1.0.0");

        // Assert - Read manifest directly to verify changes
        var manifestPath = _workspace.GetManifestPath("prod", "1.0.0");
        await using var stream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync(stream, ManifestJsonContext.Default.GameManifest);

        Assert.NotNull(manifest);
        var file1 = manifest.Files.Single(f => f.Path == "file1.txt");
        var file2 = manifest.Files.Single(f => f.Path == "file2.txt");

        Assert.Equal(UpdateStrategy.CreateOnly, file1.Strategy);
        Assert.True(file1.IsStrategyOverride);
        Assert.Equal(UpdateStrategy.HashCheck, file2.Strategy);
        Assert.True(file2.IsStrategyOverride);
    }

    #endregion

    #region Helper Methods

    private async Task SetupVersionWithFilesAsync(
        string channel,
        string version,
        params (string path, long size, string hash, UpdateStrategy strategy)[] files)
    {
        _workspace.CreateChannelStructure(channel);
        _workspace.CreateVersionStructure(channel, version);

        // Create manifest
        var manifest = new GameManifest
        {
            Version = version,
            BuildDate = DateTime.UtcNow,
            SupportedAlgorithms = ["fastcdc-v1"],
            PreferredAlgorithm = "fastcdc-v1",
            BaseUrl = "https://cdn.example.com/",
            Files = files.Select(f => new ManifestFile
            {
                Path = f.path,
                Size = f.size,
                Hash = f.hash,
                Strategy = f.strategy
            }).ToList()
        };

        var manifestPath = _workspace.GetManifestPath(channel, version);
        await using var stream = File.Create(manifestPath);
        await JsonSerializer.SerializeAsync(stream, manifest, ManifestJsonContext.Default.GameManifest);
        await stream.DisposeAsync();

        // Create metadata
        var metadata = new VersionMetadata
        {
            Version = version,
            Channel = channel,
            Status = VersionStatus.Staged,
            Build = new BuildInfo
            {
                BuiltAt = DateTime.UtcNow,
                BuiltBy = "test"
            },
            Input = new InputInfo
            {
                Path = "/test",
                FileCount = files.Length,
                TotalSize = files.Sum(f => f.size)
            },
            Output = new OutputInfo
            {
                SignatureCount = files.Count(f => f.strategy == UpdateStrategy.Delta)
            },
            Chunking = new ChunkingInfo
            {
                Algorithm = "fastcdc-v1",
                MinChunkSize = 4096,
                AvgChunkSize = 16384,
                MaxChunkSize = 65536
            },
            FileOverrides = new Dictionary<string, FileOverride>()
        };

        await _workspace.SaveVersionMetadataAsync(channel, version, metadata);
    }

    #endregion
}
