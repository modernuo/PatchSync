using System.Text.Json;
using PatchSync.CLI.Workspace;

namespace PatchSync.CLI.Tests;

/// <summary>
/// Integration tests for InitCommand workspace initialization.
/// Tests workspace creation, structure, and configuration files.
/// Note: These tests use WorkspaceManager directly to simulate init behavior,
/// since InitCommand's interactive prompts are not easily testable.
/// </summary>
public class InitCommandTests : IDisposable
{
    private readonly string _tempDir;

    public InitCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"patchsync_init_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    [Fact]
    public async Task Init_CreatesWorkspaceStructure()
    {
        // Arrange
        var workspacePath = Path.Combine(_tempDir, "my-game");
        Directory.CreateDirectory(workspacePath);
        var manager = WorkspaceManager.ForPath(workspacePath);
        var channels = new[] { "prod", "beta", "dev" };

        // Act - simulate init command behavior
        manager.CreateDirectoryStructure();
        foreach (var channel in channels)
        {
            manager.CreateChannelStructure(channel);
        }

        // Assert
        Assert.True(Directory.Exists(manager.InternalPath));
        Assert.True(Directory.Exists(manager.ChannelsPath));
        foreach (var channel in channels)
        {
            Assert.True(Directory.Exists(manager.GetChannelPath(channel)));
        }
    }

    [Fact]
    public async Task Init_CreatesConfigWithCorrectDefaults()
    {
        // Arrange
        var workspacePath = Path.Combine(_tempDir, "test-project");
        Directory.CreateDirectory(workspacePath);
        var manager = WorkspaceManager.ForPath(workspacePath);
        manager.CreateDirectoryStructure();

        var config = new WorkspaceConfig
        {
            SchemaVersion = 1,
            Project = new ProjectInfo
            {
                Name = "Test Project",
                Id = "test-project",
                Description = "A test project"
            },
            Defaults = new BuildDefaults
            {
                Chunking = new ChunkingDefaults
                {
                    Algorithm = "fastcdc-v1",
                    MinChunkSize = 4096,
                    AvgChunkSize = 16384,
                    MaxChunkSize = 65536
                },
                Compression = new CompressionDefaults
                {
                    Enabled = true,
                    Algorithm = "zstd",
                    Level = 9
                }
            }
        };

        // Act
        await manager.SaveConfigAsync(config);
        var loaded = await manager.LoadConfigAsync();

        // Assert
        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Equal("Test Project", loaded.Project.Name);
        Assert.Equal("test-project", loaded.Project.Id);
        Assert.Equal("fastcdc-v1", loaded.Defaults?.Chunking?.Algorithm);
        Assert.Equal(4096, loaded.Defaults?.Chunking?.MinChunkSize);
        Assert.True(loaded.Defaults?.Compression?.Enabled);
    }

    [Fact]
    public async Task Init_CreatesChannelStates()
    {
        // Arrange
        var workspacePath = Path.Combine(_tempDir, "game");
        Directory.CreateDirectory(workspacePath);
        var manager = WorkspaceManager.ForPath(workspacePath);
        manager.CreateDirectoryStructure();

        var channels = new[] { "prod", "beta" };
        foreach (var channel in channels)
        {
            manager.CreateChannelStructure(channel);
            await manager.SaveChannelStateAsync(channel, new ChannelState
            {
                ChannelId = channel,
                DisplayName = char.ToUpper(channel[0]) + channel[1..]
            });
        }

        // Act
        var prodState = await manager.LoadChannelStateAsync("prod");
        var betaState = await manager.LoadChannelStateAsync("beta");

        // Assert
        Assert.NotNull(prodState);
        Assert.Equal("prod", prodState.ChannelId);
        Assert.Equal("Prod", prodState.DisplayName);
        Assert.Null(prodState.Current);

        Assert.NotNull(betaState);
        Assert.Equal("beta", betaState.ChannelId);
        Assert.Equal("Beta", betaState.DisplayName);
    }

    [Fact]
    public async Task Init_CreatesEmptyWorkspaceState()
    {
        // Arrange
        var workspacePath = Path.Combine(_tempDir, "game");
        Directory.CreateDirectory(workspacePath);
        var manager = WorkspaceManager.ForPath(workspacePath);
        manager.CreateDirectoryStructure();

        // Act
        await manager.SaveStateAsync(new WorkspaceState());
        var state = await manager.LoadStateAsync();

        // Assert
        Assert.Empty(state.PendingPublish);
        Assert.Empty(state.RecentOperations);
        Assert.Empty(state.ActiveBuilds);
    }

