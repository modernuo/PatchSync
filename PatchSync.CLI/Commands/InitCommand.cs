using PatchSync.CLI.Prompts;
using PatchSync.CLI.Workspace;
using Spectre.Console;

namespace PatchSync.CLI.Commands;

public static class InitCommand
{
    private static readonly string[] DefaultChannels = ["prod", "beta", "dev"];

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
            var path = parser.Get("path") ?? Directory.GetCurrentDirectory();
            var name = parser.Get("name");
            var channelsArg = parser.Get("channels");
            var channels = string.IsNullOrEmpty(channelsArg)
                ? DefaultChannels
                : channelsArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return await ExecuteAsync(path, name, channels);
        }
        catch (ArgumentException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            AnsiConsole.WriteLine();
            ShowHelp();
            return 1;
        }
    }

    public static async Task<int> RunWizardAsync()
    {
        var (exitCode, _) = await RunWizardCoreAsync();
        return exitCode;
    }

    /// <summary>
    /// Runs the wizard and returns both exit code and the created workspace manager.
    /// </summary>
    public static async Task<(int ExitCode, WorkspaceManager? Workspace)> RunWizardAndGetWorkspaceAsync()
    {
        return await RunWizardCoreAsync();
    }

    private static async Task<(int ExitCode, WorkspaceManager? Workspace)> RunWizardCoreAsync()
    {
        AnsiConsole.MarkupLine("[grey]Initialize a new PatchSync workspace[/]\n");

        // Workspace path - type path or press Enter to browse
        var pathInput = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]Workspace directory[/] [grey](Enter to browse)[/]:")
                .AllowEmpty());

        string path;
        if (string.IsNullOrWhiteSpace(pathInput))
        {
            path = Browse.ForFolder("[green]Select workspace directory[/]", allowNew: true);
        }
        else
        {
            path = Path.GetFullPath(pathInput);
        }
        AnsiConsole.MarkupLine($"[blue]Path:[/] {path}\n");

        // Check if workspace already exists
        var manager = WorkspaceManager.ForPath(path);
        if (manager.Exists)
        {
            AnsiConsole.MarkupLine("[yellow]A workspace already exists at this location.[/]");
            if (!await AnsiConsole.ConfirmAsync("Overwrite existing workspace?", defaultValue: false))
            {
                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                return (0, null);
            }
        }

        // Project name
        var defaultName = Path.GetFileName(Path.GetFullPath(path));
        var name = AnsiConsole.Prompt(
            new TextPrompt<string>($"[green]Project name[/] [[{defaultName}]]:")
                .AllowEmpty());
        if (string.IsNullOrWhiteSpace(name))
            name = defaultName;

        // Project ID (derived from name)
        var defaultId = ToProjectId(name);
        var id = AnsiConsole.Prompt(
            new TextPrompt<string>($"[green]Project ID[/] [[{defaultId}]]:")
                .AllowEmpty()
                .Validate(s =>
                {
                    if (string.IsNullOrWhiteSpace(s))
                        return ValidationResult.Success();
                    if (!IsValidProjectId(s))
                        return ValidationResult.Error("Project ID must be lowercase letters, numbers, and hyphens only");
                    return ValidationResult.Success();
                }));
        if (string.IsNullOrWhiteSpace(id))
            id = defaultId;

        // Channels
        var channelChoices = new MultiSelectionPrompt<string>()
            .Title("[green]Select channels to create:[/]")
            .AddChoices("prod", "beta", "dev", "staging", "nightly")
            .Select("prod")
            .Select("beta")
            .Select("dev");
        var channels = AnsiConsole.Prompt(channelChoices);

        // Default input path (optional)
        string? inputPath = null;
        if (await AnsiConsole.ConfirmAsync("Configure default input directory?", defaultValue: false))
        {
            var inputPathInput = AnsiConsole.Prompt(
                new TextPrompt<string>("[green]Default input directory[/] [grey](Enter to browse)[/]:")
                    .AllowEmpty());

            if (string.IsNullOrWhiteSpace(inputPathInput))
            {
                inputPath = Browse.ForFolder("[green]Select default input directory[/]");
            }
            else
            {
                inputPath = Path.GetFullPath(inputPathInput);
            }
        }

        var exitCode = await ExecuteAsync(path, name, channels.ToArray(), id, inputPath);
        return (exitCode, exitCode == 0 ? manager : null);
    }

    private static async Task<int> ExecuteAsync(
        string path,
        string? name,
        string[] channels,
        string? projectId = null,
        string? defaultInputPath = null)
    {
        path = Path.GetFullPath(path);
        var manager = WorkspaceManager.ForPath(path);

        // Generate defaults
        name ??= Path.GetFileName(path);
        projectId ??= ToProjectId(name);

        AnsiConsole.MarkupLine($"[blue]Initializing workspace at:[/] {path}");

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Creating workspace...", async ctx =>
            {
                // Create directory structure
                ctx.Status("Creating directories...");
                manager.CreateDirectoryStructure();

                // Create channel structures
                foreach (var channel in channels)
                {
                    ctx.Status($"Creating channel: {channel}...");
                    manager.CreateChannelStructure(channel);

                    // Create initial channel state
                    var displayName = char.ToUpper(channel[0]) + channel[1..];
                    var channelState = new ChannelState
                    {
                        ChannelId = channel,
                        DisplayName = displayName
                    };
                    await manager.SaveChannelStateAsync(channel, channelState);
                }

                // Create workspace config
                ctx.Status("Creating configuration...");
                var config = CreateDefaultConfig(name, projectId, channels, defaultInputPath);
                await manager.SaveConfigAsync(config);

                // Create initial state
                ctx.Status("Creating state...");
                await manager.SaveStateAsync(new WorkspaceState());

                // Create .gitignore
                ctx.Status("Creating .gitignore...");
                await manager.CreateGitIgnoreAsync();
            });

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]:check_mark_button: Workspace initialized successfully![/]");
        AnsiConsole.WriteLine();

        // Show summary
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Property")
            .AddColumn("Value");

        table.AddRow("Project", name);
        table.AddRow("ID", projectId);
        table.AddRow("Channels", string.Join(", ", channels));
        table.AddRow("Config", Path.Combine(path, WorkspaceManager.WorkspaceConfigFileName));

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Next steps:[/]");
        AnsiConsole.MarkupLine("  1. Edit [blue]patchsync.workspace.json[/] to configure publish profiles");
        AnsiConsole.MarkupLine("  2. Run [blue]patchsync build -c prod -v 1.0.0[/] to create your first build");

        return 0;
    }

    private static WorkspaceConfig CreateDefaultConfig(
        string name,
        string projectId,
        string[] channels,
        string? defaultInputPath)
    {
        var config = new WorkspaceConfig
        {
            SchemaVersion = 1,
            Project = new ProjectInfo
            {
                Name = name,
                Id = projectId,
                Description = $"PatchSync workspace for {name}"
            },
            Defaults = new BuildDefaults
            {
                InputPath = defaultInputPath,
                Chunking = new ChunkingDefaults
                {
                    Algorithm = "fastcdc-v1",
                    MinChunkSize = 4096,
                    AvgChunkSize = 16384,
                    MaxChunkSize = 65536,
                    MinDeltaSize = 65536
                },
                Compression = new CompressionDefaults
                {
                    Enabled = true,
                    Algorithm = "zstd",
                    Level = 9
                }
            }
        };

        // Add channel configurations
        for (int i = 0; i < channels.Length; i++)
        {
            var channel = channels[i];
            var displayName = char.ToUpper(channel[0]) + channel[1..];
            var isDefault = i == 0; // First channel is default

            config.Channels[channel] = new ChannelConfig
            {
                DisplayName = displayName,
                Description = $"{displayName} release channel",
                IsDefault = isDefault,
                VersionPattern = GetDefaultVersionPattern(channel),
                RetainVersions = GetDefaultRetainVersions(channel),
                Publish = new ChannelPublishConfig
                {
                    Profile = $"{channel}-cdn",
                    Prefix = channel == "prod" ? null : $"{channel}/"
                }
            };

            // Create placeholder publish profile
            config.PublishProfiles[$"{channel}-cdn"] = new PublishProfile
            {
                Type = "s3",
                Endpoint = "https://s3.amazonaws.com",
                Bucket = $"{projectId}-cdn",
                Region = "us-east-1",
                PublicUrl = $"https://{channel}.cdn.example.com",
                CredentialSource = "environment"
            };
        }

        return config;
    }

    private static string GetDefaultVersionPattern(string channel)
    {
        return channel.ToLowerInvariant() switch
        {
            "prod" or "production" or "release" or "stable" => @"^\d+\.\d+\.\d+$",
            "beta" => @"^\d+\.\d+\.\d+-beta\.\d+$",
            "alpha" => @"^\d+\.\d+\.\d+-alpha\.\d+$",
            "dev" or "development" => ".*",
            "nightly" => @"^nightly-\d{8}$",
            "staging" => ".*",
            _ => ".*"
        };
    }

    private static int GetDefaultRetainVersions(string channel)
    {
        return channel.ToLowerInvariant() switch
        {
            "prod" or "production" or "release" or "stable" => 10,
            "beta" => 5,
            "alpha" => 3,
            "dev" or "development" => 3,
            "nightly" => 7,
            "staging" => 2,
            _ => 5
        };
    }

    private static string ToProjectId(string name)
    {
        // Convert to lowercase, replace spaces with hyphens, remove invalid chars
        var id = name.ToLowerInvariant()
            .Replace(' ', '-')
            .Replace('_', '-');

        // Remove consecutive hyphens
        while (id.Contains("--"))
            id = id.Replace("--", "-");

        // Remove non-alphanumeric except hyphens
        id = new string(id.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());

        // Trim hyphens from ends
        return id.Trim('-');
    }

    private static bool IsValidProjectId(string id)
    {
        return id.All(c => char.IsLower(c) || char.IsDigit(c) || c == '-');
    }

    private static void ShowHelp()
    {
        AnsiConsole.MarkupLine("[yellow]Usage:[/] patchsync init [options]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Options:[/]");
        AnsiConsole.MarkupLine("  --path <dir>       Directory to initialize (default: current)");
        AnsiConsole.MarkupLine("  --name <name>      Project name");
        AnsiConsole.MarkupLine("  --channels <list>  Comma-separated channel names (default: prod,beta,dev)");
        AnsiConsole.MarkupLine("  -h, --help         Show this help message");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Examples:[/]");
        AnsiConsole.MarkupLine("  patchsync init");
        AnsiConsole.MarkupLine("  patchsync init --name \"My Game\" --channels prod,beta");
        AnsiConsole.MarkupLine("  patchsync init --path ./my-game-workspace");
    }
}
