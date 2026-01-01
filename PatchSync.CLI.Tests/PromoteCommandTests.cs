using System.Text.Json;
using PatchSync.CLI.Workspace;
using PatchSync.Common.Manifest;

namespace PatchSync.CLI.Tests;

/// <summary>
/// Tests for version promotion functionality.
/// Tests the core promotion logic through workspace operations.
/// </summary>
public class PromoteCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WorkspaceManager _workspace;

    public PromoteCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"patchsync_promote_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _workspace = WorkspaceManager.ForPath(_tempDir);
        _workspace.CreateDirectoryStructure();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task VersionPromotion_CopiesAllFiles()
    {
        // Arrange
        await SetupSourceVersionAsync("beta", "1.0.0-beta.1");

        // Create some files in source
        var sourceSignaturesPath = _workspace.GetSignaturesPath("beta", "1.0.0-beta.1");
        await File.WriteAllTextAsync(Path.Combine(sourceSignaturesPath, "file1.sig"), "sig content 1");
        await File.WriteAllTextAsync(Path.Combine(sourceSignaturesPath, "file2.sig"), "sig content 2");

        // Act - Simulate promotion by copying
        _workspace.CreateChannelStructure("prod");
        _workspace.CreateVersionStructure("prod", "1.0.0");

        var targetSignaturesPath = _workspace.GetSignaturesPath("prod", "1.0.0");
        CopyDirectory(sourceSignaturesPath, targetSignaturesPath);

        // Assert
        Assert.True(File.Exists(Path.Combine(targetSignaturesPath, "file1.sig")));
        Assert.True(File.Exists(Path.Combine(targetSignaturesPath, "file2.sig")));
    }

    [Fact]
    public async Task VersionPromotion_UpdatesManifestVersion()
    {
        // Arrange
        await SetupSourceVersionAsync("beta", "1.0.0-beta.1");

        // Act - Copy and update manifest
        _workspace.CreateChannelStructure("prod");
        _workspace.CreateVersionStructure("prod", "1.0.0");

        var sourceManifestPath = _workspace.GetManifestPath("beta", "1.0.0-beta.1");
        var targetManifestPath = _workspace.GetManifestPath("prod", "1.0.0");

        // Read source, modify version, write to target
        GameManifest manifest;
        {
            await using var readStream = File.OpenRead(sourceManifestPath);
            manifest = (await JsonSerializer.DeserializeAsync(readStream, ManifestJsonContext.Default.GameManifest))!;
        }

        var updatedManifest = new GameManifest
        {
            Version = "1.0.0", // Updated version
            BuildDate = manifest.BuildDate,
            SupportedAlgorithms = manifest.SupportedAlgorithms,
            PreferredAlgorithm = manifest.PreferredAlgorithm,
            BaseUrl = manifest.BaseUrl,
            Files = manifest.Files
        };

        {
            await using var writeStream = File.Create(targetManifestPath);
            await JsonSerializer.SerializeAsync(writeStream, updatedManifest, ManifestJsonContext.Default.GameManifest);
        }

        // Assert
        await using var verifyStream = File.OpenRead(targetManifestPath);
        var savedManifest = await JsonSerializer.DeserializeAsync(verifyStream, ManifestJsonContext.Default.GameManifest);
        Assert.Equal("1.0.0", savedManifest!.Version);
    }

    [Fact]
    public async Task VersionPromotion_CreatesMetadataWithPromotedFrom()
    {
        // Arrange
        await SetupSourceVersionAsync("beta", "1.0.0-beta.1");

        // Act
        _workspace.CreateChannelStructure("prod");
        _workspace.CreateVersionStructure("prod", "1.0.0");

        var sourceMetadata = await _workspace.LoadVersionMetadataAsync("beta", "1.0.0-beta.1");

        var targetMetadata = new VersionMetadata
        {
            Version = "1.0.0",
            Channel = "prod",
            Status = VersionStatus.Staged,
            Build = sourceMetadata!.Build,
            Input = sourceMetadata.Input,
            Output = sourceMetadata.Output,
            Chunking = sourceMetadata.Chunking,
            PromotedFrom = new PromotedFromInfo
            {
                SourceChannel = "beta",
                SourceVersion = "1.0.0-beta.1",
                PromotedAt = DateTime.UtcNow,
                PromotedBy = Environment.MachineName
            }
        };

        await _workspace.SaveVersionMetadataAsync("prod", "1.0.0", targetMetadata);

        // Assert
        var savedMetadata = await _workspace.LoadVersionMetadataAsync("prod", "1.0.0");
        Assert.NotNull(savedMetadata?.PromotedFrom);
        Assert.Equal("beta", savedMetadata.PromotedFrom.SourceChannel);
        Assert.Equal("1.0.0-beta.1", savedMetadata.PromotedFrom.SourceVersion);
    }

    [Fact]
    public async Task VersionPromotion_PreservesOriginalBuildInfo()
    {
        // Arrange
        var originalBuildTime = DateTime.UtcNow.AddHours(-2);
        await SetupSourceVersionAsync("beta", "1.0.0-beta.1", builtAt: originalBuildTime);

        // Act
        _workspace.CreateChannelStructure("prod");
        _workspace.CreateVersionStructure("prod", "1.0.0");

        var sourceMetadata = await _workspace.LoadVersionMetadataAsync("beta", "1.0.0-beta.1");

        var targetMetadata = new VersionMetadata
        {
            Version = "1.0.0",
            Channel = "prod",
            Status = VersionStatus.Staged,
            Build = new BuildInfo
            {
                BuiltAt = sourceMetadata!.Build.BuiltAt,
                BuiltBy = sourceMetadata.Build.BuiltBy,
                Duration = sourceMetadata.Build.Duration,
                PatchSyncVersion = sourceMetadata.Build.PatchSyncVersion
            },
            Input = sourceMetadata.Input,
            Output = sourceMetadata.Output,
            Chunking = sourceMetadata.Chunking
        };

        await _workspace.SaveVersionMetadataAsync("prod", "1.0.0", targetMetadata);

        // Assert
        var savedMetadata = await _workspace.LoadVersionMetadataAsync("prod", "1.0.0");
        Assert.Equal(originalBuildTime, savedMetadata!.Build.BuiltAt);
    }

    [Fact]
    public async Task VersionPromotion_UpdatesChannelState()
    {
        // Arrange
        await SetupSourceVersionAsync("beta", "1.0.0-beta.1");

        // Act
        _workspace.CreateChannelStructure("prod");
        _workspace.CreateVersionStructure("prod", "1.0.0");

        var sourceMetadata = await _workspace.LoadVersionMetadataAsync("beta", "1.0.0-beta.1");

        var channelState = new ChannelState
        {
            ChannelId = "prod",
            DisplayName = "Production",
            History =
            [
                new VersionHistoryEntry
                {
                    Version = "1.0.0",
                    Status = VersionStatus.Staged,
                    BuiltAt = sourceMetadata!.Build.BuiltAt,
                    FileCount = sourceMetadata.Input.FileCount,
                    TotalSize = sourceMetadata.Input.TotalSize
                }
            ]
        };

        await _workspace.SaveChannelStateAsync("prod", channelState);

        // Assert
        var savedState = await _workspace.LoadChannelStateAsync("prod");
        Assert.NotNull(savedState);
        Assert.Single(savedState.History);
        Assert.Equal("1.0.0", savedState.History[0].Version);
        Assert.Equal(VersionStatus.Staged, savedState.History[0].Status);
    }

    [Fact]
    public async Task VersionPromotion_WithSameVersionName()
    {
        // Arrange - Promote 1.0.0-beta.1 as 1.0.0-beta.1 to prod (same name)
        await SetupSourceVersionAsync("beta", "1.0.0-beta.1");

        // Act
        _workspace.CreateChannelStructure("prod");
        _workspace.CreateVersionStructure("prod", "1.0.0-beta.1");

        var sourceMetadata = await _workspace.LoadVersionMetadataAsync("beta", "1.0.0-beta.1");

        var targetMetadata = new VersionMetadata
        {
            Version = "1.0.0-beta.1", // Same version string
            Channel = "prod",
            Status = VersionStatus.Staged,
            Build = sourceMetadata!.Build,
            Input = sourceMetadata.Input,
            Output = sourceMetadata.Output,
            Chunking = sourceMetadata.Chunking,
            PromotedFrom = new PromotedFromInfo
            {
                SourceChannel = "beta",
                SourceVersion = "1.0.0-beta.1",
                PromotedAt = DateTime.UtcNow,
                PromotedBy = "test"
            }
        };

        await _workspace.SaveVersionMetadataAsync("prod", "1.0.0-beta.1", targetMetadata);

        // Assert
        Assert.True(_workspace.VersionExists("prod", "1.0.0-beta.1"));
        var savedMetadata = await _workspace.LoadVersionMetadataAsync("prod", "1.0.0-beta.1");
        Assert.Equal("prod", savedMetadata!.Channel);
    }

    [Fact]
    public async Task PromotedFromInfo_SerializesCorrectly()
    {
        // Arrange
        var promotedFrom = new PromotedFromInfo
        {
            SourceChannel = "beta",
            SourceVersion = "2.0.0-rc1",
            PromotedAt = DateTime.UtcNow,
            PromotedBy = "build-server-01"
        };

        var metadata = new VersionMetadata
        {
            Version = "2.0.0",
            Channel = "prod",
            Status = VersionStatus.Staged,
            Build = new BuildInfo { BuiltAt = DateTime.UtcNow, BuiltBy = "test" },
            Input = new InputInfo { Path = "/test", FileCount = 10, TotalSize = 1000 },
            Output = new OutputInfo { SignatureCount = 5 },
            Chunking = new ChunkingInfo { Algorithm = "fastcdc-v1" },
            PromotedFrom = promotedFrom
        };

        // Act
        _workspace.CreateChannelStructure("prod");
        _workspace.CreateVersionStructure("prod", "2.0.0");
        await _workspace.SaveVersionMetadataAsync("prod", "2.0.0", metadata);
        var loaded = await _workspace.LoadVersionMetadataAsync("prod", "2.0.0");

        // Assert
        Assert.NotNull(loaded?.PromotedFrom);
        Assert.Equal("beta", loaded.PromotedFrom.SourceChannel);
        Assert.Equal("2.0.0-rc1", loaded.PromotedFrom.SourceVersion);
        Assert.Equal("build-server-01", loaded.PromotedFrom.PromotedBy);
    }

    [Fact]
    public async Task BasedOnInfo_SerializesCorrectly()
    {
        // Arrange
        var basedOn = new BasedOnInfo
        {
            Channel = "prod",
            Version = "1.5.0",
            BaseBuiltAt = DateTime.UtcNow.AddDays(-7)
        };

        var metadata = new VersionMetadata
        {
            Version = "1.6.0",
            Channel = "prod",
            Status = VersionStatus.Staged,
            Build = new BuildInfo { BuiltAt = DateTime.UtcNow, BuiltBy = "test" },
            Input = new InputInfo { Path = "/test", FileCount = 10, TotalSize = 1000 },
            Output = new OutputInfo { SignatureCount = 5 },
            Chunking = new ChunkingInfo { Algorithm = "fastcdc-v1" },
            BasedOn = basedOn
        };

        // Act
        _workspace.CreateChannelStructure("prod");
        _workspace.CreateVersionStructure("prod", "1.6.0");
        await _workspace.SaveVersionMetadataAsync("prod", "1.6.0", metadata);
        var loaded = await _workspace.LoadVersionMetadataAsync("prod", "1.6.0");

        // Assert
        Assert.NotNull(loaded?.BasedOn);
        Assert.Equal("prod", loaded.BasedOn.Channel);
        Assert.Equal("1.5.0", loaded.BasedOn.Version);
    }

    [Fact]
    public async Task FileOverrides_SerializeCorrectly()
    {
        // Arrange
        var overrides = new Dictionary<string, FileOverride>
        {
            ["config/settings.ini"] = new FileOverride
            {
                Strategy = UpdateStrategy.CreateOnly,
                SetAt = DateTime.UtcNow,
                Reason = "User configuration file",
                NeedsRescan = false
            },
            ["data/large.pak"] = new FileOverride
            {
                Strategy = UpdateStrategy.AlwaysCompressed,
                SetAt = DateTime.UtcNow,
                Reason = "Performance optimization",
                NeedsRescan = true
            }
        };

        var metadata = new VersionMetadata
        {
            Version = "1.0.0",
            Channel = "prod",
            Status = VersionStatus.Staged,
            Build = new BuildInfo { BuiltAt = DateTime.UtcNow, BuiltBy = "test" },
            Input = new InputInfo { Path = "/test", FileCount = 10, TotalSize = 1000 },
            Output = new OutputInfo { SignatureCount = 5 },
            Chunking = new ChunkingInfo { Algorithm = "fastcdc-v1" },
            FileOverrides = overrides
        };

        // Act
        _workspace.CreateChannelStructure("prod");
        _workspace.CreateVersionStructure("prod", "1.0.0");
        await _workspace.SaveVersionMetadataAsync("prod", "1.0.0", metadata);
        var loaded = await _workspace.LoadVersionMetadataAsync("prod", "1.0.0");

        // Assert
        Assert.NotNull(loaded?.FileOverrides);
        Assert.Equal(2, loaded.FileOverrides.Count);
        Assert.Equal(UpdateStrategy.CreateOnly, loaded.FileOverrides["config/settings.ini"].Strategy);
        Assert.True(loaded.FileOverrides["data/large.pak"].NeedsRescan);
    }

    [Fact]
    public async Task ComparisonInfo_SerializesCorrectly()
    {
        // Arrange
        var metadata = new VersionMetadata
        {
            Version = "1.1.0",
            Channel = "prod",
            Status = VersionStatus.Staged,
            Build = new BuildInfo { BuiltAt = DateTime.UtcNow, BuiltBy = "test" },
            Input = new InputInfo { Path = "/test", FileCount = 100, TotalSize = 10000 },
            Output = new OutputInfo { SignatureCount = 50 },
            Chunking = new ChunkingInfo { Algorithm = "fastcdc-v1" },
            Comparison = new ComparisonInfo
            {
                NewFiles = 5,
                ModifiedFiles = 20,
                DeletedFiles = 3,
                UnchangedFiles = 72
            }
        };

        // Act
        _workspace.CreateChannelStructure("prod");
        _workspace.CreateVersionStructure("prod", "1.1.0");
        await _workspace.SaveVersionMetadataAsync("prod", "1.1.0", metadata);
        var loaded = await _workspace.LoadVersionMetadataAsync("prod", "1.1.0");

        // Assert
        Assert.NotNull(loaded?.Comparison);
        Assert.Equal(5, loaded.Comparison.NewFiles);
        Assert.Equal(20, loaded.Comparison.ModifiedFiles);
        Assert.Equal(3, loaded.Comparison.DeletedFiles);
        Assert.Equal(72, loaded.Comparison.UnchangedFiles);
    }

    #region Helper Methods

    private async Task SetupSourceVersionAsync(string channel, string version, DateTime? builtAt = null)
    {
        _workspace.CreateChannelStructure(channel);
        _workspace.CreateVersionStructure(channel, version);

        // Create manifest
        var manifest = new GameManifest
        {
            Version = version,
            BuildDate = builtAt ?? DateTime.UtcNow,
            SupportedAlgorithms = ["fastcdc-v1"],
            PreferredAlgorithm = "fastcdc-v1",
            BaseUrl = "https://cdn.example.com/",
            Files =
            [
                new ManifestFile { Path = "game.exe", Size = 10000, Hash = "hash1", Strategy = UpdateStrategy.Delta },
                new ManifestFile { Path = "data.pak", Size = 50000, Hash = "hash2", Strategy = UpdateStrategy.VirtualDelta },
                new ManifestFile { Path = "config.ini", Size = 500, Hash = "hash3", Strategy = UpdateStrategy.HashCheck }
            ]
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
                BuiltAt = builtAt ?? DateTime.UtcNow,
                BuiltBy = "test-machine",
                Duration = 45.5,
                PatchSyncVersion = "1.0.0"
            },
            Input = new InputInfo
            {
                Path = "/test/input",
                FileCount = 3,
                TotalSize = 60500
            },
            Output = new OutputInfo
            {
                SignatureCount = 2,
                SignatureTotalSize = 1000
            },
            Chunking = new ChunkingInfo
            {
                Algorithm = "fastcdc-v1",
                MinChunkSize = 4096,
                AvgChunkSize = 16384,
                MaxChunkSize = 65536
            }
        };

        await _workspace.SaveVersionMetadataAsync(channel, version, metadata);
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var fileName = Path.GetFileName(file);
            File.Copy(file, Path.Combine(targetDir, fileName), overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(dir);
            CopyDirectory(dir, Path.Combine(targetDir, dirName));
        }
    }

    #endregion
}
