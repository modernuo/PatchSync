using PatchSync.CLI.Workspace;

namespace PatchSync.CLI.Tests;

/// <summary>
/// Tests for WorkspaceManager operations.
/// Uses temporary directories for isolation.
/// </summary>
public class WorkspaceManagerTests : IDisposable
{
    private readonly string _tempDir;

    public WorkspaceManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"patchsync_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void ForPath_CreatesManagerWithCorrectPath()
    {
        // Act
        var manager = WorkspaceManager.ForPath(_tempDir);

        // Assert
        Assert.Equal(Path.GetFullPath(_tempDir), manager.WorkspacePath);
    }

    [Fact]
    public void Exists_ReturnsFalse_WhenNoConfigFile()
    {
        // Arrange
        var manager = WorkspaceManager.ForPath(_tempDir);

        // Act & Assert
        Assert.False(manager.Exists);
    }

    [Fact]
    public async Task SaveConfigAsync_CreatesConfigFile()
    {
        // Arrange
        var manager = WorkspaceManager.ForPath(_tempDir);
        var config = CreateSampleConfig();

        // Act
        await manager.SaveConfigAsync(config);

        // Assert
        Assert.True(File.Exists(manager.ConfigPath));
        Assert.True(manager.Exists);
    }

    [Fact]
    public async Task LoadConfigAsync_ReturnsStoredConfig()
    {
        // Arrange
        var manager = WorkspaceManager.ForPath(_tempDir);
        var config = CreateSampleConfig();
        await manager.SaveConfigAsync(config);

        // Act
        var loaded = await manager.LoadConfigAsync();

        // Assert
        Assert.Equal(config.Project.Name, loaded.Project.Name);
        Assert.Equal(config.Project.Id, loaded.Project.Id);
        Assert.Equal(config.Channels.Count, loaded.Channels.Count);
    }

    [Fact]
    public async Task LoadConfigAsync_ThrowsWhenNotExists()
    {
        // Arrange
        var manager = WorkspaceManager.ForPath(_tempDir);

        // Act & Assert
        await Assert.ThrowsAsync<WorkspaceException>(() => manager.LoadConfigAsync());
    }

    [Fact]
    public void CreateDirectoryStructure_CreatesRequiredDirs()
    {
        // Arrange
        var manager = WorkspaceManager.ForPath(_tempDir);

        // Act
        manager.CreateDirectoryStructure();

        // Assert
        Assert.True(Directory.Exists(manager.InternalPath));
        Assert.True(Directory.Exists(manager.ChannelsPath));
    }

    [Fact]
    public void CreateChannelStructure_CreatesChannelDirs()
    {
        // Arrange
        var manager = WorkspaceManager.ForPath(_tempDir);
        manager.CreateDirectoryStructure();

        // Act
        manager.CreateChannelStructure("prod");

        // Assert
        Assert.True(Directory.Exists(manager.GetChannelPath("prod")));
        Assert.True(Directory.Exists(Path.Combine(manager.GetChannelPath("prod"), "versions")));
    }

    [Fact]
    public void CreateVersionStructure_CreatesVersionDirs()
    {
        // Arrange
        var manager = WorkspaceManager.ForPath(_tempDir);
        manager.CreateDirectoryStructure();
        manager.CreateChannelStructure("prod");

        // Act
        manager.CreateVersionStructure("prod", "1.0.0");

        // Assert
        Assert.True(Directory.Exists(manager.GetVersionPath("prod", "1.0.0")));
        Assert.True(Directory.Exists(manager.GetSignaturesPath("prod", "1.0.0")));
    }

    [Fact]
    public void VersionExists_ReturnsFalse_WhenNotCreated()
    {
        // Arrange
        var manager = WorkspaceManager.ForPath(_tempDir);
        manager.CreateDirectoryStructure();
        manager.CreateChannelStructure("prod");

        // Act & Assert
        Assert.False(manager.VersionExists("prod", "1.0.0"));
    }

    [Fact]
    public void VersionExists_ReturnsTrue_WhenCreated()
    {
        // Arrange
        var manager = WorkspaceManager.ForPath(_tempDir);
        manager.CreateDirectoryStructure();
        manager.CreateChannelStructure("prod");
        manager.CreateVersionStructure("prod", "1.0.0");

        // Act & Assert
        Assert.True(manager.VersionExists("prod", "1.0.0"));
    }