    [Fact]
    public async Task Init_CreatesGitIgnore()
    {
        // Arrange
        var workspacePath = Path.Combine(_tempDir, "game");
        Directory.CreateDirectory(workspacePath);
        var manager = WorkspaceManager.ForPath(workspacePath);
        manager.CreateDirectoryStructure();

        // Act
        await manager.CreateGitIgnoreAsync();

        // Assert
        var gitignorePath = Path.Combine(workspacePath, ".gitignore");
        Assert.True(File.Exists(gitignorePath));
        var content = await File.ReadAllTextAsync(gitignorePath);
        Assert.Contains(".patchsync/", content);
    }

    [Fact]
    public async Task Init_ChannelConfigHasVersionPattern()
    {
        // Arrange
        var workspacePath = Path.Combine(_tempDir, "game");
        Directory.CreateDirectory(workspacePath);
        var manager = WorkspaceManager.ForPath(workspacePath);
        manager.CreateDirectoryStructure();

        var config = new WorkspaceConfig
        {
            SchemaVersion = 1,
            Project = new ProjectInfo { Name = "Game", Id = "game" },
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
                ["prod-cdn"] = new PublishProfile { Type = "s3", Endpoint = "https://s3.amazonaws.com", Bucket = "game-cdn" },
                ["beta-cdn"] = new PublishProfile { Type = "s3", Endpoint = "https://s3.amazonaws.com", Bucket = "game-cdn" }
            }
        };

        // Act
        await manager.SaveConfigAsync(config);
        await manager.LoadConfigAsync();

        // Assert - validate version patterns
        Assert.True(manager.ValidateVersionPattern("prod", "1.0.0"));
        Assert.True(manager.ValidateVersionPattern("prod", "2.5.10"));
        Assert.False(manager.ValidateVersionPattern("prod", "1.0.0-beta.1"));
        Assert.False(manager.ValidateVersionPattern("prod", "v1.0.0"));

