using PatchSync.CLI.Build;
using PatchSync.CLI.Workspace;
using PatchSync.Common.Manifest;
using Spectre.Console;

namespace PatchSync.CLI.Commands;

/// <summary>
/// Command for managing files within a built version.
/// Subcommands: list, set-strategy, add, remove
/// </summary>
public static class FilesCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var parser = new ArgParser(args);

        if (parser.HasHelp)
        {
            ShowHelp();
            return 0;
        }

        var subCommand = parser.FirstPositional?.ToLowerInvariant();

        return subCommand switch
        {
            "list" or "ls" => await ListAsync(args[1..]),
            "set-strategy" or "strategy" => await SetStrategyAsync(args[1..]),
            "add" => await AddAsync(args[1..]),
            "remove" or "rm" => await RemoveAsync(args[1..]),
            null => await InteractiveAsync(),
            _ => ShowUnknownSubcommand(subCommand)
        };
    }

    private static async Task<int> ListAsync(string[] args)
    {
        var parser = new ArgParser(args);

        if (parser.HasHelp)
        {
            ShowListHelp();
            return 0;
        }

        var channel = parser.Get("channel", "c");
        var version = parser.Get("version", "v");
        var filter = parser.Get("filter", "f");
        var strategy = parser.Get("strategy", "s");
        var showOverrides = parser.GetBool("overrides", false);

        var workspace = WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());
        if (workspace == null || !workspace.Exists)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No workspace found. Run from a workspace directory.");
            return 1;
        }

        await workspace.LoadConfigAsync();

        // Default to first available channel/version if not specified
        if (string.IsNullOrEmpty(channel))
        {
            channel = workspace.GetDefaultChannelId();
            if (string.IsNullOrEmpty(channel))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] No channel specified and no default channel found.");
                return 1;
            }
        }

        if (string.IsNullOrEmpty(version))
        {
            var versions = workspace.GetVersions(channel).ToList();
            if (versions.Count == 0)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] No versions found for channel '{channel}'.");
                return 1;
            }
            version = versions.First();
        }

        var service = new FileOperationsService(workspace);
        var files = await service.ListFilesAsync(channel, version);

        // Apply filters
        if (!string.IsNullOrEmpty(filter))
        {
            files = files.Where(f => MatchesPattern(f.Path, filter)).ToList();
        }

        if (!string.IsNullOrEmpty(strategy) && Enum.TryParse<UpdateStrategy>(strategy, true, out var strategyFilter))
        {
            files = files.Where(f => f.Strategy == strategyFilter).ToList();
        }

        if (showOverrides)
        {
            files = files.Where(f => f.IsOverride).ToList();
        }

        // Display results
        AnsiConsole.MarkupLine($"[bold blue]Files in {channel}/{version}[/]");
        AnsiConsole.WriteLine();

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No files match the filter criteria.[/]");
            return 0;
        }

        var table = new Table();
        table.AddColumn("Path");
        table.AddColumn("Size", c => c.RightAligned());
        table.AddColumn("Strategy");
        if (showOverrides)
        {
            table.AddColumn("Override Reason");
        }

        foreach (var file in files.OrderBy(f => f.Path))
        {
            var strategyText = file.Strategy.ToString();
            if (file.IsOverride)
            {
                strategyText = $"[yellow]{strategyText}*[/]";
            }

            if (showOverrides)
            {
                table.AddRow(
                    file.Path,
                    FormatSize(file.Size),
                    strategyText,
                    file.OverrideReason ?? "-");
            }
            else
            {
                table.AddRow(file.Path, FormatSize(file.Size), strategyText);
            }
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        // Summary
        var totalSize = files.Sum(f => f.Size);
        var overrideCount = files.Count(f => f.IsOverride);
        AnsiConsole.MarkupLine($"[dim]{files.Count} files, {FormatSize(totalSize)} total[/]");
        if (overrideCount > 0)
        {
            AnsiConsole.MarkupLine($"[dim]{overrideCount} files with strategy overrides[/]");
        }

        return 0;
    }

    private static async Task<int> SetStrategyAsync(string[] args)
    {
        var parser = new ArgParser(args);

        if (parser.HasHelp)
        {
            ShowSetStrategyHelp();
            return 0;
        }

        var channel = parser.Get("channel", "c");
        var version = parser.Get("version", "v");
        var filePath = parser.FirstPositional;
        var strategyName = parser.Positional.Count > 1 ? parser.Positional[1] : parser.Get("strategy", "s");
        var reason = parser.Get("reason", "r");

        if (string.IsNullOrEmpty(filePath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No file path specified.");
            ShowSetStrategyHelp();
            return 1;
        }

        if (string.IsNullOrEmpty(strategyName))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No strategy specified.");
            ShowSetStrategyHelp();
            return 1;
        }

        if (!Enum.TryParse<UpdateStrategy>(strategyName, true, out var strategy))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Invalid strategy: {strategyName}");
            AnsiConsole.MarkupLine($"[dim]Valid strategies: {string.Join(", ", Enum.GetNames<UpdateStrategy>())}[/]");
            return 1;
        }

        var workspace = WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());
        if (workspace == null || !workspace.Exists)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No workspace found.");
            return 1;
        }

        await workspace.LoadConfigAsync();

        channel ??= workspace.GetDefaultChannelId();
        if (string.IsNullOrEmpty(channel))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No channel specified.");
            return 1;
        }

        if (string.IsNullOrEmpty(version))
        {
            version = workspace.GetVersions(channel).FirstOrDefault();
            if (version == null)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] No versions found for channel '{channel}'.");
                return 1;
            }
        }

        var service = new FileOperationsService(workspace);

        // Check if it's a pattern (contains * or ?)
        if (filePath.Contains('*') || filePath.Contains('?'))
        {
            var result = await service.SetStrategyBatchAsync(channel, version, filePath, strategy, reason);

            if (result.SuccessCount == 0 && result.FailedCount == 0)
            {
                AnsiConsole.MarkupLine($"[yellow]No files matched pattern:[/] {filePath}");
                return 0;
            }

            AnsiConsole.MarkupLine($"[green]Updated {result.SuccessCount} files to {strategy}[/]");
            if (result.FailedCount > 0)
            {
                AnsiConsole.MarkupLine($"[red]{result.FailedCount} files failed[/]");
            }
        }
        else
        {
            var result = await service.SetStrategyAsync(channel, version, filePath, strategy, reason);

            if (result.Success)
            {
                AnsiConsole.MarkupLine($"[green]{result.Message}[/]");
                if (result.PreviousStrategy.HasValue)
                {
                    AnsiConsole.MarkupLine($"[dim]{result.PreviousStrategy} -> {result.NewStrategy}[/]");
                }
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {result.Message}");
                return 1;
            }
        }

        return 0;
    }

    private static async Task<int> AddAsync(string[] args)
    {
        var parser = new ArgParser(args);

        if (parser.HasHelp)
        {
            ShowAddHelp();
            return 0;
        }

        var channel = parser.Get("channel", "c");
        var version = parser.Get("version", "v");
        var sourcePath = parser.FirstPositional;
        var targetPath = parser.Get("as");
        var strategyName = parser.Get("strategy", "s");

        if (string.IsNullOrEmpty(sourcePath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No source file specified.");
            ShowAddHelp();
            return 1;
        }

        if (!File.Exists(sourcePath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Source file not found: {sourcePath}");
            return 1;
        }

        UpdateStrategy? strategy = null;
        if (!string.IsNullOrEmpty(strategyName))
        {
            if (!Enum.TryParse<UpdateStrategy>(strategyName, true, out var parsed))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Invalid strategy: {strategyName}");
                return 1;
            }
            strategy = parsed;
        }

        var workspace = WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());
        if (workspace == null || !workspace.Exists)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No workspace found.");
            return 1;
        }

        await workspace.LoadConfigAsync();

        channel ??= workspace.GetDefaultChannelId();
        if (string.IsNullOrEmpty(channel))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No channel specified.");
            return 1;
        }

        if (string.IsNullOrEmpty(version))
        {
            version = workspace.GetVersions(channel).FirstOrDefault();
            if (version == null)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] No versions found for channel '{channel}'.");
                return 1;
            }
        }

        var service = new FileOperationsService(workspace);
        var result = await service.AddFileAsync(channel, version, sourcePath, targetPath, strategy);

        if (result.Success)
        {
            AnsiConsole.MarkupLine($"[green]Added:[/] {result.Path}");
            AnsiConsole.MarkupLine($"[dim]Strategy: {result.NewStrategy}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {result.Message}");
            return 1;
        }

        return 0;
    }

    private static async Task<int> RemoveAsync(string[] args)
    {
        var parser = new ArgParser(args);

        if (parser.HasHelp)
        {
            ShowRemoveHelp();
            return 0;
        }

        var channel = parser.Get("channel", "c");
        var version = parser.Get("version", "v");
        var filePath = parser.FirstPositional;
        var keepFile = parser.GetBool("keep", false);
        var force = parser.GetBool("force", false, "f");

        if (string.IsNullOrEmpty(filePath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No file path specified.");
            ShowRemoveHelp();
            return 1;
        }

        var workspace = WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());
        if (workspace == null || !workspace.Exists)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No workspace found.");
            return 1;
        }

        await workspace.LoadConfigAsync();

        channel ??= workspace.GetDefaultChannelId();
        if (string.IsNullOrEmpty(channel))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No channel specified.");
            return 1;
        }

        if (string.IsNullOrEmpty(version))
        {
            version = workspace.GetVersions(channel).FirstOrDefault();
            if (version == null)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] No versions found for channel '{channel}'.");
                return 1;
            }
        }

        // Confirm unless --force
        if (!force)
        {
            var confirm = AnsiConsole.Confirm($"Remove [yellow]{filePath}[/] from {channel}/{version}?", false);
            if (!confirm)
            {
                AnsiConsole.MarkupLine("[dim]Cancelled.[/]");
                return 0;
            }
        }

        var service = new FileOperationsService(workspace);
        var result = await service.RemoveFileAsync(channel, version, filePath, deletePhysicalFile: !keepFile);

        if (result.Success)
        {
            AnsiConsole.MarkupLine($"[green]Removed:[/] {result.Path}");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {result.Message}");
            return 1;
        }

        return 0;
    }

    private static async Task<int> InteractiveAsync()
    {
        var workspace = WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());
        if (workspace == null || !workspace.Exists)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No workspace found.");
            return 1;
        }

        await workspace.LoadConfigAsync();

        // Select channel
        var channels = workspace.Config!.Channels.Keys.ToList();
        if (channels.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No channels configured.");
            return 1;
        }

        var channel = channels.Count == 1
            ? channels[0]
            : AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select [green]channel[/]:")
                    .AddChoices(channels));

        // Select version
        var versions = workspace.GetVersions(channel).ToList();
        if (versions.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] No versions found for channel '{channel}'.");
            return 1;
        }

        var version = versions.Count == 1
            ? versions[0]
            : AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select [green]version[/]:")
                    .AddChoices(versions));

        var service = new FileOperationsService(workspace);
        var files = await service.ListFilesAsync(channel, version);

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No files in this version.[/]");
            return 0;
        }

        // Interactive file browser
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine($"[bold blue]Files in {channel}/{version}[/]");
            AnsiConsole.WriteLine();

            var choices = files
                .OrderBy(f => f.Path)
                .Select(f => $"{f.Path} [{f.Strategy}]{(f.IsOverride ? "*" : "")}")
                .Concat(["[Exit]"])
                .ToList();

            var selected = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select file to modify (or [yellow]Exit[/]):")
                    .PageSize(20)
                    .AddChoices(choices));

            if (selected == "[Exit]")
                break;

            // Extract path from selection
            var path = selected.Split(" [")[0];
            var file = files.FirstOrDefault(f => f.Path == path);
            if (file == null) continue;

            // Show file details and actions
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine($"[bold]{file.Path}[/]");
            AnsiConsole.MarkupLine($"Size: {FormatSize(file.Size)}");
            AnsiConsole.MarkupLine($"Hash: {file.Hash[..16]}...");
            AnsiConsole.MarkupLine($"Strategy: {file.Strategy}{(file.IsOverride ? " [yellow](override)[/]" : "")}");
            if (file.BaseHash != null)
            {
                AnsiConsole.MarkupLine($"Base Hash: {file.BaseHash[..16]}...");
            }
            AnsiConsole.WriteLine();

            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Action:")
                    .AddChoices(
                        "Change Strategy",
                        file.IsOverride ? "Remove Override" : "(No override to remove)",
                        "Remove File",
                        "Back"));

            if (action == "Back" || action == "(No override to remove)")
                continue;

            if (action == "Change Strategy")
            {
                var strategies = Enum.GetValues<UpdateStrategy>();
                var newStrategy = AnsiConsole.Prompt(
                    new SelectionPrompt<UpdateStrategy>()
                        .Title("Select new strategy:")
                        .AddChoices(strategies));

                var result = await service.SetStrategyAsync(channel, version, file.Path, newStrategy);
                AnsiConsole.MarkupLine(result.Success
                    ? $"[green]Strategy updated to {newStrategy}[/]"
                    : $"[red]{result.Message}[/]");

                // Refresh file list
                files = await service.ListFilesAsync(channel, version);
            }
            else if (action == "Remove Override" && file.IsOverride)
            {
                var result = await service.RemoveOverrideAsync(channel, version, file.Path);
                AnsiConsole.MarkupLine(result.Success
                    ? "[green]Override removed[/]"
                    : $"[red]{result.Message}[/]");

                files = await service.ListFilesAsync(channel, version);
            }
            else if (action == "Remove File")
            {
                var confirm = AnsiConsole.Confirm("Remove this file?", false);
                if (confirm)
                {
                    var result = await service.RemoveFileAsync(channel, version, file.Path);
                    AnsiConsole.MarkupLine(result.Success
                        ? "[green]File removed[/]"
                        : $"[red]{result.Message}[/]");

                    files = await service.ListFilesAsync(channel, version);
                }
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[dim]Press any key to continue...[/]");
            Console.ReadKey(true);
        }

        return 0;
    }

    private static bool MatchesPattern(string path, string pattern)
    {
        if (pattern == "*")
            return true;

        if (pattern.StartsWith("*."))
        {
            var ext = pattern[1..];
            return path.EndsWith(ext, StringComparison.OrdinalIgnoreCase);
        }

        if (pattern.EndsWith("/*"))
        {
            var dir = pattern[..^2];
            return path.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase);
        }

        return path.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static int ShowUnknownSubcommand(string subCommand)
    {
        AnsiConsole.MarkupLine($"[red]Error:[/] Unknown subcommand: {subCommand}");
        ShowHelp();
        return 1;
    }

    private static void ShowHelp()
    {
        AnsiConsole.MarkupLine("[bold]patchsync files[/] - Manage files in a built version");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Subcommands:[/]");
        AnsiConsole.MarkupLine("  list, ls          List files in a version");
        AnsiConsole.MarkupLine("  set-strategy      Set update strategy for files");
        AnsiConsole.MarkupLine("  add               Add a file to a version");
        AnsiConsole.MarkupLine("  remove, rm        Remove a file from a version");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Examples:[/]");
        AnsiConsole.MarkupLine("  patchsync files list -c prod -v 1.0.0");
        AnsiConsole.MarkupLine("  patchsync files set-strategy config.ini CreateOnly -c prod -v 1.0.0");
        AnsiConsole.MarkupLine("  patchsync files add readme.txt -c prod -v 1.0.0");
        AnsiConsole.MarkupLine("  patchsync files remove obsolete.dll -c prod -v 1.0.0");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Run [yellow]patchsync files <subcommand> --help[/] for subcommand details.");
    }

    private static void ShowListHelp()
    {
        AnsiConsole.MarkupLine("[bold]patchsync files list[/] - List files in a version");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Options:[/]");
        AnsiConsole.MarkupLine("  -c, --channel     Channel name");
        AnsiConsole.MarkupLine("  -v, --version     Version string");
        AnsiConsole.MarkupLine("  -f, --filter      Filter by path pattern (*.dll, data/*)");
        AnsiConsole.MarkupLine("  -s, --strategy    Filter by strategy (Delta, HashCheck, etc.)");
        AnsiConsole.MarkupLine("      --overrides   Show only files with strategy overrides");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Examples:[/]");
        AnsiConsole.MarkupLine("  patchsync files list -c prod -v 1.0.0");
        AnsiConsole.MarkupLine("  patchsync files list --filter *.uop");
        AnsiConsole.MarkupLine("  patchsync files list --strategy VirtualDelta");
    }

    private static void ShowSetStrategyHelp()
    {
        AnsiConsole.MarkupLine("[bold]patchsync files set-strategy[/] - Set update strategy for files");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Usage:[/]");
        AnsiConsole.MarkupLine("  patchsync files set-strategy <path> <strategy> [options]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Strategies:[/]");
        foreach (var s in Enum.GetNames<UpdateStrategy>())
        {
            AnsiConsole.MarkupLine($"  {s}");
        }
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Options:[/]");
        AnsiConsole.MarkupLine("  -c, --channel     Channel name");
        AnsiConsole.MarkupLine("  -v, --version     Version string");
        AnsiConsole.MarkupLine("  -r, --reason      Reason for override");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Examples:[/]");
        AnsiConsole.MarkupLine("  patchsync files set-strategy config.ini CreateOnly");
        AnsiConsole.MarkupLine("  patchsync files set-strategy *.uop VirtualDelta");
        AnsiConsole.MarkupLine("  patchsync files set-strategy data/* Delta --reason \"Large binary files\"");
    }

    private static void ShowAddHelp()
    {
        AnsiConsole.MarkupLine("[bold]patchsync files add[/] - Add a file to a version");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Usage:[/]");
        AnsiConsole.MarkupLine("  patchsync files add <source-path> [options]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Options:[/]");
        AnsiConsole.MarkupLine("  -c, --channel     Channel name");
        AnsiConsole.MarkupLine("  -v, --version     Version string");
        AnsiConsole.MarkupLine("      --as          Target path in version (default: file name)");
        AnsiConsole.MarkupLine("  -s, --strategy    Update strategy (default: auto-detect)");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Examples:[/]");
        AnsiConsole.MarkupLine("  patchsync files add readme.txt");
        AnsiConsole.MarkupLine("  patchsync files add C:/patch/new.dll --as bin/new.dll");
        AnsiConsole.MarkupLine("  patchsync files add config.ini --strategy CreateOnly");
    }

    private static void ShowRemoveHelp()
    {
        AnsiConsole.MarkupLine("[bold]patchsync files remove[/] - Remove a file from a version");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Usage:[/]");
        AnsiConsole.MarkupLine("  patchsync files remove <path> [options]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Options:[/]");
        AnsiConsole.MarkupLine("  -c, --channel     Channel name");
        AnsiConsole.MarkupLine("  -v, --version     Version string");
        AnsiConsole.MarkupLine("      --keep        Keep physical file (only remove from manifest)");
        AnsiConsole.MarkupLine("  -f, --force       Skip confirmation prompt");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Examples:[/]");
        AnsiConsole.MarkupLine("  patchsync files remove obsolete.dll");
        AnsiConsole.MarkupLine("  patchsync files remove temp/debug.log --force");
    }

    private static string FormatSize(long bytes)
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