    [Fact]
    public async Task SaveAndLoadVersionMetadata_RoundTrip()
    {
        // Arrange
        var manager = WorkspaceManager.ForPath(_tempDir);
        manager.CreateDirectoryStructure();
        manager.CreateChannelStructure("prod");
        manager.CreateVersionStructure("prod", "1.0.0");

        var metadata = new VersionMetadata
        {
            Version = "1.0.0",
            Channel = "prod",
            Status = VersionStatus.Staged,
            Build = new BuildInfo
            {
                BuiltAt = DateTime.UtcNow,
                BuiltBy = "test-machine",
                Duration = 123.45
            },
            Input = new InputInfo
            {
                Path = "/test/input",
                FileCount = 100,
                TotalSize = 1024 * 1024 * 500
            },
            Output = new OutputInfo
            {
                SignatureCount = 95,
                SignatureTotalSize = 1024 * 100
            },
            Chunking = new ChunkingInfo
            {
                Algorithm = "fastcdc-v1",
                MinChunkSize = 4096,
                AvgChunkSize = 16384,
                MaxChunkSize = 65536,
                TotalChunks = 50000
            }
        };

        // Act
        await manager.SaveVersionMetadataAsync("prod", "1.0.0", metadata);
        var loaded = await manager.LoadVersionMetadataAsync("prod", "1.0.0");

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal("1.0.0", loaded.Version);
        Assert.Equal("prod", loaded.Channel);
        Assert.Equal(VersionStatus.Staged, loaded.Status);
        Assert.Equal(100, loaded.Input.FileCount);
        Assert.Equal("fastcdc-v1", loaded.Chunking.Algorithm);
    }

    [Fact]
    public async Task SaveAndLoadChannelState_RoundTrip()
    {
        // Arrange
        var manager = WorkspaceManager.ForPath(_tempDir);
        manager.CreateDirectoryStructure();
        manager.CreateChannelStructure("prod");

        var state = new ChannelState
        {
            ChannelId = "prod",
            DisplayName = "Production",
            Current = new CurrentVersionInfo
            {
                Version = "1.0.0",
                PublishedAt = DateTime.UtcNow,
                ManifestUrl = "https://cdn.example.com/manifest.json"
            },
            History =
            [
                new VersionHistoryEntry
                {
                    Version = "1.0.0",
                    Status = VersionStatus.Live,
                    BuiltAt = DateTime.UtcNow.AddHours(-1),
                    PublishedAt = DateTime.UtcNow,
                    FileCount = 100,
                    TotalSize = 1024 * 1024 * 500
                }
            ],
            RollbackAvailable = ["0.9.0"]
        };

        // Act
        await manager.SaveChannelStateAsync("prod", state);
        var loaded = await manager.LoadChannelStateAsync("prod");

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal("prod", loaded.ChannelId);
        Assert.Equal("1.0.0", loaded.Current?.Version);
        Assert.Single(loaded.History);
        Assert.Single(loaded.RollbackAvailable);
    }

    [Fact]
    public async Task SaveAndLoadWorkspaceState_RoundTrip()
    {
        // Arrange
        var manager = WorkspaceManager.ForPath(_tempDir);
        manager.CreateDirectoryStructure();

        var state = new WorkspaceState
        {
            PendingPublish =
            [
                new PendingPublishInfo
                {
                    Channel = "prod",
                    Version = "1.0.0",
                    StagedAt = DateTime.UtcNow
                }
            ],
            RecentOperations =
            [
                new RecentOperationInfo
                {
                    Type = "build",
                    Channel = "prod",
                    Version = "1.0.0",
                    Timestamp = DateTime.UtcNow,
                    Status = "success"
                }
            ]
        };

        // Act
        await manager.SaveStateAsync(state);
        var loaded = await manager.LoadStateAsync();

        // Assert
        Assert.Single(loaded.PendingPublish);
        Assert.Single(loaded.RecentOperations);
        Assert.Equal("prod", loaded.PendingPublish[0].Channel);
        Assert.Equal("build", loaded.RecentOperations[0].Type);
    }

    [Fact]
    public async Task ValidateVersionPattern_ReturnsTrueForValidVersion()
    {
        // Arrange
        var manager = WorkspaceManager.ForPath(_tempDir);
        var config = CreateSampleConfig();
        await manager.SaveConfigAsync(config);
        await manager.LoadConfigAsync();

        // Act & Assert
        Assert.True(manager.ValidateVersionPattern("prod", "1.0.0"));
        Assert.True(manager.ValidateVersionPattern("prod", "2.5.10"));
        Assert.True(manager.ValidateVersionPattern("beta", "1.0.0-beta.1"));
    }

    [Fact]
    public async Task ValidateVersionPattern_ReturnsFalseForInvalidVersion()
    {
        // Arrange
        var manager = WorkspaceManager.ForPath(_tempDir);
        var config = CreateSampleConfig();
        await manager.SaveConfigAsync(config);
        await manager.LoadConfigAsync();

        // Act & Assert
        Assert.False(manager.ValidateVersionPattern("prod", "1.0.0-beta.1")); // beta format on prod
        Assert.False(manager.ValidateVersionPattern("prod", "v1.0.0")); // has v prefix
        Assert.False(manager.ValidateVersionPattern("beta", "1.0.0")); // missing -beta.N
    }

    [Fact]
    public async Task GetDefaultChannelId_ReturnsDefaultChannel()
    {
        // Arrange
        var manager = WorkspaceManager.ForPath(_tempDir);
        var config = CreateSampleConfig();
        await manager.SaveConfigAsync(config);
        await manager.LoadConfigAsync();

        // Act
        var defaultChannel = manager.GetDefaultChannelId();

        // Assert
        Assert.Equal("prod", defaultChannel);
    }

