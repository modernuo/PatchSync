using PatchSync.CLI.Config;
using PatchSync.CLI.Storage;
using PatchSync.CLI.Wizard;
using PatchSync.CLI.Wizard.Steps;
using PatchSync.CLI.Wizard.Themes;
using PatchSync.CLI.Workspace;
using Spectre.Console;

namespace PatchSync.CLI.Commands;

/// <summary>
/// Interactive wizard for first-time CDN/S3 configuration.
/// Guides users through credential setup, bucket selection, and connectivity testing.
/// </summary>
public static class CdnSetupWizard
{
    public static async Task<int> RunAsync(string[] args)
    {
        var parser = new ArgParser(args);

        if (parser.HasHelp)
        {
            ShowHelp();
            return 0;
        }

        var profileName = parser.Get("profile", "p");
        var force = parser.GetBool("force", false, "f");

        var workspace = WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());
        if (workspace == null || !workspace.Exists)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No workspace found. Run 'patchsync init' first.");
            return 1;
        }

        await workspace.LoadConfigAsync();

        return await RunWizardAsync(workspace, profileName, force);
    }

    public static async Task<int> RunWizardAsync(
        WorkspaceManager workspace,
        string? profileName = null,
        bool force = false)
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule("[bold blue]CDN Setup Wizard[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        var config = workspace.Config!;

        // Check if profiles already exist
        if (config.PublishProfiles.Count > 0 && !force)
        {
            AnsiConsole.MarkupLine("[yellow]Publish profiles already exist:[/]");
            foreach (var (name, _) in config.PublishProfiles)
            {
                AnsiConsole.MarkupLine($"  - {name}");
            }
            AnsiConsole.WriteLine();

            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("What would you like to do?")
                    .AddChoices("Add new profile", "Reconfigure existing", "Cancel"));

            if (action == "Cancel")
                return 0;

            if (action == "Reconfigure existing")
            {
                profileName = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Select profile to reconfigure:")
                        .AddChoices(config.PublishProfiles.Keys));
            }
        }

        // Get profile name
        profileName ??= AnsiConsole.Prompt(
            new TextPrompt<string>("Profile name:")
                .DefaultValue("default")
                .Validate(name =>
                {
                    if (string.IsNullOrWhiteSpace(name))
                        return ValidationResult.Error("Name is required");
                    if (!force && config.PublishProfiles.ContainsKey(name))
                        return ValidationResult.Error("Profile already exists. Use --force to overwrite.");
                    return ValidationResult.Success();
                }));

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Step 1: Storage Provider[/]");
        AnsiConsole.WriteLine();

        // Select provider type
        var provider = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select storage provider:")
                .AddChoices(
                    "Amazon S3",
                    "Cloudflare R2",
                    "Backblaze B2",
                    "DigitalOcean Spaces",
                    "MinIO / Self-hosted",
                    "Other S3-compatible"));

        // Get endpoint based on provider
        string endpoint;
        string? defaultRegion = null;

        switch (provider)
        {
            case "Amazon S3":
                defaultRegion = AnsiConsole.Prompt(
                    new TextPrompt<string>("AWS Region:")
                        .DefaultValue("us-east-1"));
                endpoint = $"https://s3.{defaultRegion}.amazonaws.com";
                break;
            case "Cloudflare R2":
                var accountId = AnsiConsole.Prompt(
                    new TextPrompt<string>("Cloudflare Account ID:"));
                endpoint = $"https://{accountId}.r2.cloudflarestorage.com";
                defaultRegion = "auto";
                break;
            case "Backblaze B2":
                endpoint = AnsiConsole.Prompt(
                    new TextPrompt<string>("B2 Endpoint:")
                        .DefaultValue("https://s3.us-west-000.backblazeb2.com"));
                break;
            case "DigitalOcean Spaces":
                var region = AnsiConsole.Prompt(
                    new TextPrompt<string>("DO Spaces Region:")
                        .DefaultValue("nyc3"));
                endpoint = $"https://{region}.digitaloceanspaces.com";
                defaultRegion = region;
                break;
            default:
                endpoint = AnsiConsole.Prompt(
                    new TextPrompt<string>("S3 Endpoint URL:")
                        .Validate(url =>
                        {
                            if (string.IsNullOrWhiteSpace(url))
                                return ValidationResult.Error("Endpoint is required");
                            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
                                return ValidationResult.Error("Invalid URL format");
                            return ValidationResult.Success();
                        }));
                break;
        }

        AnsiConsole.MarkupLine($"[dim]Endpoint: {endpoint}[/]");
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[bold]Step 2: Credentials[/]");
        AnsiConsole.WriteLine();

        // Get credentials
        var accessKey = AnsiConsole.Prompt(
            new TextPrompt<string>("Access Key ID:"));

        var secretKey = AnsiConsole.Prompt(
            new TextPrompt<string>("Secret Access Key:")
                .Secret());

        defaultRegion ??= AnsiConsole.Prompt(
            new TextPrompt<string>("Region:")
                .DefaultValue("us-east-1"));

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Step 3: Test Connection[/]");
        AnsiConsole.WriteLine();

        // Test connectivity and list buckets
        string bucket;
        var testConfig = new S3Config
        {
            Endpoint = endpoint,
            AccessKey = accessKey,
            SecretKey = secretKey,
            Region = defaultRegion,
            Bucket = "test", // Temporary
            PathStyle = true
        };

        try
        {
            using var testClient = new S3Client(testConfig);
            var connected = await AnsiConsole.Status()
                .StartAsync("Testing connectivity...", async ctx =>
                {
                    return await testClient.TestConnectivityAsync();
                });

            if (!connected)
            {
                AnsiConsole.MarkupLine("[yellow]Warning: Could not verify connectivity.[/]");
                AnsiConsole.MarkupLine("[dim]This may be normal for some S3-compatible providers.[/]");
                AnsiConsole.WriteLine();
            }
            else
            {
                AnsiConsole.MarkupLine("[green]Connection successful![/]");
                AnsiConsole.WriteLine();
            }

            // Try to list buckets
            var buckets = await AnsiConsole.Status()
                .StartAsync("Listing buckets...", async ctx =>
                {
                    try
                    {
                        return await testClient.ListBucketsAsync();
                    }
                    catch
                    {
                        return new List<string>();
                    }
                });

            if (buckets.Count > 0)
            {
                AnsiConsole.MarkupLine("[green]Found {0} bucket(s):[/]", buckets.Count);
                foreach (var b in buckets.Take(10))
                {
                    AnsiConsole.MarkupLine($"  - {b}");
                }
                if (buckets.Count > 10)
                {
                    AnsiConsole.MarkupLine($"  [dim]... and {buckets.Count - 10} more[/]");
                }
                AnsiConsole.WriteLine();

                var choices = new List<string>(buckets) { "[Enter bucket name manually]" };
                var selection = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Select bucket:")
                        .PageSize(15)
                        .AddChoices(choices));

                bucket = selection == "[Enter bucket name manually]"
                    ? AnsiConsole.Prompt(new TextPrompt<string>("Bucket name:"))
                    : selection;
            }
            else
            {
                bucket = AnsiConsole.Prompt(
                    new TextPrompt<string>("Bucket name:"));
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Connection test failed:[/] {ex.Message}");
            AnsiConsole.WriteLine();
            bucket = AnsiConsole.Prompt(
                new TextPrompt<string>("Bucket name (enter manually):"));
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Step 4: Public Access[/]");
        AnsiConsole.WriteLine();

        // Get public URL configuration
        var hasPublicUrl = AnsiConsole.Confirm(
            "Will files be accessible via a public URL (CDN)?",
            true);

        string? publicUrl = null;
        CdnConfig? cdnConfig = null;

        if (hasPublicUrl)
        {
            var suggestedUrl = provider switch
            {
                "Cloudflare R2" => $"https://{bucket}.your-domain.com",
                "Amazon S3" => $"https://{bucket}.s3.{defaultRegion}.amazonaws.com",
                "DigitalOcean Spaces" => $"https://{bucket}.{defaultRegion}.cdn.digitaloceanspaces.com",
                _ => $"https://{bucket}.your-cdn.com"
            };

            publicUrl = AnsiConsole.Prompt(
                new TextPrompt<string>("Public URL (where clients will download):")
                    .DefaultValue(suggestedUrl));

            if (provider == "Cloudflare R2")
            {
                var configureCloudflare = AnsiConsole.Confirm(
                    "Configure Cloudflare cache purging?",
                    false);

                if (configureCloudflare)
                {
                    var zoneId = AnsiConsole.Prompt(
                        new TextPrompt<string>("Cloudflare Zone ID:"));

                    cdnConfig = new CdnConfig
                    {
                        Provider = "cloudflare",
                        ZoneId = zoneId,
                        AutoPurge = true
                    };
                }
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Step 5: Path Configuration[/]");
        AnsiConsole.WriteLine();

        var prefix = AnsiConsole.Prompt(
            new TextPrompt<string>("Base path prefix (optional):")
                .AllowEmpty()
                .DefaultValue(config.Project.Id));

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Step 6: Save Credentials[/]");
        AnsiConsole.WriteLine();

        var credentialSource = "prompt";

        if (CredentialManager.IsSecureStorageAvailable)
        {
            var saveCredentials = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("How should credentials be stored?")
                    .AddChoices(
                        "Save securely (DPAPI encrypted)",
                        "Use environment variables",
                        "Prompt each time"));

            switch (saveCredentials)
            {
                case "Save securely (DPAPI encrypted)":
                    credentialSource = "stored";
                    if (OperatingSystem.IsWindows())
                    {
                        var credentials = new S3Credentials
                        {
                            Endpoint = endpoint,
                            Bucket = bucket,
                            Region = defaultRegion,
                            AccessKey = accessKey,
                            SecretKey = secretKey,
                            Prefix = string.IsNullOrWhiteSpace(prefix) ? null : prefix,
                            PublicUrl = publicUrl
                        };
                        CredentialManager.SaveCredentials(credentials, showWarning: true);
                    }
                    break;
                case "Use environment variables":
                    credentialSource = "environment";
                    AnsiConsole.MarkupLine("[dim]Set these environment variables:[/]");
                    AnsiConsole.MarkupLine($"  {CredentialManager.EnvironmentVariables.AccessKey}={accessKey}");
                    AnsiConsole.MarkupLine($"  {CredentialManager.EnvironmentVariables.SecretKey}=***");
                    AnsiConsole.MarkupLine($"  {CredentialManager.EnvironmentVariables.Endpoint}={endpoint}");
                    AnsiConsole.MarkupLine($"  {CredentialManager.EnvironmentVariables.Bucket}={bucket}");
                    break;
            }
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]Secure credential storage not available on this platform.[/]");
            credentialSource = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("How should credentials be stored?")
                    .AddChoices(
                        "Use environment variables",
                        "Prompt each time"));

            if (credentialSource == "Use environment variables")
            {
                credentialSource = "environment";
                AnsiConsole.MarkupLine("[dim]Set these environment variables:[/]");
                AnsiConsole.MarkupLine($"  {CredentialManager.EnvironmentVariables.AccessKey}={accessKey}");
                AnsiConsole.MarkupLine($"  {CredentialManager.EnvironmentVariables.SecretKey}=***");
            }
            else
            {
                credentialSource = "prompt";
            }
        }

        // Create publish profile
        var profile = new PublishProfile
        {
            Type = "s3",
            Endpoint = endpoint,
            Bucket = bucket,
            Region = defaultRegion,
            Prefix = string.IsNullOrWhiteSpace(prefix) ? null : prefix,
            PublicUrl = publicUrl,
            CredentialSource = credentialSource,
            PathStyle = true,
            Cdn = cdnConfig
        };

        config.PublishProfiles[profileName] = profile;
        await workspace.SaveConfigAsync(config);

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[green]Setup Complete[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        var summary = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Setting")
            .AddColumn("Value");

        summary.AddRow("Profile Name", $"[green]{profileName}[/]");
        summary.AddRow("Provider", provider);
        summary.AddRow("Endpoint", endpoint);
        summary.AddRow("Bucket", bucket);
        summary.AddRow("Region", defaultRegion ?? "-");
        summary.AddRow("Prefix", string.IsNullOrEmpty(prefix) ? "-" : prefix);
        summary.AddRow("Public URL", publicUrl ?? "-");
        summary.AddRow("Credentials", credentialSource);

        AnsiConsole.Write(summary);
        AnsiConsole.WriteLine();

        // Offer to assign to channels
        if (config.Channels.Count > 0)
        {
            var assignToChannel = AnsiConsole.Confirm(
                "Assign this profile to a channel?",
                true);

            if (assignToChannel)
            {
                var channel = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Select channel:")
                        .AddChoices(config.Channels.Keys));

                var channelConfig = config.Channels[channel];
                channelConfig.Publish = new ChannelPublishConfig
                {
                    Profile = profileName
                };

                await workspace.SaveConfigAsync(config);
                AnsiConsole.MarkupLine($"[green]Assigned '{profileName}' to channel '{channel}'[/]");
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Run 'patchsync publish -c <channel>' to upload a version.[/]");

        return 0;
    }

    private static void ShowHelp()
    {
        AnsiConsole.MarkupLine("[bold]patchsync cdn setup[/] - Configure CDN/S3 storage");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Usage:[/]");
        AnsiConsole.MarkupLine("  patchsync cdn setup [options]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Options:[/]");
        AnsiConsole.MarkupLine("  -p, --profile <NAME>  Profile name to create/update");
        AnsiConsole.MarkupLine("  -f, --force           Overwrite existing profile");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Notes:[/]");
        AnsiConsole.MarkupLine("  - Run without options for interactive setup wizard");
        AnsiConsole.MarkupLine("  - Credentials can be stored securely (Windows) or via env vars");
        AnsiConsole.MarkupLine("  - Multiple profiles can be created for different environments");
    }
}
