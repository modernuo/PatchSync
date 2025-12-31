using System.Text.Json;
using PatchSync.CLI.Workspace;

namespace PatchSync.CLI.Tests;

/// <summary>
/// Tests for workspace configuration serialization and deserialization.
/// Ensures AOT-compatible JSON context works correctly.
/// </summary>
public class WorkspaceConfigTests
{
    [Fact]
    public void WorkspaceConfig_SerializesAndDeserializes_RoundTrip()
    {
        // Arrange
        var config = CreateSampleConfig();

        // Act
        var json = JsonSerializer.Serialize(config, WorkspaceJsonContext.Default.WorkspaceConfig);
        var deserialized = JsonSerializer.Deserialize(json, WorkspaceJsonContext.Default.WorkspaceConfig);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(config.SchemaVersion, deserialized.SchemaVersion);
        Assert.Equal(config.Project.Name, deserialized.Project.Name);
        Assert.Equal(config.Project.Id, deserialized.Project.Id);
        Assert.Equal(config.Channels.Count, deserialized.Channels.Count);
        Assert.Equal(config.PublishProfiles.Count, deserialized.PublishProfiles.Count);
    }

    [Fact]
    public void WorkspaceConfig_SerializesWithCamelCase()
    {
        // Arrange
        var config = CreateSampleConfig();

        // Act
        var json = JsonSerializer.Serialize(config, WorkspaceJsonContext.Default.WorkspaceConfig);

        // Assert
        Assert.Contains("\"schemaVersion\"", json);
        Assert.Contains("\"project\"", json);
        Assert.Contains("\"channels\"", json);
        Assert.DoesNotContain("\"SchemaVersion\"", json);
        Assert.DoesNotContain("\"Project\"", json);
    }

    [Fact]
    public void WorkspaceConfig_OmitsNullValues()
    {
        // Arrange
        var config = new WorkspaceConfig
        {
            Project = new ProjectInfo
            {
                Name = "TestProject",
                Id = "test-project",
                Description = null // Should be omitted
            }
        };

        // Act
        var json = JsonSerializer.Serialize(config, WorkspaceJsonContext.Default.WorkspaceConfig);

        // Assert
        Assert.DoesNotContain("\"description\"", json);
    }

    [Fact]
    public void ChannelConfig_SerializesWithAllProperties()
    {
        // Arrange
        var config = CreateSampleConfig();

        // Act
        var json = JsonSerializer.Serialize(config, WorkspaceJsonContext.Default.WorkspaceConfig);

        // Assert
        Assert.Contains("\"displayName\"", json);
        Assert.Contains("\"versionPattern\"", json);
        Assert.Contains("\"retainVersions\"", json);
        Assert.Contains("\"isDefault\"", json);
    }

    [Fact]
    public void PublishProfile_SerializesS3Config()
    {
        // Arrange
        var profile = new PublishProfile
        {
            Type = "s3",
            Endpoint = "https://s3.amazonaws.com",
            Bucket = "test-bucket",
            Region = "us-east-1",
            PublicUrl = "https://cdn.example.com",
            CredentialSource = "environment"
        };

        // Act
        var json = JsonSerializer.Serialize(profile, WorkspaceJsonContext.Default.PublishProfile);
        var deserialized = JsonSerializer.Deserialize(json, WorkspaceJsonContext.Default.PublishProfile);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal("s3", deserialized.Type);
        Assert.Equal("https://s3.amazonaws.com", deserialized.Endpoint);
        Assert.Equal("test-bucket", deserialized.Bucket);
        Assert.Equal("us-east-1", deserialized.Region);
    }

    [Fact]
    public void ChunkingDefaults_HasCorrectDefaultValues()
    {
        // Arrange & Act
        var defaults = new ChunkingDefaults();

        // Assert
        Assert.Equal("fastcdc-v1", defaults.Algorithm);
        Assert.Equal(4096, defaults.MinChunkSize);
        Assert.Equal(16384, defaults.AvgChunkSize);
        Assert.Equal(65536, defaults.MaxChunkSize);
        Assert.Equal(65536, defaults.MinDeltaSize);
    }

    private static WorkspaceConfig CreateSampleConfig()
    {
        return new WorkspaceConfig
        {
            SchemaVersion = 1,
            Project = new ProjectInfo
            {
                Name = "Test Game",
                Id = "test-game",
                Description = "A test game for unit testing"
            },
            Defaults = new BuildDefaults
            {
                InputPath = "/path/to/game",
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
            },
            Channels = new Dictionary<string, ChannelConfig>
            {
                ["prod"] = new ChannelConfig
                {
                    DisplayName = "Production",
                    Description = "Stable releases",
                    IsDefault = true,
                    VersionPattern = @"^\d+\.\d+\.\d+$",
                    RetainVersions = 10,
                    Publish = new ChannelPublishConfig
                    {
                        Profile = "prod-cdn"
                    }
                },
                ["beta"] = new ChannelConfig
                {
                    DisplayName = "Beta",
                    Description = "Pre-release testing",
                    IsDefault = false,
                    VersionPattern = @"^\d+\.\d+\.\d+-beta\.\d+$",
                    RetainVersions = 5,
                    Publish = new ChannelPublishConfig
                    {
                        Profile = "beta-cdn",
                        Prefix = "beta/"
                    }
                }
            },
            PublishProfiles = new Dictionary<string, PublishProfile>
            {
                ["prod-cdn"] = new PublishProfile
                {
                    Type = "s3",
                    Endpoint = "https://s3.amazonaws.com",
                    Bucket = "test-game-cdn",
                    Region = "us-east-1",
                    PublicUrl = "https://cdn.testgame.com",
                    CredentialSource = "environment"
                },
                ["beta-cdn"] = new PublishProfile
                {
                    Type = "s3",
                    Endpoint = "https://s3.amazonaws.com",
                    Bucket = "test-game-cdn",
                    Region = "us-east-1",
                    PublicUrl = "https://beta.cdn.testgame.com",
                    CredentialSource = "stored"
                }
            }
        };
    }
}
