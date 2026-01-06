using System.Diagnostics;
using System.Text.Json;
using PatchSync.CLI.Build;
using PatchSync.CLI.TUI.Core;
using PatchSync.CLI.Workspace;
using PatchSync.Common.Chunking;
using PatchSync.Common.Manifest;

namespace PatchSync.CLI.TUI.Integration;

/// <summary>
/// Service that bridges TUI to the ParallelSignatureBuilder.
/// Runs build operations on background tasks and posts messages back to TUI.
/// </summary>
public sealed class TuiBuildService
{
    private readonly TuiApplication _app;
    private readonly WorkspaceManager _workspace;
    private CancellationTokenSource? _buildCts;

    public TuiBuildService(TuiApplication app, WorkspaceManager workspace)
    {
        _app = app;
        _workspace = workspace;
    }

    /// <summary>
    /// Start a build operation on a background task.
    /// </summary>
    public void StartBuild(string channel, string version, string inputPath, string? baseVersion = null)
    {
        // Cancel any existing build
        _buildCts?.Cancel();
        _buildCts = new CancellationTokenSource();

        var token = _buildCts.Token;

        // Run build on background task
        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteBuildAsync(channel, version, inputPath, baseVersion, token);
            }
            catch (OperationCanceledException)
            {
                _app.PostMessage(new BuildFailed("Build cancelled"));
            }
            catch (Exception ex)
            {
                _app.PostMessage(new BuildFailed(ex.Message));
            }
        }, token);
    }

    /// <summary>
    /// Cancel the current build operation.
    /// </summary>
    public void CancelBuild()
    {
        _buildCts?.Cancel();
    }

    private async Task ExecuteBuildAsync(
        string channel,
        string version,
        string inputPath,
        string? baseVersion,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var config = _workspace.Config;

        if (config == null)
        {
            _app.PostMessage(new BuildFailed("Workspace configuration not loaded"));
            return;
        }

        if (!config.Channels.TryGetValue(channel, out var channelConfig))
        {
            _app.PostMessage(new BuildFailed($"Channel not found: {channel}"));
            return;
        }

        if (!Directory.Exists(inputPath))
        {
            _app.PostMessage(new BuildFailed($"Input directory not found: {inputPath}"));
            return;
        }

        // Create version structure
        _workspace.CreateVersionStructure(channel, version);
        var versionPath = _workspace.GetVersionPath(channel, version);

        // Try to acquire build lock
        using var buildLock = BuildLock.TryAcquire(versionPath);
        if (buildLock == null)
        {
            var holder = BuildLock.GetLockHolder(versionPath);
            var message = "Build in progress by another process";
            if (holder != null)
            {
                message += $" ({holder.MachineName}, PID {holder.ProcessId})";
            }
            _app.PostMessage(new BuildFailed(message));
            return;
        }

        // Resolve chunking options
        var chunkingDefaults = config.Defaults?.Chunking ?? new ChunkingDefaults();
        var algorithm = chunkingDefaults.Algorithm;
        var minChunk = chunkingDefaults.MinChunkSize;
        var avgChunk = chunkingDefaults.AvgChunkSize;
        var maxChunk = chunkingDefaults.MaxChunkSize;
        var minDeltaSize = chunkingDefaults.MinDeltaSize;

        // Get chunker
        var registry = ChunkerRegistry.Default;
        if (!registry.TryGetChunker(algorithm, out var chunker))
        {
            _app.PostMessage(new BuildFailed($"Unknown algorithm: {algorithm}"));
            return;
        }

        var options = new ChunkingOptions
        {
            MinSize = minChunk,
            AverageSize = avgChunk,
            MaxSize = maxChunk
        };

        try
        {
            options.Validate();
        }
        catch (Exception ex)
        {
            _app.PostMessage(new BuildFailed($"Invalid chunking options: {ex.Message}"));
            return;
        }

        // Load base version if specified
        GameManifest? baseManifest = null;
        BasedOnInfo? basedOnInfo = null;
        BuildComparisonResult? comparisonResult = null;
        string? baseChannel = null;
        string? baseVersionString = null;

        if (!string.IsNullOrEmpty(baseVersion))
        {
            var colonIdx = baseVersion.IndexOf(':');
            if (colonIdx > 0)
            {
                baseChannel = baseVersion[..colonIdx];
                baseVersionString = baseVersion[(colonIdx + 1)..];
            }
            else
            {
                baseChannel = channel;
                baseVersionString = baseVersion;
            }

            if (!_workspace.VersionExists(baseChannel, baseVersionString))
            {
                _app.PostMessage(new BuildFailed($"Base version not found: {baseChannel}/{baseVersionString}"));
                return;
            }

            var baseManifestPath = _workspace.GetManifestPath(baseChannel, baseVersionString);
            if (!File.Exists(baseManifestPath))
            {
                _app.PostMessage(new BuildFailed($"Base manifest not found"));
                return;
            }

            await using var baseManifestStream = File.OpenRead(baseManifestPath);
            baseManifest = await JsonSerializer.DeserializeAsync(
                baseManifestStream,
                ManifestJsonContext.Default.GameManifest,
                ct);

            if (baseManifest != null)
            {
                basedOnInfo = new BasedOnInfo
                {
                    Channel = baseChannel,
                    Version = baseVersionString,
                    BaseBuiltAt = baseManifest.BuildDate,
                    IsCrossChannel = baseChannel != channel
                };

                // Run comparison
                var comparer = new BuildComparer();
                comparisonResult = await comparer.CompareAsync(inputPath, baseManifest);
            }
        }

        // Setup output paths
        var sigDir = _workspace.GetSignaturesPath(channel, version);
        Directory.CreateDirectory(sigDir);

        var parallelBuilder = new ParallelSignatureBuilder(chunker!, options);
        var buildOptions = new ParallelBuildOptions
        {
            MinDeltaSize = minDeltaSize,
            GenerateSignatures = true,
            SignatureOutputDirectory = sigDir
        };

        // Create progress adapter
        var progress = new TuiBuildProgress(_app);

        // Run the build
        var manifest = await parallelBuilder.BuildAsync(
            inputPath,
            version,
            "", // Base URL will be set at publish time
            buildOptions,
            progress,
            ct);

        stopwatch.Stop();

        // Write manifest
        var manifestPath = _workspace.GetManifestPath(channel, version);
        await using (var manifestStream = File.Create(manifestPath))
        {
            await JsonSerializer.SerializeAsync(
                manifestStream,
                manifest,
                ManifestJsonContext.Default.GameManifest,
                ct);
        }

        // Calculate signature stats
        var sigFiles = Directory.GetFiles(sigDir, "*.sig");
        var sigTotalSize = sigFiles.Sum(f => new FileInfo(f).Length);

        // Create version metadata
        var metadata = new VersionMetadata
        {
            Version = version,
            Channel = channel,
            Status = VersionStatus.Staged,
            Build = new BuildInfo
            {
                BuiltAt = DateTime.UtcNow,
                BuiltBy = Environment.MachineName,
                Duration = stopwatch.Elapsed.TotalSeconds,
                PatchSyncVersion = "2.0.0"
            },
            Input = new InputInfo
            {
                Path = inputPath,
                FileCount = manifest.Files.Count,
                TotalSize = manifest.Files.Sum(f => f.Size)
            },
            Output = new OutputInfo
            {
                SignatureCount = sigFiles.Length,
                SignatureTotalSize = sigTotalSize
            },
            Chunking = new ChunkingInfo
            {
                Algorithm = algorithm,
                MinChunkSize = minChunk,
                AvgChunkSize = avgChunk,
                MaxChunkSize = maxChunk
            },
            BasedOn = basedOnInfo,
            Comparison = comparisonResult != null && baseVersionString != null
                ? new ComparisonInfo
                {
                    PreviousVersion = baseVersionString,
                    NewFiles = comparisonResult.NewFiles.Count,
                    ModifiedFiles = comparisonResult.ModifiedFiles.Count,
                    DeletedFiles = comparisonResult.DeletedFiles.Count,
                    UnchangedFiles = comparisonResult.UnchangedFiles.Count
                }
                : null
        };

        await _workspace.SaveVersionMetadataAsync(channel, version, metadata, ct);

        // Update channel state
        var channelState = await _workspace.LoadChannelStateAsync(channel, ct) ?? new ChannelState
        {
            ChannelId = channel,
            DisplayName = channelConfig.DisplayName
        };

        channelState.History.Insert(0, new VersionHistoryEntry
        {
            Version = version,
            Status = VersionStatus.Staged,
            BuiltAt = metadata.Build.BuiltAt,
            FileCount = metadata.Input.FileCount,
            TotalSize = metadata.Input.TotalSize
        });

        await _workspace.SaveChannelStateAsync(channel, channelState, ct);

        // Update workspace state
        var state = await _workspace.LoadStateAsync(ct);
        state.PendingPublish.RemoveAll(p => p.Channel == channel);
        state.PendingPublish.Add(new PendingPublishInfo
        {
            Channel = channel,
            Version = version,
            StagedAt = DateTime.UtcNow
        });

        state.RecentOperations.Insert(0, new RecentOperationInfo
        {
            Type = "build",
            Channel = channel,
            Version = version,
            Timestamp = DateTime.UtcNow,
            Status = "success"
        });

        if (state.RecentOperations.Count > 20)
        {
            state.RecentOperations = state.RecentOperations.Take(20).ToList();
        }

        await _workspace.SaveStateAsync(state, ct);

        // Report completion
        _app.PostMessage(new BuildCompleted(
            stopwatch.Elapsed,
            manifest.Files.Count,
            manifest.Files.Sum(f => f.Size)));
    }
}
