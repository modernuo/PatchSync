using PatchSync.CLI.Workspace;

namespace PatchSync.CLI.Tests;

/// <summary>
/// Tests for version status lifecycle transitions.
/// Verifies correct state changes: Building → Staged → Publishing → Live → Superseded → Archived
/// </summary>
public class VersionStatusLifecycleTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WorkspaceManager _manager;

    public VersionStatusLifecycleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"patchsync_lifecycle_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _manager = WorkspaceManager.ForPath(_tempDir);
        _manager.CreateDirectoryStructure();
        _manager.CreateChannelStructure("prod");
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
    public async Task Version_StartsInBuildingStatus()
    {
        // Arrange
        _manager.CreateVersionStructure("prod", "1.0.0");

        var metadata = CreateVersionMetadata("1.0.0", VersionStatus.Building);

        // Act
        await _manager.SaveVersionMetadataAsync("prod", "1.0.0", metadata);
        var loaded = await _manager.LoadVersionMetadataAsync("prod", "1.0.0");

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal(VersionStatus.Building, loaded.Status);
    }

    [Fact]
    public async Task Version_TransitionsToStagedAfterBuild()
    {
        // Arrange
        _manager.CreateVersionStructure("prod", "1.0.0");
        var metadata = CreateVersionMetadata("1.0.0", VersionStatus.Building);
        await _manager.SaveVersionMetadataAsync("prod", "1.0.0", metadata);

        // Act - simulate build completion
        metadata.Status = VersionStatus.Staged;
        metadata.Output = new OutputInfo
        {
            SignatureCount = 100,
            SignatureTotalSize = 1024 * 1024
        };
        await _manager.SaveVersionMetadataAsync("prod", "1.0.0", metadata);

        var loaded = await _manager.LoadVersionMetadataAsync("prod", "1.0.0");

        // Assert
        Assert.Equal(VersionStatus.Staged, loaded!.Status);
        Assert.NotNull(loaded.Output);
        Assert.Equal(100, loaded.Output.SignatureCount);
    }

    [Fact]
    public async Task Version_TransitionsToPublishingDuringUpload()
    {
        // Arrange
        _manager.CreateVersionStructure("prod", "1.0.0");
        var metadata = CreateVersionMetadata("1.0.0", VersionStatus.Staged);
        await _manager.SaveVersionMetadataAsync("prod", "1.0.0", metadata);

        // Act - simulate publish start
        metadata.Status = VersionStatus.Publishing;
        metadata.Publish = new PublishInfo
        {
            Profile = "prod-cdn",
            LastUploadAt = DateTime.UtcNow
        };
        await _manager.SaveVersionMetadataAsync("prod", "1.0.0", metadata);

        var loaded = await _manager.LoadVersionMetadataAsync("prod", "1.0.0");

        // Assert
        Assert.Equal(VersionStatus.Publishing, loaded!.Status);
        Assert.NotNull(loaded.Publish);
        Assert.NotNull(loaded.Publish.LastUploadAt);
    }

    [Fact]
    public async Task Version_TransitionsToLiveAfterPublish()
    {
        // Arrange
        _manager.CreateVersionStructure("prod", "1.0.0");
        var metadata = CreateVersionMetadata("1.0.0", VersionStatus.Publishing);
        metadata.Publish = new PublishInfo { Profile = "prod-cdn", LastUploadAt = DateTime.UtcNow };
        await _manager.SaveVersionMetadataAsync("prod", "1.0.0", metadata);

        // Act - simulate publish completion
        metadata.Status = VersionStatus.Live;
        metadata.Publish!.PublishedAt = DateTime.UtcNow;
        metadata.Publish.ManifestUrl = "https://cdn.example.com/prod/1.0.0/manifest.json";
        await _manager.SaveVersionMetadataAsync("prod", "1.0.0", metadata);

        // Update channel state
        var channelState = new ChannelState
        {
            ChannelId = "prod",
            DisplayName = "Production",
            Current = new CurrentVersionInfo
            {
                Version = "1.0.0",
                PublishedAt = DateTime.UtcNow,
                ManifestUrl = metadata.Publish.ManifestUrl
            }
        };
        await _manager.SaveChannelStateAsync("prod", channelState);

        var loadedMetadata = await _manager.LoadVersionMetadataAsync("prod", "1.0.0");
        var loadedState = await _manager.LoadChannelStateAsync("prod");

        // Assert
        Assert.Equal(VersionStatus.Live, loadedMetadata!.Status);
        Assert.NotNull(loadedMetadata.Publish!.PublishedAt);
        Assert.Equal("1.0.0", loadedState!.Current!.Version);
    }

    [Fact]
    public async Task OldVersion_TransitionsToSupersededWhenNewVersionGoesLive()
    {
        // Arrange
        _manager.CreateVersionStructure("prod", "1.0.0");
        _manager.CreateVersionStructure("prod", "1.1.0");

        // Set up 1.0.0 as live
        var v1Metadata = CreateVersionMetadata("1.0.0", VersionStatus.Live);
        await _manager.SaveVersionMetadataAsync("prod", "1.0.0", v1Metadata);

        // Act - 1.1.0 goes live, 1.0.0 becomes superseded
        var v2Metadata = CreateVersionMetadata("1.1.0", VersionStatus.Live);
        await _manager.SaveVersionMetadataAsync("prod", "1.1.0", v2Metadata);

        v1Metadata.Status = VersionStatus.Superseded;
        await _manager.SaveVersionMetadataAsync("prod", "1.0.0", v1Metadata);

        // Update channel state with history
        var channelState = new ChannelState
        {
            ChannelId = "prod",
            DisplayName = "Production",
            Current = new CurrentVersionInfo
            {
                Version = "1.1.0",
                PublishedAt = DateTime.UtcNow
            },
            History =
            [
                new VersionHistoryEntry
                {
                    Version = "1.1.0",
                    Status = VersionStatus.Live,
                    BuiltAt = DateTime.UtcNow,
                    PublishedAt = DateTime.UtcNow
                },
                new VersionHistoryEntry
                {
                    Version = "1.0.0",
                    Status = VersionStatus.Superseded,
                    BuiltAt = DateTime.UtcNow.AddDays(-7),
                    PublishedAt = DateTime.UtcNow.AddDays(-7)
                }
            ],
            RollbackAvailable = ["1.0.0"]
        };
        await _manager.SaveChannelStateAsync("prod", channelState);

        var loaded100 = await _manager.LoadVersionMetadataAsync("prod", "1.0.0");
        var loaded110 = await _manager.LoadVersionMetadataAsync("prod", "1.1.0");
        var state = await _manager.LoadChannelStateAsync("prod");

        // Assert
        Assert.Equal(VersionStatus.Superseded, loaded100!.Status);
        Assert.Equal(VersionStatus.Live, loaded110!.Status);
        Assert.Equal("1.1.0", state!.Current!.Version);
        Assert.Contains("1.0.0", state.RollbackAvailable);
    }

    [Fact]
    public async Task Version_TransitionsToArchivedAfterRetentionLimit()
    {
        // Arrange
        _manager.CreateVersionStructure("prod", "1.0.0");
        var metadata = CreateVersionMetadata("1.0.0", VersionStatus.Superseded);
        await _manager.SaveVersionMetadataAsync("prod", "1.0.0", metadata);

        // Act - simulate archival (after too many newer versions)
        metadata.Status = VersionStatus.Archived;
        await _manager.SaveVersionMetadataAsync("prod", "1.0.0", metadata);

        var loaded = await _manager.LoadVersionMetadataAsync("prod", "1.0.0");

        // Assert
        Assert.Equal(VersionStatus.Archived, loaded!.Status);
    }

    [Fact]
    public async Task Version_TransitionsToFailedOnBuildError()
    {
        // Arrange
        _manager.CreateVersionStructure("prod", "1.0.0");
        var metadata = CreateVersionMetadata("1.0.0", VersionStatus.Building);
        await _manager.SaveVersionMetadataAsync("prod", "1.0.0", metadata);

        // Act - simulate build failure
        metadata.Status = VersionStatus.Failed;
        await _manager.SaveVersionMetadataAsync("prod", "1.0.0", metadata);

        var loaded = await _manager.LoadVersionMetadataAsync("prod", "1.0.0");

        // Assert
        Assert.Equal(VersionStatus.Failed, loaded!.Status);
    }

    [Fact]
    public async Task ChannelState_TracksVersionHistory()
    {
        // Arrange & Act
        var versions = new[] { "1.0.0", "1.1.0", "1.2.0" };
        var history = new List<VersionHistoryEntry>();

        foreach (var version in versions)
        {
            _manager.CreateVersionStructure("prod", version);
            history.Add(new VersionHistoryEntry
            {
                Version = version,
                Status = VersionStatus.Live,
                BuiltAt = DateTime.UtcNow.AddHours(-3),
                PublishedAt = DateTime.UtcNow
            });
        }

        // Mark older versions as superseded
        history[0].Status = VersionStatus.Superseded;
        history[1].Status = VersionStatus.Superseded;

        var channelState = new ChannelState
        {
            ChannelId = "prod",
            DisplayName = "Production",
            Current = new CurrentVersionInfo { Version = "1.2.0", PublishedAt = DateTime.UtcNow },
            History = history,
            RollbackAvailable = ["1.1.0", "1.0.0"]
        };

        await _manager.SaveChannelStateAsync("prod", channelState);
        var loaded = await _manager.LoadChannelStateAsync("prod");

        // Assert
        Assert.Equal(3, loaded!.History.Count);
        Assert.Equal("1.2.0", loaded.Current!.Version);
        Assert.Equal(2, loaded.RollbackAvailable.Count);
        Assert.Contains("1.1.0", loaded.RollbackAvailable);
        Assert.Contains("1.0.0", loaded.RollbackAvailable);
    }

    [Fact]
    public async Task WorkspaceState_TracksPendingPublish()
    {
        // Arrange
        _manager.CreateVersionStructure("prod", "1.0.0");
        _manager.CreateVersionStructure("beta", "2.0.0-beta.1");

        var state = new WorkspaceState
        {
            PendingPublish =
            [
                new PendingPublishInfo { Channel = "prod", Version = "1.0.0", StagedAt = DateTime.UtcNow },
                new PendingPublishInfo { Channel = "beta", Version = "2.0.0-beta.1", StagedAt = DateTime.UtcNow }
            ]
        };

        // Act
        await _manager.SaveStateAsync(state);
        var loaded = await _manager.LoadStateAsync();

        // Assert
        Assert.Equal(2, loaded.PendingPublish.Count);
        Assert.Contains(loaded.PendingPublish, p => p.Channel == "prod" && p.Version == "1.0.0");
        Assert.Contains(loaded.PendingPublish, p => p.Channel == "beta" && p.Version == "2.0.0-beta.1");
    }

    [Fact]
    public async Task WorkspaceState_TracksRecentOperations()
    {
        // Arrange
        var state = new WorkspaceState
        {
            RecentOperations =
            [
                new RecentOperationInfo
                {
                    Type = "build",
                    Channel = "prod",
                    Version = "1.0.0",
                    Timestamp = DateTime.UtcNow.AddMinutes(-30),
                    Status = "success"
                },
                new RecentOperationInfo
                {
                    Type = "publish",
                    Channel = "prod",
                    Version = "1.0.0",
                    Timestamp = DateTime.UtcNow.AddMinutes(-15),
                    Status = "success"
                },
                new RecentOperationInfo
                {
                    Type = "build",
                    Channel = "beta",
                    Version = "2.0.0-beta.1",
                    Timestamp = DateTime.UtcNow,
                    Status = "failed"
                }
            ]
        };

        // Act
        await _manager.SaveStateAsync(state);
        var loaded = await _manager.LoadStateAsync();

        // Assert
        Assert.Equal(3, loaded.RecentOperations.Count);
        Assert.Contains(loaded.RecentOperations, o => o.Type == "build" && o.Status == "failed");
        Assert.Contains(loaded.RecentOperations, o => o.Type == "publish" && o.Status == "success");
    }

    [Fact]
    public async Task WorkspaceState_TracksActiveBuilds()
    {
        // Arrange
        var state = new WorkspaceState
        {
            ActiveBuilds =
            [
                new ActiveBuildInfo
                {
                    Channel = "prod",
                    Version = "1.0.0",
                    StartedAt = DateTime.UtcNow,
                    MachineName = Environment.MachineName,
                    ProcessId = Environment.ProcessId
                }
            ]
        };

        // Act
        await _manager.SaveStateAsync(state);
        var loaded = await _manager.LoadStateAsync();

        // Assert
        Assert.Single(loaded.ActiveBuilds);
        Assert.Equal("prod", loaded.ActiveBuilds[0].Channel);
        Assert.Equal("1.0.0", loaded.ActiveBuilds[0].Version);
        Assert.Equal(Environment.MachineName, loaded.ActiveBuilds[0].MachineName);
    }

    [Fact]
    public async Task VersionMetadata_TracksComparisonInfo()
    {
        // Arrange
        _manager.CreateVersionStructure("prod", "1.1.0");
        var metadata = CreateVersionMetadata("1.1.0", VersionStatus.Staged);
        metadata.Comparison = new ComparisonInfo
        {
            PreviousVersion = "1.0.0",
            NewFiles = 10,
            ModifiedFiles = 25,
            DeletedFiles = 3,
            UnchangedFiles = 100,
            EstimatedDeltaDownload = 50 * 1024 * 1024
        };

        // Act
        await _manager.SaveVersionMetadataAsync("prod", "1.1.0", metadata);
        var loaded = await _manager.LoadVersionMetadataAsync("prod", "1.1.0");

        // Assert
        Assert.NotNull(loaded!.Comparison);
        Assert.Equal("1.0.0", loaded.Comparison.PreviousVersion);
        Assert.Equal(10, loaded.Comparison.NewFiles);
        Assert.Equal(25, loaded.Comparison.ModifiedFiles);
        Assert.Equal(3, loaded.Comparison.DeletedFiles);
        Assert.Equal(100, loaded.Comparison.UnchangedFiles);
        Assert.Equal(50 * 1024 * 1024, loaded.Comparison.EstimatedDeltaDownload);
    }

    [Fact]
    public async Task VersionMetadata_SerializesStatusAsString()
    {
        // Arrange
        _manager.CreateVersionStructure("prod", "1.0.0");
        var metadata = CreateVersionMetadata("1.0.0", VersionStatus.Live);
        await _manager.SaveVersionMetadataAsync("prod", "1.0.0", metadata);

        // Act - read raw JSON to verify status is a string
        var metadataPath = Path.Combine(_manager.GetVersionPath("prod", "1.0.0"), "version.json");
        var json = await File.ReadAllTextAsync(metadataPath);

        // Assert
        Assert.Contains("\"status\": \"Live\"", json);
        Assert.DoesNotContain("\"status\": 3", json); // Not a number
    }

    [Fact]
    public async Task MultipleChannels_IndependentLifecycles()
    {
        // Arrange
        _manager.CreateChannelStructure("beta");
        _manager.CreateVersionStructure("prod", "1.0.0");
        _manager.CreateVersionStructure("beta", "2.0.0-beta.1");

        // Act - prod at Live, beta at Staged
        var prodMetadata = CreateVersionMetadata("1.0.0", VersionStatus.Live);
        var betaMetadata = CreateVersionMetadata("2.0.0-beta.1", VersionStatus.Staged);

        await _manager.SaveVersionMetadataAsync("prod", "1.0.0", prodMetadata);
        await _manager.SaveVersionMetadataAsync("beta", "2.0.0-beta.1", betaMetadata);

        var loadedProd = await _manager.LoadVersionMetadataAsync("prod", "1.0.0");
        var loadedBeta = await _manager.LoadVersionMetadataAsync("beta", "2.0.0-beta.1");

        // Assert
        Assert.Equal(VersionStatus.Live, loadedProd!.Status);
        Assert.Equal(VersionStatus.Staged, loadedBeta!.Status);
    }

    private static VersionMetadata CreateVersionMetadata(string version, VersionStatus status)
    {
        return new VersionMetadata
        {
            Version = version,
            Channel = "prod",
            Status = status,
            Build = new BuildInfo
            {
                BuiltAt = DateTime.UtcNow,
                BuiltBy = Environment.MachineName,
                Duration = 10.5
            },
            Input = new InputInfo
            {
                Path = "/test/input",
                FileCount = 100,
                TotalSize = 1024 * 1024 * 500
            },
            Output = new OutputInfo
            {
                SignatureCount = 100,
                SignatureTotalSize = 1024 * 100
            },
            Chunking = new ChunkingInfo
            {
                Algorithm = "fastcdc-v1",
                MinChunkSize = 4096,
                AvgChunkSize = 16384,
                MaxChunkSize = 65536,
                TotalChunks = 10000
            }
        };
    }
}
