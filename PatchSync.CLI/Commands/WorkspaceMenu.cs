using PatchSync.CLI.Workspace;
using Spectre.Console;

namespace PatchSync.CLI.Commands;

/// <summary>
/// Interactive menu for workspace operations.
/// Shows after a workspace is opened or created.
/// </summary>
public static class WorkspaceMenu
{
    /// <summary>
    /// Shows the main interactive menu.
    /// Checks for existing workspace or prompts to create/open one.
    /// </summary>
    public static async Task<int> ShowMainMenuAsync()
    {
        // Try to find workspace in current directory
        var workspace = WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());

        if (workspace != null)
        {
            // Workspace found - go directly to workspace menu
            return await ShowWorkspaceMenuAsync(workspace);
        }

        // No workspace - show workspace selector
        return await ShowWorkspaceSelectorAsync();
    }

    /// <summary>
    /// Shows workspace selector when no workspace is active.
    /// </summary>
    private static async Task<int> ShowWorkspaceSelectorAsync()
    {
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(
                new FigletText("PatchSync")
                    .Color(Color.Blue));
            AnsiConsole.MarkupLine("[grey]Delta patching tool for game updates[/]\n");

            var choices = new[]
            {
                ":sparkles: Create new workspace",
                ":file_folder: Open existing workspace",
                ":wrench: Standalone tools (no workspace)",
                ":cross_mark: Exit"
            };

            var selection = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[yellow]What would you like to do?[/]")
                    .PageSize(10)
                    .HighlightStyle(new Style(Color.Green))
                    .AddChoices(choices));

            if (selection.Contains("Create new"))
            {
                AnsiConsole.Clear();
                AnsiConsole.MarkupLine("[bold blue]:sparkles: CREATE WORKSPACE[/]\n");

                var (exitCode, workspace) = await InitCommand.RunWizardAndGetWorkspaceAsync();
                if (exitCode == 0 && workspace != null)
                {
                    // Init succeeded - open the workspace directly
                    return await ShowWorkspaceMenuAsync(workspace);
                }
                // If init failed or was cancelled, loop back to selector
            }
            else if (selection.Contains("Open existing"))
            {
                var workspace = await BrowseForWorkspaceAsync();
                if (workspace != null)
                {
                    return await ShowWorkspaceMenuAsync(workspace);
                }
                // If browse cancelled, loop back to selector
            }
            else if (selection.Contains("Standalone"))
            {
                return await ShowStandaloneMenuAsync();
            }
            else if (selection.Contains("Exit"))
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// Browse for an existing workspace.
    /// </summary>
    private static Task<WorkspaceManager?> BrowseForWorkspaceAsync()
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[bold blue]:file_folder: OPEN WORKSPACE[/]\n");

        var pathInput = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]Workspace directory[/] [grey](Enter to browse)[/]:")
                .AllowEmpty());

        string path;
        if (string.IsNullOrWhiteSpace(pathInput))
        {
            path = Prompts.Browse.ForFolder("[green]Select workspace directory[/]");
        }
        else
        {
            path = Path.GetFullPath(pathInput);
        }

        var manager = WorkspaceManager.ForPath(path);
        if (!manager.Exists)
        {
            AnsiConsole.MarkupLine($"[red]No workspace found at:[/] {path}");
            AnsiConsole.MarkupLine("[grey]Press any key to continue...[/]");
            Console.ReadKey(true);
            return Task.FromResult<WorkspaceManager?>(null);
        }

        return Task.FromResult<WorkspaceManager?>(manager);
    }

    /// <summary>
    /// Shows the workspace operations menu.
    /// Loops until user chooses to exit or switch workspaces.
    /// </summary>
    public static async Task<int> ShowWorkspaceMenuAsync(WorkspaceManager workspace)
    {
        // Load config for display
        WorkspaceConfig? config = null;
        try
        {
            config = await workspace.LoadConfigAsync();
        }
        catch
        {
            // Config might be invalid - we'll show limited options
        }

        while (true)
        {
            AnsiConsole.Clear();

            // Show consistent PatchSync header
            AnsiConsole.Write(
                new FigletText("PatchSync")
                    .Color(Color.Blue));

            // Show workspace info: name (path)
            var projectName = config?.Project.Name ?? "Unknown Project";
            AnsiConsole.MarkupLine($"[grey]Workspace:[/] [blue]{projectName}[/] [grey]({workspace.WorkspacePath})[/]");
            AnsiConsole.WriteLine();

            // Show quick status
            if (config != null)
            {
                await ShowQuickStatusAsync(workspace, config);
            }

            var choices = new List<string>
            {
                ":hammer: Build new version",
                ":bar_chart: View status",
                ":outbox_tray: Publish version",
                ":check_mark_button: Verify installation",
                ":hammer_and_wrench:  Settings",
                "",
                ":file_folder: Switch workspace",
                ":cross_mark: Exit"
            };

            var selection = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[yellow]What would you like to do?[/]")
                    .PageSize(12)
                    .HighlightStyle(new Style(Color.Green))
                    .AddChoices(choices.Where(c => !string.IsNullOrEmpty(c))));

            var result = await HandleWorkspaceMenuSelectionAsync(selection, workspace, config);

            if (result == -1) // Switch workspace
            {
                return await ShowWorkspaceSelectorAsync();
            }
            if (result == -2) // Exit
            {
                return 0;
            }
            // Otherwise, loop back to menu
        }
    }

    private static async Task<int> HandleWorkspaceMenuSelectionAsync(
        string selection,
        WorkspaceManager workspace,
        WorkspaceConfig? config)
    {
        if (selection.Contains("Build"))
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine("[bold blue]:hammer: BUILD NEW VERSION[/]\n");
            await RunWorkspaceBuildAsync(workspace, config);
            WaitForKey();
            return 0;
        }

        if (selection.Contains("status"))
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine("[bold blue]:bar_chart: WORKSPACE STATUS[/]\n");
            await StatusCommand.RunWizardAsync();
            WaitForKey();
            return 0;
        }

        if (selection.Contains("Publish"))
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine("[bold blue]:outbox_tray: PUBLISH VERSION[/]\n");
            await RunWorkspacePublishAsync(workspace, config);
            WaitForKey();
            return 0;
        }

        if (selection.Contains("Verify"))
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine("[bold blue]:check_mark_button: VERIFY INSTALLATION[/]\n");
            await VerifyCommand.RunWizardAsync();
            WaitForKey();
            return 0;
        }

        if (selection.Contains("Settings"))
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine("[bold blue]:hammer_and_wrench:  WORKSPACE SETTINGS[/]\n");
            await ShowSettingsMenuAsync(workspace, config);
            return 0;
        }

        if (selection.Contains("Switch"))
        {
            return -1;
        }

        if (selection.Contains("Exit"))
        {
            return -2;
        }

        return 0;
    }

    private static async Task ShowQuickStatusAsync(WorkspaceManager workspace, WorkspaceConfig config)
    {
        try
        {
            var state = await workspace.LoadStateAsync();

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Channel")
                .AddColumn("Latest")
                .AddColumn("Status");

            foreach (var (channelId, channelConfig) in config.Channels)
            {
                try
                {
                    var channelState = await workspace.LoadChannelStateAsync(channelId);
                    var version = channelState.Current?.Version ?? "-";
                    var status = channelState.Current != null ? "[green]Live[/]" : "[grey]No versions[/]";

                    // Check for pending publish
                    var pending = state.PendingPublish.FirstOrDefault(p => p.Channel == channelId);
                    if (pending != null)
                    {
                        version = pending.Version;
                        status = "[yellow]Staged[/]";
                    }

                    table.AddRow(
                        channelConfig.DisplayName ?? channelId,
                        version,
                        status);
                }
                catch
                {
                    table.AddRow(channelConfig.DisplayName ?? channelId, "-", "[grey]Unknown[/]");
                }
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
        }
        catch
        {
            // Ignore status errors
        }
    }

    private static async Task RunWorkspaceBuildAsync(WorkspaceManager workspace, WorkspaceConfig? config)
    {
        if (config == null)
        {
            AnsiConsole.MarkupLine("[red]Cannot load workspace configuration.[/]");
            return;
        }

        // Select channel
        var channels = config.Channels.Keys.ToList();
        if (channels.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No channels configured in workspace.[/]");
            return;
        }

        var channel = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[green]Select channel:[/]")
                .AddChoices(channels));

        // Get version
        var channelConfig = config.Channels[channel];
        var version = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]Version:[/]")
                .Validate(v =>
                {
                    if (string.IsNullOrWhiteSpace(v))
                        return ValidationResult.Error("Version is required");
                    return ValidationResult.Success();
                }));

        // Validate version pattern
        if (!workspace.ValidateVersionPattern(channel, version))
        {
            AnsiConsole.MarkupLine($"[yellow]Warning:[/] Version '{version}' doesn't match channel pattern: {channelConfig.VersionPattern}");
            if (!await AnsiConsole.ConfirmAsync("Continue anyway?", defaultValue: false))
            {
                return;
            }
        }

        // Get input path
        var defaultInput = config.Defaults?.InputPath ?? "";
        var inputPrompt = string.IsNullOrEmpty(defaultInput)
            ? "[green]Input directory:[/]"
            : $"[green]Input directory[/] [[{defaultInput}]]:";

        var inputPath = AnsiConsole.Prompt(
            new TextPrompt<string>(inputPrompt)
                .AllowEmpty());

        if (string.IsNullOrWhiteSpace(inputPath))
            inputPath = defaultInput;

        if (string.IsNullOrWhiteSpace(inputPath) || !Directory.Exists(inputPath))
        {
            AnsiConsole.MarkupLine("[red]Invalid input directory.[/]");
            return;
        }

        // Run build
        var args = new[] { "-c", channel, "-v", version, "-i", inputPath };
        await BuildCommand.RunAsync(args);
    }

    private static async Task RunWorkspacePublishAsync(WorkspaceManager workspace, WorkspaceConfig? config)
    {
        if (config == null)
        {
            AnsiConsole.MarkupLine("[red]Cannot load workspace configuration.[/]");
            return;
        }

        // Load state to find staged versions
        var state = await workspace.LoadStateAsync();

        if (state.PendingPublish.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No versions are staged for publishing.[/]");
            AnsiConsole.MarkupLine("[grey]Build a new version first.[/]");
            return;
        }

        // Select version to publish
        var choices = state.PendingPublish
            .Select(p => $"{p.Channel}/{p.Version} (staged {p.StagedAt:g})")
            .ToList();

        var selection = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[green]Select version to publish:[/]")
                .AddChoices(choices));

        var parts = selection.Split('/');
        var channel = parts[0];
        var version = parts[1].Split(' ')[0]; // Remove the staged date part

        // Get publish profile
        var channelConfig = config.Channels.GetValueOrDefault(channel);
        var profileName = channelConfig?.Publish?.Profile;

        if (string.IsNullOrEmpty(profileName) || !config.PublishProfiles.TryGetValue(profileName, out var profile))
        {
            AnsiConsole.MarkupLine($"[red]No publish profile configured for channel '{channel}'.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[blue]Publishing {channel}/{version} to {profile.Bucket}...[/]");
        AnsiConsole.MarkupLine("[yellow]Note: Upload functionality not yet implemented for workspace mode.[/]");
    }

    private static async Task ShowSettingsMenuAsync(WorkspaceManager workspace, WorkspaceConfig? config)
    {
        var choices = new[]
        {
            ":memo: Edit configuration file",
            ":open_file_folder: Open workspace folder",
            ":information:  View configuration",
            ":left_arrow: Back"
        };

        var selection = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[yellow]Settings:[/]")
                .AddChoices(choices));

        if (selection.Contains("Edit configuration"))
        {
            var configPath = workspace.ConfigPath;
            AnsiConsole.MarkupLine($"[blue]Configuration file:[/] {configPath}");
            AnsiConsole.MarkupLine("[grey]Open this file in your preferred editor to modify settings.[/]");
            WaitForKey();
        }
        else if (selection.Contains("Open workspace"))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = workspace.WorkspacePath,
                    UseShellExecute = true
                });
            }
            catch
            {
                AnsiConsole.MarkupLine($"[blue]Workspace folder:[/] {workspace.WorkspacePath}");
            }
            await Task.CompletedTask;
        }
        else if (selection.Contains("View configuration"))
        {
            if (config != null)
            {
                AnsiConsole.MarkupLine("[blue]Workspace Configuration:[/]\n");

                var tree = new Tree($"[bold]{config.Project.Name}[/]");

                var projectNode = tree.AddNode("[yellow]Project[/]");
                projectNode.AddNode($"ID: {config.Project.Id}");
                if (!string.IsNullOrEmpty(config.Project.Description))
                    projectNode.AddNode($"Description: {config.Project.Description}");

                var channelsNode = tree.AddNode("[yellow]Channels[/]");
                foreach (var (id, ch) in config.Channels)
                {
                    var chNode = channelsNode.AddNode($"[green]{id}[/] - {ch.DisplayName}");
                    if (ch.IsDefault) chNode.AddNode("[grey]Default channel[/]");
                    chNode.AddNode($"Retain: {ch.RetainVersions} versions");
                }

                var profilesNode = tree.AddNode("[yellow]Publish Profiles[/]");
                foreach (var (id, p) in config.PublishProfiles)
                {
                    profilesNode.AddNode($"[green]{id}[/] - {p.Type}://{p.Bucket}");
                }

                AnsiConsole.Write(tree);
            }
            WaitForKey();
        }
    }

    /// <summary>
    /// Shows standalone tools menu (no workspace).
    /// </summary>
    private static async Task<int> ShowStandaloneMenuAsync()
    {
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(
                new FigletText("PatchSync")
                    .Color(Color.Blue));
            AnsiConsole.MarkupLine("[grey]Standalone tools (no workspace)[/]\n");

            var choices = new[]
            {
                ":hammer: Build signatures",
                ":inbox_tray: Patch installation",
                ":check_mark_button: Verify files",
                ":outbox_tray: Upload to storage",
                ":red_question_mark: Inspect files",
                "",
                ":left_arrow: Back to main menu",
                ":cross_mark: Exit"
            };

            var selection = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[yellow]Select a tool:[/]")
                    .PageSize(12)
                    .HighlightStyle(new Style(Color.Green))
                    .AddChoices(choices.Where(c => !string.IsNullOrEmpty(c))));

            if (selection.Contains("Build"))
            {
                AnsiConsole.Clear();
                AnsiConsole.MarkupLine("[bold blue]:hammer: BUILD SIGNATURES[/]\n");
                await BuildCommand.RunWizardAsync();
                WaitForKey();
            }
            else if (selection.Contains("Patch"))
            {
                AnsiConsole.Clear();
                AnsiConsole.MarkupLine("[bold blue]:inbox_tray: PATCH INSTALLATION[/]\n");
                await PatchCommand.RunWizardAsync();
                WaitForKey();
            }
            else if (selection.Contains("Verify"))
            {
                AnsiConsole.Clear();
                AnsiConsole.MarkupLine("[bold blue]:check_mark_button: VERIFY FILES[/]\n");
                await VerifyCommand.RunWizardAsync();
                WaitForKey();
            }
            else if (selection.Contains("Upload"))
            {
                AnsiConsole.Clear();
                AnsiConsole.MarkupLine("[bold blue]:outbox_tray: UPLOAD[/]\n");
                await UploadCommand.RunWizardAsync();
                WaitForKey();
            }
            else if (selection.Contains("Inspect"))
            {
                AnsiConsole.Clear();
                AnsiConsole.MarkupLine("[bold blue]:red_question_mark: INSPECT FILES[/]\n");
                await InfoCommand.RunWizardAsync();
                WaitForKey();
            }
            else if (selection.Contains("Back"))
            {
                return await ShowWorkspaceSelectorAsync();
            }
            else if (selection.Contains("Exit"))
            {
                return 0;
            }
        }
    }

    private static void WaitForKey()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Press any key to continue...[/]");
        Console.ReadKey(true);
    }
}
