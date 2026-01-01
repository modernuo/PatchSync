using System.Text.Json;
using PatchSync.CLI.Cdn;
using PatchSync.CLI.Config;
using PatchSync.CLI.Storage;
using PatchSync.CLI.Workspace;
using Spectre.Console;

namespace PatchSync.CLI.Commands;

/// <summary>
/// Workspace-integrated publish command for uploading versions to S3/CDN.
/// Uses versioned paths for cache safety.
/// </summary>
public static class PublishCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var parser = new ArgParser(args);

        if (parser.HasHelp)
        {
            ShowHelp();
            return 0;
        }

        try
        {
            var workspacePath = parser.Get("workspace", "w") ?? Directory.GetCurrentDirectory();
            var channelId = parser.Get("channel", "c");
            var version = parser.Get("version", "v");
            var force = parser.GetBool("force");
            var skipChannelJson = parser.GetBool("skip-channel-json");

            return await ExecuteAsync(workspacePath, channelId, version, force, skipChannelJson);
        }
        catch (ArgumentException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            AnsiConsole.WriteLine();
            ShowHelp();
            return 1;
        }
    }

    public static async Task<int> RunWizardAsync(WorkspaceManager? workspace = null)
    {
        // Find or use provided workspace
        workspace ??= WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());
        if (workspace == null || !workspace.Exists)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No workspace found. Run from a workspace directory or use --workspace.");
            return 1;
        }

        await workspace.LoadConfigAsync();
        var config = workspace.Config!;

        AnsiConsole.MarkupLine($"[blue]Workspace:[/] {config.Project.Name}");
        AnsiConsole.WriteLine();

        // Select channel
        var channels = config.Channels.Keys.ToList();
        if (channels.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No channels configured in workspace.");
            return 1;
        }

        var channelId = channels.Count == 1
            ? channels[0]
            : AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[green]Select channel to publish:[/]")
                    .AddChoices(channels));

        // Get publishable versions (staged or failed)
        var versions = workspace.GetVersions(channelId).ToList();
        var publishableVersions = new List<(string Version, VersionMetadata Metadata)>();

        foreach (var ver in versions)
        {
            var metadata = await workspace.LoadVersionMetadataAsync(channelId, ver);
            if (metadata != null && (metadata.Status == VersionStatus.Staged || metadata.Status == VersionStatus.Failed))
            {
                publishableVersions.Add((ver, metadata));
            }
        }

        if (publishableVersions.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No staged versions available to publish.[/]");
            AnsiConsole.MarkupLine("[grey]Build a version first using the build command.[/]");
            return 0;
        }

        // Select version
        var selectedVersion = publishableVersions.Count == 1
            ? publishableVersions[0].Version
            : AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[green]Select version to publish:[/]")
                    .AddChoices(publishableVersions.Select(v =>
                        $"{v.Version} ({v.Metadata.Status}) - built {v.Metadata.Build.BuiltAt:g}")));

        // Extract version string if it has metadata attached
        if (selectedVersion.Contains(" ("))
            selectedVersion = selectedVersion.Split(' ')[0];

        return await ExecuteAsync(workspace.WorkspacePath, channelId, selectedVersion, force: false, skipChannelJson: false);
    }

    private static async Task<int> ExecuteAsync(
        string workspacePath,
        string? channelId,
        string? version,
        bool force,
        bool skipChannelJson)
    {
        // Load workspace
        var workspace = WorkspaceManager.ForPath(workspacePath);
        if (!workspace.Exists)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] No workspace found at: {workspacePath}");
            return 1;
        }

        await workspace.LoadConfigAsync();
        var config = workspace.Config!;

        // Resolve channel
        channelId ??= workspace.GetDefaultChannelId();
        if (string.IsNullOrEmpty(channelId))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No channel specified and no default channel configured.");
            return 1;
        }

        if (!config.Channels.TryGetValue(channelId, out var channelConfig))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Channel not found: {channelId}");
            return 1;
        }

        // Resolve version
        if (string.IsNullOrEmpty(version))
        {
            // Get the most recent staged version
            var versions = workspace.GetVersions(channelId).ToList();
            foreach (var ver in versions)
            {
                var metadata = await workspace.LoadVersionMetadataAsync(channelId, ver);
                if (metadata?.Status == VersionStatus.Staged)
                {
                    version = ver;
                    break;
                }
            }

            if (string.IsNullOrEmpty(version))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] No staged versions available to publish.");
                return 1;
            }
        }

        // Load version metadata
        var versionMetadata = await workspace.LoadVersionMetadataAsync(channelId, version);
        if (versionMetadata == null)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Version not found: {version}");
            return 1;
        }

        // Resolve publish profile
        var profile = workspace.GetPublishProfile(channelId);
        if (profile == null)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] No publish profile configured for channel: {channelId}");
            AnsiConsole.MarkupLine("[grey]Configure a publish profile in the workspace settings.[/]");
            return 1;
        }

        // Get credentials
        var s3Config = await ResolveCredentialsAsync(profile);
        if (s3Config == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Failed to resolve S3 credentials.");
            return 1;
        }

        // Build versioned remote path
        var channelPrefix = channelConfig.Publish?.Prefix ?? channelId;
        var remotePath = $"{channelPrefix}/versions/{version}";
        var manifestRemotePath = $"{remotePath}/manifest.json";

        // Display publish summary
        AnsiConsole.MarkupLine($"[blue]Workspace:[/] {config.Project.Name}");
        AnsiConsole.MarkupLine($"[blue]Channel:[/] {channelId} ({channelConfig.DisplayName})");
        AnsiConsole.MarkupLine($"[blue]Version:[/] {version}");
        AnsiConsole.MarkupLine($"[blue]Target:[/] {s3Config.Bucket} / {remotePath}");
        if (!string.IsNullOrEmpty(profile.PublicUrl))
        {
            var publicPath = $"{profile.PublicUrl.TrimEnd('/')}/{remotePath}";
            AnsiConsole.MarkupLine($"[blue]CDN URL:[/] {publicPath}");
        }
        AnsiConsole.WriteLine();

        // Check if version already exists
        using var s3Client = new S3Client(s3Config);
        var existingManifest = await s3Client.GetObjectInfoAsync(manifestRemotePath);
        if (existingManifest != null && !force)
        {
            AnsiConsole.MarkupLine($"[yellow]Version {version} already exists on remote.[/]");
            AnsiConsole.MarkupLine($"  Last modified: {existingManifest.LastModified:g}");
            AnsiConsole.MarkupLine($"  Size: {FormatBytes(existingManifest.Size)}");
            AnsiConsole.WriteLine();

            if (!AnsiConsole.Confirm("Overwrite existing version?", false))
            {
                AnsiConsole.MarkupLine("[grey]Publish cancelled.[/]");
                return 0;
            }
        }

        // Collect files to upload
        var filesToUpload = new List<(string LocalPath, string RemotePath, string Category)>();
        var versionPath = workspace.GetVersionPath(channelId, version);
        var signaturesPath = workspace.GetSignaturesPath(channelId, version);
        var manifestPath = workspace.GetManifestPath(channelId, version);

        // Manifest
        if (File.Exists(manifestPath))
        {
            filesToUpload.Add((manifestPath, $"{remotePath}/manifest.json", "Manifest"));
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Manifest not found: {manifestPath}");
            return 1;
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
        var sigCount = filesToUpload.Count(f => f.Category == "Signature");

        AnsiConsole.MarkupLine($"[blue]Files to upload:[/]");
        AnsiConsole.MarkupLine($"  manifest.json ({FormatBytes(new FileInfo(manifestPath).Length)})");
        AnsiConsole.MarkupLine($"  signatures/ ({sigCount} files, {FormatBytes(filesToUpload.Where(f => f.Category == "Signature").Sum(f => new FileInfo(f.LocalPath).Length))})");
        if (!skipChannelJson)
        {
            AnsiConsole.MarkupLine($"  channel.json [yellow](will purge cache)[/]");
        }
        AnsiConsole.MarkupLine($"[blue]Total:[/] {FormatBytes(totalSize)}");
        AnsiConsole.WriteLine();

        // Update version status to Publishing
        versionMetadata.Status = VersionStatus.Publishing;
        versionMetadata.Publish ??= new PublishInfo();
        versionMetadata.Publish.Profile = channelConfig.Publish?.Profile;
        versionMetadata.Publish.RemotePath = remotePath;
        versionMetadata.Publish.TotalBytes = totalSize;
        versionMetadata.Publish.LastUploadAt = DateTime.UtcNow;
        await workspace.SaveVersionMetadataAsync(channelId, version, versionMetadata);

        try
        {
            // Upload files
            var uploadedFiles = new List<UploadedFile>();
            var uploadedBytes = 0L;

            await AnsiConsole.Progress()
                .AutoRefresh(true)
                .HideCompleted(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new TransferSpeedColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var overallTask = ctx.AddTask($"Publishing {version}");
                    overallTask.MaxValue = totalSize;

                    foreach (var (localPath, remoteFilePath, category) in filesToUpload)
                    {
                        var fileInfo = new FileInfo(localPath);
                        var fileName = Path.GetFileName(localPath);

                        var progress = new Progress<UploadProgress>(p =>
                        {
                            overallTask.Value = uploadedBytes + p.BytesUploaded;
                            overallTask.Description = $"[{category}] {fileName}";
                        });

                        await s3Client.UploadFileAsync(localPath, remoteFilePath, progress: progress);

                        // Get ETag after upload for verification
                        var info = await s3Client.GetObjectInfoAsync(remoteFilePath);
                        uploadedFiles.Add(new UploadedFile
                        {
                            LocalPath = Path.GetRelativePath(versionPath, localPath).Replace('\\', '/'),
                            RemoteKey = remoteFilePath,
                            ETag = info?.ETag ?? "",
                            Size = fileInfo.Length
                        });

                        uploadedBytes += fileInfo.Length;
                    }

                    overallTask.Value = totalSize;
                    overallTask.Description = "Upload complete";
                });

            // Update and upload channel.json
            if (!skipChannelJson)
            {
                await UpdateChannelJsonAsync(
                    s3Client, workspace, channelId, channelPrefix,
                    version, versionMetadata, profile);

                // Purge CDN cache for channel.json if configured
                await PurgeCdnCacheAsync(profile, channelPrefix);
            }

            // Update version metadata
            versionMetadata.Status = VersionStatus.Live;
            versionMetadata.Publish.PublishedAt = DateTime.UtcNow;
            versionMetadata.Publish.Files = uploadedFiles;
            versionMetadata.Publish.UploadedBytes = uploadedBytes;
            if (!string.IsNullOrEmpty(profile.PublicUrl))
            {
                versionMetadata.Publish.ManifestUrl = $"{profile.PublicUrl.TrimEnd('/')}/{remotePath}/manifest.json";
            }
            await workspace.SaveVersionMetadataAsync(channelId, version, versionMetadata);

            // Update channel state
            var channelState = await workspace.LoadChannelStateAsync(channelId) ?? new ChannelState
            {
                ChannelId = channelId,
                DisplayName = channelConfig.DisplayName,
                History = []
            };

            // Mark previous live version as superseded
            if (channelState.Current != null)
            {
                var prevVersion = channelState.Current.Version;
                var prevMetadata = await workspace.LoadVersionMetadataAsync(channelId, prevVersion);
                if (prevMetadata != null)
                {
                    prevMetadata.Status = VersionStatus.Superseded;
                    await workspace.SaveVersionMetadataAsync(channelId, prevVersion, prevMetadata);
                }
            }

            channelState.Current = new CurrentVersionInfo
            {
                Version = version,
                PublishedAt = DateTime.UtcNow,
                ManifestUrl = versionMetadata.Publish.ManifestUrl
            };
            await workspace.SaveChannelStateAsync(channelId, channelState);

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[green]:check_mark_button: Publish complete![/]");
            AnsiConsole.WriteLine();

            var summaryTable = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Property")
                .AddColumn("Value");

            summaryTable.AddRow("Version", version);
            summaryTable.AddRow("Channel", $"{channelConfig.DisplayName} ({channelId})");
            summaryTable.AddRow("Status", "[green]Live[/]");
            summaryTable.AddRow("Files", filesToUpload.Count.ToString());
            summaryTable.AddRow("Size", FormatBytes(totalSize));
            if (!string.IsNullOrEmpty(versionMetadata.Publish.ManifestUrl))
            {
                summaryTable.AddRow("Manifest URL", versionMetadata.Publish.ManifestUrl);
            }

            AnsiConsole.Write(summaryTable);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]Users will now receive this version.[/]");

            return 0;
        }
        catch (Exception ex)
        {
            // Mark as failed
            versionMetadata.Status = VersionStatus.Failed;
            await workspace.SaveVersionMetadataAsync(channelId, version, versionMetadata);

            AnsiConsole.MarkupLine($"[red]Publish failed:[/] {ex.Message}");
            return 1;
        }
    }

    private static async Task PurgeCdnCacheAsync(PublishProfile profile, string channelPrefix)
    {
        if (profile.Cdn == null || !profile.Cdn.AutoPurge)
            return;

        if (string.IsNullOrEmpty(profile.Cdn.ZoneId))
        {
            AnsiConsole.MarkupLine("[yellow]CDN auto-purge enabled but no zone ID configured[/]");
            return;
        }

        // Get Cloudflare API token from environment or credentials
        var apiToken = Environment.GetEnvironmentVariable("CLOUDFLARE_API_TOKEN");
        if (string.IsNullOrEmpty(apiToken))
        {
            var creds = CredentialManager.LoadCredentials();
            apiToken = creds?.CloudflareToken;
        }

        if (string.IsNullOrEmpty(apiToken))
        {
            AnsiConsole.MarkupLine("[yellow]CDN purge skipped: No Cloudflare API token found[/]");
            AnsiConsole.MarkupLine("[grey]Set CLOUDFLARE_API_TOKEN environment variable[/]");
            return;
        }

        try
        {
            using var purgeClient = new CloudflarePurgeClient(profile.Cdn.ZoneId, apiToken);

            // Build the full URL for channel.json
            var channelJsonUrl = $"{profile.PublicUrl?.TrimEnd('/')}/{channelPrefix}/channel.json";

            var result = await purgeClient.PurgeUrlsAsync([channelJsonUrl]);
            if (result.Success)
            {
                AnsiConsole.MarkupLine("[green]Purged CDN cache for channel.json[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[yellow]CDN purge failed:[/] {string.Join(", ", result.Errors)}");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]CDN purge error:[/] {ex.Message}");
        }
    }

    private static async Task UpdateChannelJsonAsync(
        S3Client s3Client,
        WorkspaceManager workspace,
        string channelId,
        string channelPrefix,
        string version,
        VersionMetadata versionMetadata,
        PublishProfile profile)
    {
        var channelJsonPath = $"{channelPrefix}/channel.json";

        // Try to load existing channel.json
        ChannelJson channelJson;
        try
        {
            var existing = await s3Client.GetObjectInfoAsync(channelJsonPath);
            if (existing != null)
            {
                // Download and parse existing
                // For now, create new - we'd need a download method
                channelJson = new ChannelJson
                {
                    ChannelId = channelId,
                    Current = null!,
                    Available = []
                };
            }
            else
            {
                channelJson = new ChannelJson
                {
                    ChannelId = channelId,
                    Current = null!,
                    Available = []
                };
            }
        }
        catch
        {
            channelJson = new ChannelJson
            {
                ChannelId = channelId,
                Current = null!,
                Available = []
            };
        }

        // Create version info
        var versionInfo = new ChannelVersionInfo
        {
            Version = version,
            ManifestUrl = $"versions/{version}/manifest.json",
            PublishedAt = DateTime.UtcNow,
            Size = versionMetadata.Input.TotalSize,
            FileCount = versionMetadata.Input.FileCount
        };

        // Update channel.json
        channelJson.Current = versionInfo;
        channelJson.UpdatedAt = DateTime.UtcNow;

        // Add to available list if not already present
        var existingIndex = channelJson.Available.FindIndex(v => v.Version == version);
        if (existingIndex >= 0)
        {
            channelJson.Available[existingIndex] = versionInfo;
        }
        else
        {
            channelJson.Available.Insert(0, versionInfo);
        }

        // Upload channel.json
        var json = JsonSerializer.Serialize(channelJson, WorkspaceJsonContext.Default.ChannelJson);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        await s3Client.UploadStreamAsync(stream, channelJsonPath, "application/json");

        AnsiConsole.MarkupLine("[grey]Updated channel.json[/]");

        // TODO: Trigger CDN cache purge for channel.json if configured
    }

    private static async Task<S3Config?> ResolveCredentialsAsync(PublishProfile profile)
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
                AnsiConsole.MarkupLine("[yellow]Enter S3 credentials:[/]");
                var accessKey = AnsiConsole.Prompt(
                    new TextPrompt<string>("Access Key ID:")
                        .Validate(k => !string.IsNullOrWhiteSpace(k)
                            ? ValidationResult.Success()
                            : ValidationResult.Error("Access key is required")));

                var secretKey = AnsiConsole.Prompt(
                    new TextPrompt<string>("Secret Access Key:")
                        .Secret()
                        .Validate(k => !string.IsNullOrWhiteSpace(k)
                            ? ValidationResult.Success()
                            : ValidationResult.Error("Secret key is required")));

                s3Config = new S3Config
                {
                    Endpoint = profile.Endpoint,
                    Bucket = profile.Bucket,
                    Region = profile.Region ?? "us-east-1",
                    Prefix = profile.Prefix,
                    PublicUrl = profile.PublicUrl,
                    PathStyle = profile.PathStyle,
                    AccessKey = accessKey,
                    SecretKey = secretKey
                };
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

        return s3Config?.IsComplete == true ? s3Config : null;
    }

    private static void ShowHelp()
    {
        AnsiConsole.MarkupLine("[bold]patchsync publish[/] - Publish a version to CDN");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Usage:[/]");
        AnsiConsole.MarkupLine("  patchsync publish [[options]]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Options:[/]");
        AnsiConsole.MarkupLine("  -w, --workspace <PATH>    Workspace directory (default: current)");
        AnsiConsole.MarkupLine("  -c, --channel <ID>        Channel to publish to");
        AnsiConsole.MarkupLine("  -v, --version <VERSION>   Version to publish");
        AnsiConsole.MarkupLine("      --force               Overwrite existing version without prompt");
        AnsiConsole.MarkupLine("      --skip-channel-json   Don't update channel.json");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]CDN Path Structure:[/]");
        AnsiConsole.MarkupLine("  {channel}/channel.json                      Version discovery");
        AnsiConsole.MarkupLine("  {channel}/versions/{version}/manifest.json  Game manifest");
        AnsiConsole.MarkupLine("  {channel}/versions/{version}/signatures/    Signature files");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Run 'patchsync publish' without args for interactive mode.[/]");
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
        };
    }
}
