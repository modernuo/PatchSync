using System.Text.Json;
using PatchSync.CLI.Cdn;
using PatchSync.CLI.Config;
using PatchSync.CLI.Storage;
using PatchSync.CLI.Workspace;
using Spectre.Console;

namespace PatchSync.CLI.Commands;

/// <summary>
/// Version management commands for listing, cleaning up, and rolling back versions.
/// </summary>
public static class VersionsCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var parser = new ArgParser(args);

        if (parser.HasHelp || args.Length == 0)
        {
            ShowHelp();
            return 0;
        }

        var subcommand = args.FirstOrDefault(a => !a.StartsWith("-")) ?? "";

        return subcommand.ToLowerInvariant() switch
        {
            "list" => await ListVersionsAsync(parser),
            "check" => await CheckRemoteVersionsAsync(parser),
            "cleanup" => await CleanupVersionsAsync(parser),
            "rollback" => await RollbackVersionAsync(parser),
            _ => ShowHelp()
        };
    }

    private static async Task<int> ListVersionsAsync(ArgParser parser)
    {
        var workspacePath = parser.Get("workspace", "w") ?? Directory.GetCurrentDirectory();
        var channelId = parser.Get("channel", "c");

        var workspace = WorkspaceManager.ForPath(workspacePath);
        if (!workspace.Exists)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] No workspace found at: {workspacePath}");
            return 1;
        }

        await workspace.LoadConfigAsync();
        var config = workspace.Config!;

        channelId ??= workspace.GetDefaultChannelId();
        if (string.IsNullOrEmpty(channelId))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No channel specified and no default channel configured.");
            return 1;
        }

        var versions = workspace.GetVersions(channelId).ToList();
        if (versions.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No versions found for channel: {channelId}[/]");
            return 0;
        }

        var table = new Table()
            .AddColumn("Version")
            .AddColumn("Status")
            .AddColumn("Built")
            .AddColumn("Published")
            .AddColumn("Size");

        foreach (var ver in versions)
        {
            var metadata = await workspace.LoadVersionMetadataAsync(channelId, ver);
            if (metadata == null) continue;

            var statusMarkup = metadata.Status switch
            {
                VersionStatus.Live => "[green]Live[/]",
                VersionStatus.Staged => "[yellow]Staged[/]",
                VersionStatus.Publishing => "[blue]Publishing[/]",
                VersionStatus.Superseded => "[grey]Superseded[/]",
                VersionStatus.Failed => "[red]Failed[/]",
                VersionStatus.Archived => "[grey]Archived[/]",
                _ => metadata.Status.ToString()
            };

            var builtAt = metadata.Build.BuiltAt.ToString("g");
            var publishedAt = metadata.Publish?.PublishedAt?.ToString("g") ?? "-";
            var size = FormatBytes(metadata.Input.TotalSize);

            table.AddRow(ver, statusMarkup, builtAt, publishedAt, size);
        }

        AnsiConsole.MarkupLine($"[blue]Channel:[/] {channelId}");
        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);

        return 0;
    }

    private static async Task<int> CheckRemoteVersionsAsync(ArgParser parser)
    {
        var workspacePath = parser.Get("workspace", "w") ?? Directory.GetCurrentDirectory();
        var channelId = parser.Get("channel", "c");

        var workspace = WorkspaceManager.ForPath(workspacePath);
        if (!workspace.Exists)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] No workspace found at: {workspacePath}");
            return 1;
        }

        await workspace.LoadConfigAsync();
        var config = workspace.Config!;

        channelId ??= workspace.GetDefaultChannelId();
        if (string.IsNullOrEmpty(channelId) || !config.Channels.TryGetValue(channelId, out var channelConfig))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Channel not found: {channelId}");
            return 1;
        }

        var profile = workspace.GetPublishProfile(channelId);
        if (profile == null)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] No publish profile for channel: {channelId}");
            return 1;
        }

        // Get credentials
        var s3Config = await ResolveCredentialsAsync(profile);
        if (s3Config == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Failed to resolve S3 credentials.");
            return 1;
        }

        var channelPrefix = channelConfig.Publish?.Prefix ?? channelId;

        await AnsiConsole.Status()
            .StartAsync("Checking remote versions...", async ctx =>
            {
                using var s3Client = new S3Client(s3Config);

                // List all version directories
                var objects = await s3Client.ListObjectsAsync($"{channelPrefix}/versions/");

                // Extract unique version directories
                var remoteVersions = objects
                    .Select(o => o.Key)
                    .Where(k => k.Contains("/versions/") && k.EndsWith("/manifest.json"))
                    .Select(k =>
                    {
                        var parts = k.Split('/');
                        var versionsIdx = Array.IndexOf(parts, "versions");
                        return versionsIdx >= 0 && versionsIdx + 1 < parts.Length
                            ? parts[versionsIdx + 1]
                            : null;
                    })
                    .Where(v => v != null)
                    .Distinct()
                    .ToList();

                ctx.Status("Building comparison...");

                var table = new Table()
                    .AddColumn("Version")
                    .AddColumn("Local")
                    .AddColumn("Remote")
                    .AddColumn("Status");

                var localVersions = workspace.GetVersions(channelId).ToHashSet();

                foreach (var ver in remoteVersions.Union(localVersions).OrderByDescending(v => v))
                {
                    var localExists = localVersions.Contains(ver!);
                    var remoteExists = remoteVersions.Contains(ver);

                    string status;
                    if (localExists && remoteExists)
                        status = "[green]Synced[/]";
                    else if (localExists && !remoteExists)
                        status = "[yellow]Not published[/]";
                    else
                        status = "[grey]Remote only[/]";

                    table.AddRow(
                        ver!,
                        localExists ? "[green]Yes[/]" : "[grey]No[/]",
                        remoteExists ? "[green]Yes[/]" : "[grey]No[/]",
                        status);
                }

                AnsiConsole.MarkupLine($"[blue]Channel:[/] {channelId}");
                AnsiConsole.MarkupLine($"[blue]Remote:[/] {profile.Endpoint}/{profile.Bucket}");
                AnsiConsole.WriteLine();
                AnsiConsole.Write(table);
            });

        return 0;
    }

    private static async Task<int> CleanupVersionsAsync(ArgParser parser)
    {
        var workspacePath = parser.Get("workspace", "w") ?? Directory.GetCurrentDirectory();
        var channelId = parser.Get("channel", "c");
        var keep = parser.GetInt("keep", 10, "k");
        var dryRun = parser.GetBool("dry-run");
        var remote = parser.GetBool("remote");

        var workspace = WorkspaceManager.ForPath(workspacePath);
        if (!workspace.Exists)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] No workspace found at: {workspacePath}");
            return 1;
        }

        await workspace.LoadConfigAsync();
        var config = workspace.Config!;

        channelId ??= workspace.GetDefaultChannelId();
        if (string.IsNullOrEmpty(channelId) || !config.Channels.TryGetValue(channelId, out var channelConfig))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Channel not found: {channelId}");
            return 1;
        }

        // Get all versions with metadata
        var versions = new List<(string Version, VersionMetadata? Metadata)>();
        foreach (var ver in workspace.GetVersions(channelId))
        {
            var metadata = await workspace.LoadVersionMetadataAsync(channelId, ver);
            versions.Add((ver, metadata));
        }

        // Sort by publish date (or build date if not published), keep newest
        var sortedVersions = versions
            .OrderByDescending(v => v.Metadata?.Publish?.PublishedAt ?? v.Metadata?.Build.BuiltAt ?? DateTime.MinValue)
            .ToList();

        var toDelete = sortedVersions.Skip(keep).ToList();

        if (toDelete.Count == 0)
        {
            AnsiConsole.MarkupLine($"[green]Nothing to clean up.[/] Keeping {sortedVersions.Count} versions (limit: {keep})");
            return 0;
        }

        AnsiConsole.MarkupLine($"[blue]Channel:[/] {channelId}");
        AnsiConsole.MarkupLine($"[blue]Keeping:[/] {keep} most recent versions");
        AnsiConsole.MarkupLine($"[yellow]Will delete:[/] {toDelete.Count} old versions");
        AnsiConsole.WriteLine();

        foreach (var (ver, metadata) in toDelete)
        {
            var status = metadata?.Status.ToString() ?? "Unknown";
            AnsiConsole.MarkupLine($"  [grey]{ver}[/] ({status})");
        }
        AnsiConsole.WriteLine();

        if (dryRun)
        {
            AnsiConsole.MarkupLine("[yellow]Dry run - no changes made.[/]");
            return 0;
        }

        if (!AnsiConsole.Confirm($"Delete {toDelete.Count} old versions?", false))
        {
            AnsiConsole.MarkupLine("[grey]Cleanup cancelled.[/]");
            return 0;
        }

        // Delete local versions
        foreach (var (ver, _) in toDelete)
        {
            var versionPath = workspace.GetVersionPath(channelId, ver);
            if (Directory.Exists(versionPath))
            {
                Directory.Delete(versionPath, recursive: true);
                AnsiConsole.MarkupLine($"[grey]Deleted local: {ver}[/]");
            }
        }

        // Delete remote versions if requested
        if (remote)
        {
            var profile = workspace.GetPublishProfile(channelId);
            if (profile != null)
            {
                var s3Config = await ResolveCredentialsAsync(profile);
                if (s3Config != null)
                {
                    using var s3Client = new S3Client(s3Config);
                    var channelPrefix = channelConfig.Publish?.Prefix ?? channelId;

                    foreach (var (ver, _) in toDelete)
                    {
                        var prefix = $"{channelPrefix}/versions/{ver}/";
                        var objects = await s3Client.ListObjectsAsync(prefix);
                        if (objects.Count > 0)
                        {
                            await s3Client.DeleteObjectsAsync(objects.Select(o => o.Key));
                            AnsiConsole.MarkupLine($"[grey]Deleted remote: {ver} ({objects.Count} objects)[/]");
                        }
                    }
                }
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]:check_mark_button: Cleanup complete![/]");
        AnsiConsole.MarkupLine($"[grey]Removed {toDelete.Count} old versions.[/]");
        return 0;
    }

    private static async Task<int> RollbackVersionAsync(ArgParser parser)
    {
        var workspacePath = parser.Get("workspace", "w") ?? Directory.GetCurrentDirectory();
        var channelId = parser.Get("channel", "c");
        var targetVersion = parser.Get("version", "v");

        var workspace = WorkspaceManager.ForPath(workspacePath);
        if (!workspace.Exists)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] No workspace found at: {workspacePath}");
            return 1;
        }

        await workspace.LoadConfigAsync();
        var config = workspace.Config!;

        channelId ??= workspace.GetDefaultChannelId();
        if (string.IsNullOrEmpty(channelId) || !config.Channels.TryGetValue(channelId, out var channelConfig))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Channel not found: {channelId}");
            return 1;
        }

        // Get available versions
        var versions = new List<(string Version, VersionMetadata Metadata)>();
        foreach (var ver in workspace.GetVersions(channelId))
        {
            var metadata = await workspace.LoadVersionMetadataAsync(channelId, ver);
            if (metadata?.Publish?.PublishedAt != null)
            {
                versions.Add((ver, metadata));
            }
        }

        if (versions.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No published versions available for rollback.");
            return 1;
        }

        // Select target version if not specified
        if (string.IsNullOrEmpty(targetVersion))
        {
            var versionPrompt = new SelectionPrompt<string>()
                .Title("[green]Select version to rollback to:[/]")
                .AddChoices(versions.Select(v =>
                    $"{v.Version} - published {v.Metadata.Publish!.PublishedAt:g}"));

            targetVersion = await versionPrompt.ShowAsync(AnsiConsole.Console, CancellationToken.None);
            targetVersion = targetVersion.Split(' ')[0];
        }

        var targetMetadata = versions.FirstOrDefault(v => v.Version == targetVersion).Metadata;
        if (targetMetadata == null)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Version not found or not published: {targetVersion}");
            return 1;
        }

        var profile = workspace.GetPublishProfile(channelId);
        if (profile == null)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] No publish profile for channel: {channelId}");
            return 1;
        }

        var s3Config = await ResolveCredentialsAsync(profile);
        if (s3Config == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Failed to resolve S3 credentials.");
            return 1;
        }

        AnsiConsole.MarkupLine($"[blue]Channel:[/] {channelId}");
        AnsiConsole.MarkupLine($"[yellow]Rolling back to:[/] {targetVersion}");
        AnsiConsole.WriteLine();

        if (!AnsiConsole.Confirm("Update channel.json to point to this version?", true))
        {
            AnsiConsole.MarkupLine("[grey]Rollback cancelled.[/]");
            return 0;
        }

        using var s3Client = new S3Client(s3Config);
        var channelPrefix = channelConfig.Publish?.Prefix ?? channelId;

        // Update channel.json
        var channelJson = new ChannelJson
        {
            ChannelId = channelId,
            Current = new ChannelVersionInfo
            {
                Version = targetVersion,
                ManifestUrl = $"versions/{targetVersion}/manifest.json",
                PublishedAt = targetMetadata.Publish!.PublishedAt,
                Size = targetMetadata.Input.TotalSize,
                FileCount = targetMetadata.Input.FileCount
            },
            Available = versions.Select(v => new ChannelVersionInfo
            {
                Version = v.Version,
                ManifestUrl = $"versions/{v.Version}/manifest.json",
                PublishedAt = v.Metadata.Publish!.PublishedAt,
                Size = v.Metadata.Input.TotalSize,
                FileCount = v.Metadata.Input.FileCount
            }).ToList(),
            UpdatedAt = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(channelJson, WorkspaceJsonContext.Default.ChannelJson);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        await s3Client.UploadStreamAsync(stream, $"{channelPrefix}/channel.json", "application/json");

        // Update local state
        var channelState = await workspace.LoadChannelStateAsync(channelId);
        if (channelState != null)
        {
            channelState.Current = new CurrentVersionInfo
            {
                Version = targetVersion,
                PublishedAt = DateTime.UtcNow,
                ManifestUrl = targetMetadata.Publish.ManifestUrl
            };
            await workspace.SaveChannelStateAsync(channelId, channelState);
        }

        // Mark current as superseded, target as live
        foreach (var (ver, metadata) in versions)
        {
            if (metadata.Status == VersionStatus.Live)
            {
                metadata.Status = VersionStatus.Superseded;
                await workspace.SaveVersionMetadataAsync(channelId, ver, metadata);
            }
        }

        targetMetadata.Status = VersionStatus.Live;
        await workspace.SaveVersionMetadataAsync(channelId, targetVersion, targetMetadata);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]:check_mark_button: Rollback complete![/]");
        AnsiConsole.WriteLine();

        var summaryTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Property")
            .AddColumn("Value");

        summaryTable.AddRow("Channel", channelId);
        summaryTable.AddRow("Version", targetVersion);
        summaryTable.AddRow("Status", "[green]Live[/]");

        AnsiConsole.Write(summaryTable);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Users will now receive this version.[/]");

        // Purge CDN cache if configured
        if (profile.Cdn?.AutoPurge == true && !string.IsNullOrEmpty(profile.Cdn.ZoneId))
        {
            var apiToken = Environment.GetEnvironmentVariable("CLOUDFLARE_API_TOKEN");
            if (!string.IsNullOrEmpty(apiToken))
            {
                using var purgeClient = new CloudflarePurgeClient(profile.Cdn.ZoneId, apiToken);
                var channelJsonUrl = $"{profile.PublicUrl?.TrimEnd('/')}/{channelPrefix}/channel.json";
                var result = await purgeClient.PurgeUrlsAsync([channelJsonUrl]);
                if (result.Success)
                {
                    AnsiConsole.MarkupLine("[green]Purged CDN cache[/]");
                }
            }
        }

        return 0;
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
        }

        // Try environment as fallback
        if (s3Config == null || !s3Config.IsComplete)
        {
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

    private static int ShowHelp()
    {
        AnsiConsole.MarkupLine("[bold]patchsync versions[/] - Manage versions");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Subcommands:[/]");
        AnsiConsole.MarkupLine("  list       List all versions for a channel");
        AnsiConsole.MarkupLine("  check      Check which versions are on remote");
        AnsiConsole.MarkupLine("  cleanup    Delete old versions (local and/or remote)");
        AnsiConsole.MarkupLine("  rollback   Update channel.json to point to older version");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Common Options:[/]");
        AnsiConsole.MarkupLine("  -w, --workspace <PATH>    Workspace directory (default: current)");
        AnsiConsole.MarkupLine("  -c, --channel <ID>        Channel to manage");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Cleanup Options:[/]");
        AnsiConsole.MarkupLine("  -k, --keep <N>            Keep N most recent versions (default: 10)");
        AnsiConsole.MarkupLine("      --dry-run             Show what would be deleted without deleting");
        AnsiConsole.MarkupLine("      --remote              Also delete from remote S3");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Rollback Options:[/]");
        AnsiConsole.MarkupLine("  -v, --version <VERSION>   Version to rollback to");
        return 0;
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
