using PatchSync.CLI.Workspace;
using Spectre.Console;

namespace PatchSync.CLI.Commands;

public static class StatusCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var parser = new ArgParser(args);

        if (parser.HasHelp)
        {
            ShowHelp();
            return 0;
        }

        var channelFilter = parser.Get("channel", "c");
        var stagedOnly = parser.Has("staged");
        var jsonOutput = parser.Has("json");

        return await ExecuteAsync(channelFilter, stagedOnly, jsonOutput);
    }

    public static async Task<int> RunWizardAsync()
    {
        AnsiConsole.MarkupLine("[grey]Show workspace status and pending operations[/]\n");
        return await ExecuteAsync(null, false, false);
    }

    private static async Task<int> ExecuteAsync(string? channelFilter, bool stagedOnly, bool jsonOutput)
    {
        // Find workspace
        var manager = WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());
        if (manager == null || !manager.Exists)
        {
            AnsiConsole.MarkupLine("[red]No workspace found.[/]");
            AnsiConsole.MarkupLine("[grey]Run 'patchsync init' to create a new workspace.[/]");
            return 1;
        }

        try
        {
            var config = await manager.LoadConfigAsync();
            var state = await manager.LoadStateAsync();

            if (jsonOutput)
            {
                return await OutputJsonAsync(manager, config, state, channelFilter, stagedOnly);
            }

            // Show workspace header
            AnsiConsole.Write(new Rule($"[blue]{config.Project.Name}[/]").RuleStyle("grey"));
            AnsiConsole.MarkupLine($"[grey]Workspace:[/] {manager.WorkspacePath}");
            AnsiConsole.WriteLine();

            // Filter channels
            var channels = config.Channels.Keys.AsEnumerable();
            if (!string.IsNullOrEmpty(channelFilter))
            {
                if (!config.Channels.ContainsKey(channelFilter))
                {
                    AnsiConsole.MarkupLine($"[red]Channel not found:[/] {channelFilter}");
                    return 1;
                }
                channels = [channelFilter];
            }

            // Show channel status
            foreach (var channelId in channels)
            {
                await ShowChannelStatusAsync(manager, channelId, config.Channels[channelId], stagedOnly);
            }

            // Show pending operations
            if (state.PendingPublish.Count > 0)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.Write(new Rule("[yellow]Pending Publish[/]").RuleStyle("grey"));
                var table = new Table()
                    .AddColumn("Channel")
                    .AddColumn("Version")
                    .AddColumn("Staged At");

                foreach (var pending in state.PendingPublish)
                {
                    table.AddRow(
                        pending.Channel,
                        pending.Version,
                        pending.StagedAt.ToString("g"));
                }
                AnsiConsole.Write(table);
            }

            // Show recent operations
            if (state.RecentOperations.Count > 0)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.Write(new Rule("[grey]Recent Operations[/]").RuleStyle("grey"));
                var table = new Table()
                    .AddColumn("Type")
                    .AddColumn("Channel")
                    .AddColumn("Version")
                    .AddColumn("Status")
                    .AddColumn("Time");

                foreach (var op in state.RecentOperations.Take(5))
                {
                    var statusColor = op.Status == "success" ? "green" : "red";
                    table.AddRow(
                        op.Type,
                        op.Channel,
                        op.Version,
                        $"[{statusColor}]{op.Status}[/]",
                        op.Timestamp.ToString("g"));
                }
                AnsiConsole.Write(table);
            }

            return 0;
        }
        catch (WorkspaceException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
    }

    private static async Task ShowChannelStatusAsync(
        WorkspaceManager manager,
        string channelId,
        ChannelConfig config,
        bool stagedOnly)
    {
        var channelState = await manager.LoadChannelStateAsync(channelId);
        var versions = manager.GetVersions(channelId).ToList();

        // Channel header with status indicator
        var statusIcon = channelState?.Current != null ? ":green_circle:" : ":white_circle:";
        AnsiConsole.MarkupLine($"{statusIcon} [bold]{config.DisplayName}[/] ([grey]{channelId}[/])");

        if (channelState?.Current != null)
        {
            AnsiConsole.MarkupLine($"  [green]Live:[/] {channelState.Current.Version}");
            if (!string.IsNullOrEmpty(channelState.Current.ManifestUrl))
            {
                AnsiConsole.MarkupLine($"  [grey]URL:[/] {channelState.Current.ManifestUrl}");
            }
        }
        else
        {
            AnsiConsole.MarkupLine("  [grey]No published version[/]");
        }

        // List recent versions
        if (versions.Count > 0)
        {
            var versionTable = new Table()
                .Border(TableBorder.Simple)
                .AddColumn("Version")
                .AddColumn("Status")
                .AddColumn("Built")
                .AddColumn("Files")
                .AddColumn("Size");

            var displayCount = 0;
            foreach (var version in versions.Take(5))
            {
                var metadata = await manager.LoadVersionMetadataAsync(channelId, version);
                if (metadata == null) continue;

                if (stagedOnly && metadata.Status != VersionStatus.Staged)
                    continue;

                var statusColor = metadata.Status switch
                {
                    VersionStatus.Live => "green",
                    VersionStatus.Staged => "yellow",
                    VersionStatus.Publishing => "blue",
                    VersionStatus.Superseded => "grey",
                    VersionStatus.Failed => "red",
                    _ => "white"
                };

                versionTable.AddRow(
                    version,
                    $"[{statusColor}]{metadata.Status}[/]",
                    metadata.Build.BuiltAt.ToString("g"),
                    metadata.Input.FileCount.ToString(),
                    FormatBytes(metadata.Input.TotalSize));

                displayCount++;
            }

            if (displayCount > 0)
            {
                AnsiConsole.Write(versionTable);
            }

            if (versions.Count > 5)
            {
                AnsiConsole.MarkupLine($"  [grey]... and {versions.Count - 5} more versions[/]");
            }
        }

        AnsiConsole.WriteLine();
    }

    private static async Task<int> OutputJsonAsync(
        WorkspaceManager manager,
        WorkspaceConfig config,
        WorkspaceState state,
        string? channelFilter,
        bool stagedOnly)
    {
        // Build JSON manually to avoid reflection-based serialization (AOT-compatible)
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"workspace\": \"{EscapeJson(manager.WorkspacePath)}\",");
        sb.AppendLine($"  \"project\": {{");
        sb.AppendLine($"    \"name\": \"{EscapeJson(config.Project.Name)}\",");
        sb.AppendLine($"    \"id\": \"{EscapeJson(config.Project.Id)}\"");
        sb.AppendLine($"  }},");
        sb.AppendLine($"  \"channels\": {{");

        var channels = config.Channels.Keys.ToList();
        if (!string.IsNullOrEmpty(channelFilter))
            channels = [channelFilter];

        for (int i = 0; i < channels.Count; i++)
        {
            var channelId = channels[i];
            var channelState = await manager.LoadChannelStateAsync(channelId);
            var channelConfig = config.Channels[channelId];

            sb.AppendLine($"    \"{channelId}\": {{");
            sb.AppendLine($"      \"displayName\": \"{EscapeJson(channelConfig.DisplayName)}\",");
            sb.AppendLine($"      \"current\": {(channelState?.Current?.Version != null ? $"\"{channelState.Current.Version}\"" : "null")},");
            sb.AppendLine($"      \"versions\": [");

            var versions = manager.GetVersions(channelId).Take(10).ToList();
            var versionIndex = 0;
            foreach (var version in versions)
            {
                var metadata = await manager.LoadVersionMetadataAsync(channelId, version);
                if (metadata == null) continue;

                if (stagedOnly && metadata.Status != VersionStatus.Staged)
                    continue;

                if (versionIndex > 0) sb.AppendLine(",");
                sb.AppendLine($"        {{");
                sb.AppendLine($"          \"version\": \"{EscapeJson(metadata.Version)}\",");
                sb.AppendLine($"          \"status\": \"{metadata.Status.ToString().ToLowerInvariant()}\",");
                sb.AppendLine($"          \"builtAt\": \"{metadata.Build.BuiltAt:O}\",");
                sb.AppendLine($"          \"fileCount\": {metadata.Input.FileCount},");
                sb.Append($"          \"totalSize\": {metadata.Input.TotalSize}");
                sb.AppendLine($"        }}");
                versionIndex++;
            }

            sb.AppendLine($"      ]");
            sb.Append($"    }}");
            if (i < channels.Count - 1) sb.AppendLine(",");
            else sb.AppendLine();
        }

        sb.AppendLine($"  }}");
        sb.AppendLine("}");

        Console.Write(sb.ToString());
        return 0;
    }

    private static string EscapeJson(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
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

    private static void ShowHelp()
    {
        AnsiConsole.MarkupLine("[yellow]Usage:[/] patchsync status [options]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Options:[/]");
        AnsiConsole.MarkupLine("  -c, --channel <name>  Filter by channel");
        AnsiConsole.MarkupLine("  --staged              Show only staged (unpublished) versions");
        AnsiConsole.MarkupLine("  --json                Output as JSON");
        AnsiConsole.MarkupLine("  -h, --help            Show this help message");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Examples:[/]");
        AnsiConsole.MarkupLine("  patchsync status");
        AnsiConsole.MarkupLine("  patchsync status -c prod");
        AnsiConsole.MarkupLine("  patchsync status --staged --json");
    }
}
