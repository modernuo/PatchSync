using System.Text.Json;
using PatchSync.CLI.Cdn;
using PatchSync.CLI.Config;
using PatchSync.CLI.Storage;
using PatchSync.CLI.TUI.Core;
using PatchSync.CLI.Workspace;

namespace PatchSync.CLI.TUI.Integration;

/// <summary>
/// Service that bridges TUI to S3 publishing.
/// Runs publish operations on background tasks and posts messages back to TUI.
/// </summary>
public sealed class TuiPublishService
{
    private readonly TuiApplication _app;
    private readonly WorkspaceManager _workspace;
    private CancellationTokenSource? _publishCts;

    public TuiPublishService(TuiApplication app, WorkspaceManager workspace)
    {
        _app = app;
        _workspace = workspace;
    }

    /// <summary>
    /// Start a publish operation on a background task.
    /// </summary>
    public void StartPublish(string channel, string version)
    {
        // Cancel any existing publish
        _publishCts?.Cancel();
        _publishCts = new CancellationTokenSource();

        var token = _publishCts.Token;

        // Run publish on background task
        _ = Task.Run(async () =>
        {
            try
            {
                await ExecutePublishAsync(channel, version, token);
            }
            catch (OperationCanceledException)
            {
                _app.PostMessage(new PublishFailed("Publish cancelled", 0));
            }
            catch (Exception ex)
            {
                _app.PostMessage(new PublishFailed(ex.Message, 0));
            }
        }, token);
    }

    /// <summary>
    /// Cancel the current publish operation.
    /// </summary>
    public void CancelPublish()
    {
        _publishCts?.Cancel();
    }