        Assert.True(manager.ValidateVersionPattern("beta", "1.0.0-beta.1"));
        Assert.True(manager.ValidateVersionPattern("beta", "2.0.0-beta.5"));
        Assert.False(manager.ValidateVersionPattern("beta", "1.0.0"));
    }

    [Fact]
    public async Task Init_PublishProfilesConfigured()
    {
        // Arrange
        var workspacePath = Path.Combine(_tempDir, "game");
        Directory.CreateDirectory(workspacePath);
        var manager = WorkspaceManager.ForPath(workspacePath);
        manager.CreateDirectoryStructure();

        var config = new WorkspaceConfig
        {
            SchemaVersion = 1,
            Project = new ProjectInfo { Name = "Game", Id = "game" },
            Channels = new Dictionary<string, ChannelConfig>
            {
                ["prod"] = new ChannelConfig
                {
                    DisplayName = "Production",
                    IsDefault = true,
                    Publish = new ChannelPublishConfig { Profile = "prod-cdn" }
                }
            },
            PublishProfiles = new Dictionary<string, PublishProfile>
            {
                ["prod-cdn"] = new PublishProfile
                {
                    Type = "s3",
                    Endpoint = "https://s3.amazonaws.com",
                    Bucket = "game-cdn",
                    Region = "us-east-1",
                    PublicUrl = "https://cdn.game.com",
                    CredentialSource = "environment"
                }
            }
        };

        // Act
        await manager.SaveConfigAsync(config);
        await manager.LoadConfigAsync();
        var profile = manager.GetPublishProfile("prod");

        // Assert
        Assert.NotNull(profile);
        Assert.Equal("s3", profile.Type);
        Assert.Equal("game-cdn", profile.Bucket);
        Assert.Equal("us-east-1", profile.Region);
        Assert.Equal("https://cdn.game.com", profile.PublicUrl);
    }

    [Fact]
    public async Task Init_GetDefaultChannelId_ReturnsFirstChannel()
    {
        // Arrange
        var workspacePath = Path.Combine(_tempDir, "game");
        Directory.CreateDirectory(workspacePath);
        var manager = WorkspaceManager.ForPath(workspacePath);
        manager.CreateDirectoryStructure();

        var config = new WorkspaceConfig
        {
            SchemaVersion = 1,
            Project = new ProjectInfo { Name = "Game", Id = "game" },
            Channels = new Dictionary<string, ChannelConfig>
            {
                ["staging"] = new ChannelConfig { DisplayName = "Staging", IsDefault = false },
                ["prod"] = new ChannelConfig { DisplayName = "Production", IsDefault = true },
                ["dev"] = new ChannelConfig { DisplayName = "Dev", IsDefault = false }
            }
        };

        // Act
        await manager.SaveConfigAsync(config);
        await manager.LoadConfigAsync();
        var defaultChannel = manager.GetDefaultChannelId();

        // Assert
        Assert.Equal("prod", defaultChannel);
    }

    [Fact]
    public async Task Init_ConfigJsonIsValidFormat()
    {
        // Arrange
        var workspacePath = Path.Combine(_tempDir, "game");
        Directory.CreateDirectory(workspacePath);
        var manager = WorkspaceManager.ForPath(workspacePath);
        manager.CreateDirectoryStructure();

        var config = new WorkspaceConfig
        {
            SchemaVersion = 1,
            Project = new ProjectInfo { Name = "My Game", Id = "my-game" },
            Channels = new Dictionary<string, ChannelConfig>
            {
                ["prod"] = new ChannelConfig { DisplayName = "Production", IsDefault = true }
            }
        };

        // Act
        await manager.SaveConfigAsync(config);

        // Assert - verify JSON is properly formatted (camelCase, indented)
        var json = await File.ReadAllTextAsync(manager.ConfigPath);
        Assert.Contains("\"schemaVersion\"", json);
        Assert.Contains("\"project\"", json);
        Assert.Contains("\"name\"", json);
        Assert.DoesNotContain("\"SchemaVersion\"", json);
        Assert.DoesNotContain("\"Project\"", json);

        // Verify it's valid JSON
        var doc = JsonDocument.Parse(json);
        Assert.NotNull(doc);
    }

    [Fact]
    public void Init_WorkspaceAlreadyExists_CanBeDetected()
    {
        // Arrange
        var workspacePath = Path.Combine(_tempDir, "existing");
        Directory.CreateDirectory(workspacePath);
        var manager = WorkspaceManager.ForPath(workspacePath);
        manager.CreateDirectoryStructure();

        // Create config file to mark as existing workspace
        File.WriteAllText(manager.ConfigPath, "{}");

        // Act
        var newManager = WorkspaceManager.ForPath(workspacePath);

        // Assert
        Assert.True(newManager.Exists);
    }

    [Fact]
    public void Init_FindWorkspace_FindsExistingWorkspace()
    {
        // Arrange
        var workspacePath = Path.Combine(_tempDir, "game-project");
        var subDir = Path.Combine(workspacePath, "src", "deep", "nested");
        Directory.CreateDirectory(workspacePath);
        Directory.CreateDirectory(subDir);

        var manager = WorkspaceManager.ForPath(workspacePath);
        manager.CreateDirectoryStructure();
        File.WriteAllText(manager.ConfigPath, "{}");

        // Act
        var found = WorkspaceManager.FindWorkspace(subDir);

        // Assert
        Assert.NotNull(found);
        Assert.Equal(Path.GetFullPath(workspacePath), found.WorkspacePath);
    }

    [Fact]
    public async Task Init_MultipleChannels_EachHasCorrectConfig()
    {
        // Arrange
        var workspacePath = Path.Combine(_tempDir, "game");
        Directory.CreateDirectory(workspacePath);
        var manager = WorkspaceManager.ForPath(workspacePath);
        manager.CreateDirectoryStructure();

        var channels = new[] { "prod", "beta", "dev", "nightly" };
        var config = new WorkspaceConfig
        {
            SchemaVersion = 1,
            Project = new ProjectInfo { Name = "Game", Id = "game" }
        };

        foreach (var channel in channels)
        {
            manager.CreateChannelStructure(channel);
            config.Channels[channel] = new ChannelConfig
            {
                DisplayName = char.ToUpper(channel[0]) + channel[1..],
                IsDefault = channel == "prod"
            };

            await manager.SaveChannelStateAsync(channel, new ChannelState
            {
                ChannelId = channel,
                DisplayName = config.Channels[channel].DisplayName
            });
        }

        await manager.SaveConfigAsync(config);

        // Act
        var loaded = await manager.LoadConfigAsync();

        // Assert
        Assert.Equal(4, loaded.Channels.Count);
        Assert.True(loaded.Channels["prod"].IsDefault);
        Assert.False(loaded.Channels["beta"].IsDefault);

        foreach (var channel in channels)
        {
            Assert.True(Directory.Exists(manager.GetChannelPath(channel)));
            var state = await manager.LoadChannelStateAsync(channel);
            Assert.NotNull(state);
            Assert.Equal(channel, state.ChannelId);
        }
    }
}
