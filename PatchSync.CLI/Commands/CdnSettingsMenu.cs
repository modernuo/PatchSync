using PatchSync.CLI.Config;
using PatchSync.CLI.Storage;
using PatchSync.CLI.Workspace;
using Spectre.Console;

namespace PatchSync.CLI.Commands;

/// <summary>
/// CDN settings menu for viewing, testing, and managing publish profiles.
/// </summary>
public static class CdnSettingsMenu
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
        var profileName = parser.Get("profile", "p");

        var workspace = WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());
        if (workspace == null || !workspace.Exists)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No workspace found.");
            return 1;
        }

        await workspace.LoadConfigAsync();

        return subCommand switch
        {
            "test" => await TestConnectivityAsync(workspace, profileName),
            "list" => ListProfiles(workspace),
            "delete" => await DeleteProfileAsync(workspace, profileName),
            "show" => ShowProfile(workspace, profileName),
            _ => await ShowMenuAsync(workspace)
        };
    }

    private static async Task<int> ShowMenuAsync(WorkspaceManager workspace)
    {
        var config = workspace.Config!;

        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule("[bold blue]CDN Settings[/]").RuleStyle("grey"));
            AnsiConsole.WriteLine();

            if (config.PublishProfiles.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No publish profiles configured.[/]");
                AnsiConsole.WriteLine();

                var setup = AnsiConsole.Confirm("Run CDN setup wizard?", true);
                if (setup)
                {
                    await CdnSetupWizard.RunWizardAsync(workspace);
                    await workspace.LoadConfigAsync();
                    config = workspace.Config!;
                    continue;
                }
                return 0;
            }

            // Show current profiles
            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Profile")
                .AddColumn("Endpoint")
                .AddColumn("Bucket")
                .AddColumn("Credentials");

            foreach (var (name, profile) in config.PublishProfiles)
            {
                var credStatus = profile.CredentialSource switch
                {
                    "stored" => CredentialManager.HasStoredCredentials()
                        ? "[green]Stored[/]"
                        : "[yellow]Missing[/]",
                    "environment" => CredentialManager.LoadFromEnvironment() != null
                        ? "[green]Set[/]"
                        : "[yellow]Not set[/]",
                    _ => "[dim]Prompt[/]"
                };

                table.AddRow(name, profile.Endpoint, profile.Bucket, credStatus);
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();

            // Show channel assignments
            var assignments = config.Channels
                .Where(c => c.Value.Publish != null)
                .Select(c => $"{c.Key} -> {c.Value.Publish!.Profile}")
                .ToList();

            if (assignments.Count > 0)
            {
                AnsiConsole.MarkupLine("[bold]Channel Assignments:[/]");
                foreach (var a in assignments)
                {
                    AnsiConsole.MarkupLine($"  {a}");
                }
                AnsiConsole.WriteLine();
            }

            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select action:")
                    .AddChoices(
                        "Test connectivity",
                        "Add new profile",
                        "Edit profile",
                        "Delete profile",
                        "Assign profile to channel",
                        "View upload history",
                        "Back"));

            switch (action)
            {
                case "Test connectivity":
                    await TestConnectivityInteractiveAsync(workspace);
                    break;
                case "Add new profile":
                    await CdnSetupWizard.RunWizardAsync(workspace);
                    await workspace.LoadConfigAsync();
                    config = workspace.Config!;
                    break;
                case "Edit profile":
                    await EditProfileAsync(workspace);
                    await workspace.LoadConfigAsync();
                    config = workspace.Config!;
                    break;
                case "Delete profile":
                    await DeleteProfileInteractiveAsync(workspace);
                    await workspace.LoadConfigAsync();
                    config = workspace.Config!;
                    break;
                case "Assign profile to channel":
                    await AssignProfileAsync(workspace);
                    await workspace.LoadConfigAsync();
                    config = workspace.Config!;
                    break;
                case "View upload history":
                    await ShowUploadHistoryAsync(workspace);
                    break;
                case "Back":
                    return 0;
            }
        }
    }

    private static async Task<int> TestConnectivityAsync(WorkspaceManager workspace, string? profileName)
    {
        var config = workspace.Config!;

        if (config.PublishProfiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No publish profiles configured.");
            return 1;
        }

        profileName ??= config.PublishProfiles.Keys.First();

        if (!config.PublishProfiles.TryGetValue(profileName, out var profile))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Profile not found: {profileName}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold]Testing profile:[/] {profileName}");
        AnsiConsole.WriteLine();

        // Get credentials
        var s3Config = ResolveS3Config(profile);
        if (s3Config == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Could not resolve credentials.");
            return 1;
        }

        try
        {
            using var client = new S3Client(s3Config);

            var success = await AnsiConsole.Status()
                .StartAsync("Testing connection...", async ctx =>
                {
                    return await client.TestConnectivityAsync();
                });

            if (success)
            {
                AnsiConsole.MarkupLine("[green]Connection successful![/]");

                // Try to list objects
                await AnsiConsole.Status()
                    .StartAsync("Listing bucket contents...", async ctx =>
                    {
                        try
                        {
                            var prefixes = await client.ListPrefixesAsync(profile.Prefix ?? "");
                            if (prefixes.Count > 0)
                            {
                                AnsiConsole.MarkupLine($"[dim]Found {prefixes.Count} prefix(es) in bucket[/]");
                            }
                        }
                        catch
                        {
                            // Ignore listing errors
                        }
                    });

                return 0;
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]Connection test returned false.[/]");
                return 1;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Connection failed:[/] {ex.Message}");
            return 1;
        }
    }

    private static async Task TestConnectivityInteractiveAsync(WorkspaceManager workspace)
    {
        var config = workspace.Config!;

        var profileName = config.PublishProfiles.Count == 1
            ? config.PublishProfiles.Keys.First()
            : AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select profile to test:")
                    .AddChoices(config.PublishProfiles.Keys));

        await TestConnectivityAsync(workspace, profileName);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Press any key to continue...[/]");
        Console.ReadKey(true);
    }

    private static int ListProfiles(WorkspaceManager workspace)
    {
        var config = workspace.Config!;

        if (config.PublishProfiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No publish profiles configured.[/]");
            return 0;
        }

        foreach (var (name, profile) in config.PublishProfiles)
        {
            AnsiConsole.MarkupLine($"[bold]{name}[/]");
            AnsiConsole.MarkupLine($"  Endpoint: {profile.Endpoint}");
            AnsiConsole.MarkupLine($"  Bucket: {profile.Bucket}");
            AnsiConsole.MarkupLine($"  Region: {profile.Region ?? "-"}");
            AnsiConsole.MarkupLine($"  Public URL: {profile.PublicUrl ?? "-"}");
            AnsiConsole.WriteLine();
        }

        return 0;
    }

    private static int ShowProfile(WorkspaceManager workspace, string? profileName)
    {
        var config = workspace.Config!;

        if (string.IsNullOrEmpty(profileName))
        {
            return ListProfiles(workspace);
        }

        if (!config.PublishProfiles.TryGetValue(profileName, out var profile))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Profile not found: {profileName}");
            return 1;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Setting")
            .AddColumn("Value");

        table.AddRow("Name", profileName);
        table.AddRow("Type", profile.Type);
        table.AddRow("Endpoint", profile.Endpoint);
        table.AddRow("Bucket", profile.Bucket);
        table.AddRow("Region", profile.Region ?? "-");
        table.AddRow("Prefix", profile.Prefix ?? "-");
        table.AddRow("Public URL", profile.PublicUrl ?? "-");
        table.AddRow("Path Style", profile.PathStyle.ToString());
        table.AddRow("Credential Source", profile.CredentialSource);

        if (profile.Cdn != null)
        {
            table.AddRow("CDN Provider", profile.Cdn.Provider ?? "-");
            table.AddRow("CDN Zone ID", profile.Cdn.ZoneId ?? "-");
            table.AddRow("Auto Purge", profile.Cdn.AutoPurge.ToString());
        }

        AnsiConsole.Write(table);
        return 0;
    }

    private static async Task<int> DeleteProfileAsync(WorkspaceManager workspace, string? profileName)
    {
        var config = workspace.Config!;

        if (string.IsNullOrEmpty(profileName))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Profile name required.");
            return 1;
        }

        if (!config.PublishProfiles.ContainsKey(profileName))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Profile not found: {profileName}");
            return 1;
        }

        config.PublishProfiles.Remove(profileName);

        // Also remove from channel assignments
        foreach (var channel in config.Channels.Values)
        {
            if (channel.Publish?.Profile == profileName)
            {
                channel.Publish = null;
            }
        }

        await workspace.SaveConfigAsync(config);
        AnsiConsole.MarkupLine($"[green]Deleted profile:[/] {profileName}");
        return 0;
    }

    private static async Task DeleteProfileInteractiveAsync(WorkspaceManager workspace)
    {
        var config = workspace.Config!;

        if (config.PublishProfiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No profiles to delete.[/]");
            return;
        }

        var profileName = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select profile to delete:")
                .AddChoices(config.PublishProfiles.Keys));

        var confirm = AnsiConsole.Confirm($"Delete profile '{profileName}'?", false);
        if (!confirm)
            return;

        await DeleteProfileAsync(workspace, profileName);
    }

    private static async Task EditProfileAsync(WorkspaceManager workspace)
    {
        var config = workspace.Config!;

        if (config.PublishProfiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No profiles to edit.[/]");
            return;
        }

        var profileName = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select profile to edit:")
                .AddChoices(config.PublishProfiles.Keys));

        // Re-run setup wizard with force option
        await CdnSetupWizard.RunWizardAsync(workspace, profileName, force: true);
    }

    private static async Task AssignProfileAsync(WorkspaceManager workspace)
    {
        var config = workspace.Config!;

        if (config.PublishProfiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No profiles available.[/]");
            return;
        }

        if (config.Channels.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No channels configured.[/]");
            return;
        }

        var channel = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select channel:")
                .AddChoices(config.Channels.Keys));

        var profileChoices = new List<string>(config.PublishProfiles.Keys) { "(None)" };
        var profile = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select profile for this channel:")
                .AddChoices(profileChoices));

        var channelConfig = config.Channels[channel];
        if (profile == "(None)")
        {
            channelConfig.Publish = null;
        }
        else
        {
            channelConfig.Publish = new ChannelPublishConfig { Profile = profile };
        }

        await workspace.SaveConfigAsync(config);
        AnsiConsole.MarkupLine($"[green]Channel '{channel}' assigned to profile '{profile}'[/]");
    }

    private static async Task ShowUploadHistoryAsync(WorkspaceManager workspace)
    {
        var logManager = new UploadLogManager(workspace.WorkspacePath);
        var logs = await logManager.GetLogsAsync(limit: 20);

        if (logs.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No upload history found.[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[dim]Press any key to continue...[/]");
            Console.ReadKey(true);
            return;
        }

        AnsiConsole.MarkupLine("[bold]Recent Uploads:[/]");
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Date")
            .AddColumn("Channel")
            .AddColumn("Version")
            .AddColumn("Status")
            .AddColumn("Files")
            .AddColumn("Duration");

        foreach (var log in logs)
        {
            var status = log.Status switch
            {
                UploadStatus.Completed => "[green]Completed[/]",
                UploadStatus.Failed => "[red]Failed[/]",
                UploadStatus.PartialSuccess => "[yellow]Partial[/]",
                UploadStatus.InProgress => "[blue]In Progress[/]",
                UploadStatus.Cancelled => "[grey]Cancelled[/]",
                _ => log.Status.ToString()
            };

            var duration = log.Duration.TotalSeconds > 0
                ? $"{log.Duration.TotalSeconds:F1}s"
                : "-";

            table.AddRow(
                log.StartedAt.ToString("yyyy-MM-dd HH:mm"),
                log.Channel,
                log.Version,
                status,
                $"{log.UploadedFiles}/{log.TotalFiles}",
                duration);
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Press any key to continue...[/]");
        Console.ReadKey(true);
    }

    private static S3Config? ResolveS3Config(PublishProfile profile)
    {
        S3Credentials? credentials = null;

        switch (profile.CredentialSource)
        {
            case "stored":
                if (OperatingSystem.IsWindows())
                {
                    credentials = CredentialManager.LoadFromEncryptedFile();
                }
                break;
            case "environment":
                credentials = CredentialManager.LoadFromEnvironment();
                break;
            case "prompt":
                var accessKey = AnsiConsole.Prompt(
                    new TextPrompt<string>("Access Key ID:"));
                var secretKey = AnsiConsole.Prompt(
                    new TextPrompt<string>("Secret Access Key:")
                        .Secret());
                credentials = new S3Credentials
                {
                    AccessKey = accessKey,
                    SecretKey = secretKey
                };
                break;
        }

        if (credentials == null)
            return null;

        return new S3Config
        {
            Endpoint = profile.Endpoint,
            Bucket = profile.Bucket,
            Region = profile.Region ?? "us-east-1",
            AccessKey = credentials.AccessKey,
            SecretKey = credentials.SecretKey,
            Prefix = profile.Prefix,
            PublicUrl = profile.PublicUrl,
            PathStyle = profile.PathStyle
        };
    }

    private static void ShowHelp()
    {
        AnsiConsole.MarkupLine("[bold]patchsync cdn[/] - CDN and storage configuration");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Subcommands:[/]");
        AnsiConsole.MarkupLine("  setup       Configure CDN/S3 storage (wizard)");
        AnsiConsole.MarkupLine("  test        Test connectivity to a profile");
        AnsiConsole.MarkupLine("  list        List all profiles");
        AnsiConsole.MarkupLine("  show        Show profile details");
        AnsiConsole.MarkupLine("  delete      Delete a profile");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Options:[/]");
        AnsiConsole.MarkupLine("  -p, --profile <NAME>  Profile name");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Examples:[/]");
        AnsiConsole.MarkupLine("  patchsync cdn setup");
        AnsiConsole.MarkupLine("  patchsync cdn test -p default");
        AnsiConsole.MarkupLine("  patchsync cdn list");
    }
}