    private async Task ExecutePublishAsync(string channel, string version, CancellationToken ct)
    {
        var config = _workspace.Config;
        if (config == null)
        {
            _app.PostMessage(new PublishFailed("Workspace configuration not loaded", 0));
            return;
        }

        if (!config.Channels.TryGetValue(channel, out var channelConfig))
        {
            _app.PostMessage(new PublishFailed($"Channel not found: {channel}", 0));
            return;
        }

        // Load version metadata
        var versionMetadata = await _workspace.LoadVersionMetadataAsync(channel, version, ct);
        if (versionMetadata == null)
        {
            _app.PostMessage(new PublishFailed($"Version not found: {version}", 0));
            return;
        }

        // Resolve publish profile
        var profile = _workspace.GetPublishProfile(channel);
        if (profile == null)
        {
            _app.PostMessage(new PublishFailed($"No publish profile configured for channel: {channel}", 0));
            return;
        }

        // Get credentials
        var s3Config = await ResolveCredentialsAsync(profile);
        if (s3Config == null)
        {
            _app.PostMessage(new PublishFailed("Failed to resolve S3 credentials. Check environment variables or stored credentials.", 0));
            return;
        }

        // Build versioned remote path
        var channelPrefix = channelConfig.Publish?.Prefix ?? channel;
        var remotePath = $"{channelPrefix}/versions/{version}";

        // Collect files to upload
        var filesToUpload = new List<(string LocalPath, string RemotePath, string Category)>();
        var versionPath = _workspace.GetVersionPath(channel, version);
        var signaturesPath = _workspace.GetSignaturesPath(channel, version);
        var manifestPath = _workspace.GetManifestPath(channel, version);

        // Manifest
        if (File.Exists(manifestPath))
        {
            filesToUpload.Add((manifestPath, $"{remotePath}/manifest.json", "Manifest"));
        }
        else
        {
            _app.PostMessage(new PublishFailed($"Manifest not found: {manifestPath}", 0));
            return;
        }

        // Signatures
        if (Directory.Exists(signaturesPath))
        {
            foreach (var sigFile in Directory.GetFiles(signaturesPath, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(signaturesPath, sigFile).Replace('\\', '/');
                filesToUpload.Add((sigFile, $"{remotePath}/signatures/{relativePath}", "Signature"));
            }
        }

        // Calculate totals
        var totalSize = filesToUpload.Sum(f => new FileInfo(f.LocalPath).Length);
        var totalFiles = filesToUpload.Count;

        // Update version status to Publishing
        versionMetadata.Status = VersionStatus.Publishing;
        versionMetadata.Publish ??= new PublishInfo();
        versionMetadata.Publish.Profile = channelConfig.Publish?.Profile;
        versionMetadata.Publish.RemotePath = remotePath;
        versionMetadata.Publish.TotalBytes = totalSize;
        versionMetadata.Publish.LastUploadAt = DateTime.UtcNow;
        await _workspace.SaveVersionMetadataAsync(channel, version, versionMetadata, ct);

        // Create progress adapter
        var progress = new TuiPublishProgress(_app);
        progress.Initialize(totalFiles, totalSize);

        var failedFiles = 0;
        var uploadedBytes = 0L;
        var uploadedFiles = new List<UploadedFile>();

        try
        {
            using var s3Client = new S3Client(s3Config);

            foreach (var (localPath, remoteFilePath, category) in filesToUpload)
            {
                ct.ThrowIfCancellationRequested();

                var fileInfo = new FileInfo(localPath);
                progress.ReportFileStarted(Path.GetFileName(localPath));

                try
                {
                    var fileProgress = new Progress<UploadProgress>(p =>
                    {
                        progress.ReportBytesUploaded(p.BytesUploaded - uploadedBytes);
                        // Note: This may double-count bytes within a file, but TuiPublishProgress handles throttling
                    });

                    await s3Client.UploadFileAsync(localPath, remoteFilePath, progress: fileProgress, cancellationToken: ct);

                    // Get ETag after upload for verification
                    var info = await s3Client.GetObjectInfoAsync(remoteFilePath, ct);
                    uploadedFiles.Add(new UploadedFile
                    {
                        LocalPath = Path.GetRelativePath(versionPath, localPath).Replace('\\', '/'),
                        RemoteKey = remoteFilePath,
                        ETag = info?.ETag ?? "",
                        Size = fileInfo.Length
                    });

                    uploadedBytes += fileInfo.Length;
                    progress.ReportFileCompleted(Path.GetFileName(localPath), fileInfo.Length);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failedFiles++;
                    progress.ReportFileFailed(Path.GetFileName(localPath), ex.Message);
                }
            }

            if (failedFiles > 0)
            {
                versionMetadata.Status = VersionStatus.Failed;
                await _workspace.SaveVersionMetadataAsync(channel, version, versionMetadata, ct);
                _app.PostMessage(new PublishFailed($"{failedFiles} file(s) failed to upload", failedFiles));
                return;
            }

            // Update and upload channel.json
            await UpdateChannelJsonAsync(s3Client, channel, channelPrefix, version, versionMetadata, profile, ct);

            // Update version metadata
            versionMetadata.Status = VersionStatus.Live;
            versionMetadata.Publish.PublishedAt = DateTime.UtcNow;
            versionMetadata.Publish.Files = uploadedFiles;
            versionMetadata.Publish.UploadedBytes = uploadedBytes;
            if (!string.IsNullOrEmpty(profile.PublicUrl))
            {
                versionMetadata.Publish.ManifestUrl = $"{profile.PublicUrl.TrimEnd('/')}/{remotePath}/manifest.json";
            }
            await _workspace.SaveVersionMetadataAsync(channel, version, versionMetadata, ct);

            // Update channel state
            var channelState = await _workspace.LoadChannelStateAsync(channel, ct) ?? new ChannelState
            {
                ChannelId = channel,
                DisplayName = channelConfig.DisplayName,
                History = []
            };

            // Mark previous live version as superseded
            if (channelState.Current != null)
            {
                var prevVersion = channelState.Current.Version;
                var prevMetadata = await _workspace.LoadVersionMetadataAsync(channel, prevVersion, ct);
                if (prevMetadata != null)
                {
                    prevMetadata.Status = VersionStatus.Superseded;
                    await _workspace.SaveVersionMetadataAsync(channel, prevVersion, prevMetadata, ct);
                }
            }

            channelState.Current = new CurrentVersionInfo
            {
                Version = version,
                PublishedAt = DateTime.UtcNow,
                ManifestUrl = versionMetadata.Publish.ManifestUrl
            };
            await _workspace.SaveChannelStateAsync(channel, channelState, ct);

            // Report completion
            progress.ReportCompleted(versionMetadata.Publish.ManifestUrl ?? $"{remotePath}/manifest.json");
        }
        catch (OperationCanceledException)
        {
            versionMetadata.Status = VersionStatus.Failed;
            await _workspace.SaveVersionMetadataAsync(channel, version, versionMetadata, CancellationToken.None);
            throw;
        }
        catch (Exception)
        {
            versionMetadata.Status = VersionStatus.Failed;
            await _workspace.SaveVersionMetadataAsync(channel, version, versionMetadata, CancellationToken.None);
            throw;
        }
    }

    private async Task UpdateChannelJsonAsync(
        S3Client s3Client,
        string channelId,
        string channelPrefix,
        string version,
        VersionMetadata versionMetadata,
        PublishProfile profile,
        CancellationToken ct)
    {
        var channelJsonPath = $"{channelPrefix}/channel.json";

        // Create channel.json
        var versionInfo = new ChannelVersionInfo
        {
            Version = version,
            ManifestUrl = $"versions/{version}/manifest.json",
            PublishedAt = DateTime.UtcNow,
            Size = versionMetadata.Input.TotalSize,
            FileCount = versionMetadata.Input.FileCount
        };

        var channelJson = new ChannelJson
        {
            ChannelId = channelId,
            Current = versionInfo,
            UpdatedAt = DateTime.UtcNow,
            Available = [versionInfo]
        };

        // Upload channel.json
        var json = JsonSerializer.Serialize(channelJson, WorkspaceJsonContext.Default.ChannelJson);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        await s3Client.UploadStreamAsync(stream, channelJsonPath, "application/json", ct);

        // Purge CDN cache if configured
        await PurgeCdnCacheAsync(profile, channelPrefix);
    }

    private async Task PurgeCdnCacheAsync(PublishProfile profile, string channelPrefix)
    {
        if (profile.Cdn == null || !profile.Cdn.AutoPurge)
            return;

        if (string.IsNullOrEmpty(profile.Cdn.ZoneId))
            return;

        // Get Cloudflare API token from environment or credentials
        var apiToken = Environment.GetEnvironmentVariable("CLOUDFLARE_API_TOKEN");
        if (string.IsNullOrEmpty(apiToken))
        {
            var creds = CredentialManager.LoadCredentials();
            apiToken = creds?.CloudflareToken;
        }

        if (string.IsNullOrEmpty(apiToken))
            return;

        try
        {
            using var purgeClient = new CloudflarePurgeClient(profile.Cdn.ZoneId, apiToken);
            var channelJsonUrl = $"{profile.PublicUrl?.TrimEnd('/')}/{channelPrefix}/channel.json";
            await purgeClient.PurgeUrlsAsync([channelJsonUrl]);
        }
        catch
        {
            // Ignore CDN purge failures - not critical
        }
    }

    private static Task<S3Config?> ResolveCredentialsAsync(PublishProfile profile)
    {
        S3Config? s3Config = null;

        switch (profile.CredentialSource)
        {
            case "environment":
                s3Config = new S3Config
                {
                    Endpoint = profile.Endpoint,
                    Bucket = profile.Bucket,
                    Region = profile.Region ?? "us-east-1",
                    Prefix = profile.Prefix,
                    PublicUrl = profile.PublicUrl,
                    PathStyle = profile.PathStyle,
                    AccessKey = Environment.GetEnvironmentVariable(CredentialManager.EnvironmentVariables.AccessKey),
                    SecretKey = Environment.GetEnvironmentVariable(CredentialManager.EnvironmentVariables.SecretKey)
                };
                break;

            case "stored":
                var stored = CredentialManager.LoadCredentials();
                if (stored != null)
                {
                    s3Config = new S3Config
                    {
                        Endpoint = profile.Endpoint,
                        Bucket = profile.Bucket,
                        Region = profile.Region ?? stored.Region ?? "us-east-1",
                        Prefix = profile.Prefix,
                        PublicUrl = profile.PublicUrl,
                        PathStyle = profile.PathStyle,
                        AccessKey = stored.AccessKey,
                        SecretKey = stored.SecretKey
                    };
                }
                break;

            case "prompt":
                // TUI cannot prompt for credentials - use environment fallback
                break;
        }

        if (s3Config == null || !s3Config.IsComplete)
        {
            // Try environment variables as fallback
            var envAccess = Environment.GetEnvironmentVariable(CredentialManager.EnvironmentVariables.AccessKey);
            var envSecret = Environment.GetEnvironmentVariable(CredentialManager.EnvironmentVariables.SecretKey);

            if (!string.IsNullOrEmpty(envAccess) && !string.IsNullOrEmpty(envSecret))
            {
                s3Config = new S3Config
                {
                    Endpoint = profile.Endpoint,
                    Bucket = profile.Bucket,
                    Region = profile.Region ?? "us-east-1",
                    Prefix = profile.Prefix,
                    PublicUrl = profile.PublicUrl,
                    PathStyle = profile.PathStyle,
                    AccessKey = envAccess,
                    SecretKey = envSecret
                };
            }
        }

        return Task.FromResult(s3Config?.IsComplete == true ? s3Config : null);
    }
}