    [Fact]
    public async Task GetPublishProfile_ReturnsCorrectProfile()
    {
        // Arrange
        var manager = WorkspaceManager.ForPath(_tempDir);
        var config = CreateSampleConfig();
        await manager.SaveConfigAsync(config);
        await manager.LoadConfigAsync();

        // Act
        var profile = manager.GetPublishProfile("prod");

        // Assert
        Assert.NotNull(profile);
        Assert.Equal("s3", profile.Type);
        Assert.Equal("test-game-cdn", profile.Bucket);
    }

    [Fact]
    public void GetVersions_ReturnsEmptyWhenNoVersions()
    {
        // Arrange
        var manager = WorkspaceManager.ForPath(_tempDir);
        manager.CreateDirectoryStructure();
        manager.CreateChannelStructure("prod");

        // Act
        var versions = manager.GetVersions("prod").ToList();

        // Assert
        Assert.Empty(versions);
    }

    [Fact]
    public void GetVersions_ReturnsCreatedVersions()
    {
        // Arrange
        var manager = WorkspaceManager.ForPath(_tempDir);
        manager.CreateDirectoryStructure();
        manager.CreateChannelStructure("prod");
        manager.CreateVersionStructure("prod", "1.0.0");
        manager.CreateVersionStructure("prod", "1.0.1");
        manager.CreateVersionStructure("prod", "1.1.0");

        // Act
        var versions = manager.GetVersions("prod").ToList();

        // Assert
        Assert.Equal(3, versions.Count);
        Assert.Contains("1.0.0", versions);
        Assert.Contains("1.0.1", versions);
        Assert.Contains("1.1.0", versions);
    }

    [Fact]
    public void FindWorkspace_FindsWorkspaceInCurrentDir()
    {
        // Arrange
        var manager = WorkspaceManager.ForPath(_tempDir);
        manager.CreateDirectoryStructure();
        File.WriteAllText(manager.ConfigPath, "{}");

        // Act
        var found = WorkspaceManager.FindWorkspace(_tempDir);

        // Assert
        Assert.NotNull(found);
        Assert.Equal(manager.WorkspacePath, found.WorkspacePath);
    }

    [Fact]
    public void FindWorkspace_FindsWorkspaceInParentDir()
    {
        // Arrange
        var manager = WorkspaceManager.ForPath(_tempDir);
        manager.CreateDirectoryStructure();
        File.WriteAllText(manager.ConfigPath, "{}");

        var subDir = Path.Combine(_tempDir, "subdir", "deep");
        Directory.CreateDirectory(subDir);

        // Act
        var found = WorkspaceManager.FindWorkspace(subDir);

        // Assert
        Assert.NotNull(found);
        Assert.Equal(manager.WorkspacePath, found.WorkspacePath);
    }

    [Fact]
    public void FindWorkspace_ReturnsNull_WhenNoWorkspace()
    {
        // Act
        var found = WorkspaceManager.FindWorkspace(_tempDir);

        // Assert
        Assert.Null(found);
    }

    [Fact]
    public async Task CreateGitIgnoreAsync_CreatesFile()
    {
        // Arrange
        var manager = WorkspaceManager.ForPath(_tempDir);
        manager.CreateDirectoryStructure();

        // Act
        await manager.CreateGitIgnoreAsync();

        // Assert
        var gitignorePath = Path.Combine(_tempDir, ".gitignore");
        Assert.True(File.Exists(gitignorePath));

        var content = await File.ReadAllTextAsync(gitignorePath);
        Assert.Contains(".patchsync/", content);
    }

    private static WorkspaceConfig CreateSampleConfig()
    {
        return new WorkspaceConfig
        {
            SchemaVersion = 1,
            Project = new ProjectInfo
            {
                Name = "Test Game",
                Id = "test-game"
            },
            Channels = new Dictionary<string, ChannelConfig>
            {
                ["prod"] = new ChannelConfig
                {
                    DisplayName = "Production",
                    IsDefault = true,
                    VersionPattern = @"^\d+\.\d+\.\d+$",
                    RetainVersions = 10,
                    Publish = new ChannelPublishConfig { Profile = "prod-cdn" }
                },
                ["beta"] = new ChannelConfig
                {
                    DisplayName = "Beta",
                    VersionPattern = @"^\d+\.\d+\.\d+-beta\.\d+$",
                    RetainVersions = 5,
                    Publish = new ChannelPublishConfig { Profile = "beta-cdn" }
                }
            },
            PublishProfiles = new Dictionary<string, PublishProfile>
            {
                ["prod-cdn"] = new PublishProfile
                {
                    Type = "s3",
                    Endpoint = "https://s3.amazonaws.com",
                    Bucket = "test-game-cdn",
                    Region = "us-east-1"
                },
                ["beta-cdn"] = new PublishProfile
                {
                    Type = "s3",
                    Endpoint = "https://s3.amazonaws.com",
                    Bucket = "test-game-cdn",
                    Region = "us-east-1"
                }
            }
        };
    }
}
