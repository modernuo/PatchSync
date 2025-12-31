using PatchSync.CLI.Prompts;
using PatchSync.SDK.Client;
using PatchSync.SDK.Storage;
using Spectre.Console;

namespace PatchSync.CLI.Commands;

public static class PatchCommand
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
            var url = parser.GetRequired("url", "u");
            var localPath = parser.GetRequired("path", "p");
            var manifestPath = parser.GetOrDefault("manifest", "manifest.json", "m");
            var deltaThreshold = parser.GetDouble("delta-threshold", 0.8);
            var verify = parser.GetBool("verify", true);

            return await ExecuteAsync(url, localPath, manifestPath, deltaThreshold, verify);
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
        AnsiConsole.MarkupLine("[grey]Apply delta patches to update local files[/]\n");

        // CDN URL
        var url = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]CDN URL[/] (base URL hosting manifest and files):")
                .Validate(u =>
                {
                    if (!Uri.TryCreate(u, UriKind.Absolute, out _))
                        return ValidationResult.Error("Invalid URL");
                    return ValidationResult.Success();
                }));

        // Local path - use file browser
        var useBrowser = await AnsiConsole.ConfirmAsync("Browse for installation directory?");
        string localPath;
        if (useBrowser)
        {
            localPath = Browse.ForFolder("[green]Select installation directory[/]", allowNew: true);
        }
        else
        {
            localPath = AnsiConsole.Prompt(
                new TextPrompt<string>("[green]Installation directory path:[/]")
                    .DefaultValue("./game"));
        }
        AnsiConsole.MarkupLine($"[blue]Local path:[/] {localPath}\n");

        // Manifest path
        var manifestPath = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]Manifest file[/] (relative to CDN URL):")
                .DefaultValue("manifest.json"));

        // Options
        var verify = await AnsiConsole.ConfirmAsync("Verify files after patching?");

        var useAdvanced = await AnsiConsole.ConfirmAsync("Configure advanced options?", false);

        double deltaThreshold = 0.8;
        if (useAdvanced)
        {
            deltaThreshold = AnsiConsole.Prompt(
                new TextPrompt<double>("[green]Delta threshold[/] (0.0-1.0, lower = prefer delta):")
                    .DefaultValue(0.8)
                    .Validate(d => d is >= 0.0 and <= 1.0
                        ? ValidationResult.Success()
                        : ValidationResult.Error("Must be between 0.0 and 1.0")));
        }

        AnsiConsole.WriteLine();

        return await ExecuteAsync(url, localPath, manifestPath, deltaThreshold, verify);
    }

    private static async Task<int> ExecuteAsync(
        string url,
        string localPath,
        string manifestPath,
        double deltaThreshold,
        bool verify)
    {
        // Validate local path
        if (!Directory.Exists(localPath))
        {
            AnsiConsole.MarkupLine($"[yellow]Creating directory:[/] {localPath}");
            Directory.CreateDirectory(localPath);
        }

        // Parse URL
        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Invalid URL: {url}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[blue]CDN URL:[/] {url}");
        AnsiConsole.MarkupLine($"[blue]Local path:[/] {localPath}");
        AnsiConsole.WriteLine();

        try
        {
            using var storage = new HttpStorageProvider(baseUri);
            using var client = new PatchSyncClient(storage, options: new PatchClientOptions
            {
                DeltaThreshold = deltaThreshold,
                VerifyAfterPatch = verify
            });

            // Fetch manifest
            AnsiConsole.MarkupLine("[grey]Fetching manifest...[/]");
            var manifest = await client.GetManifestAsync(manifestPath);

            AnsiConsole.MarkupLine($"[green]Manifest version:[/] {manifest.Version}");
            AnsiConsole.MarkupLine($"[green]Files:[/] {manifest.Files.Count}");
            AnsiConsole.MarkupLine($"[green]Total size:[/] {FormatBytes(manifest.Files.Sum(f => f.Size))}");
            AnsiConsole.WriteLine();

            // Apply patches with progress
            await AnsiConsole.Progress()
                .AutoRefresh(true)
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new RemainingTimeColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var overallTask = ctx.AddTask("Overall progress", autoStart: true);
                    var fileTask = ctx.AddTask("Current file", autoStart: false);

                    var progress = new Progress<PatchProgress>(p =>
                    {
                        overallTask.Value = p.OverallPercentage * 100;
                        overallTask.Description = $"Overall ({p.FilesComplete}/{p.FilesTotal} files)";

                        if (p.CurrentFile != null)
                        {
                            if (!fileTask.IsStarted)
                                fileTask.StartTask();
                            fileTask.Description = p.CurrentFile;
                            fileTask.Value = p.CurrentFileProgress * 100;
                        }

                        if (p.Phase == PatchPhase.Complete)
                        {
                            fileTask.Value = 100;
                            fileTask.StopTask();
                        }
                    });

                    await client.PatchAsync(localPath, manifest, progress);

                    overallTask.Value = 100;
                    overallTask.StopTask();
                });

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[green]Patch complete![/]");
            return 0;
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]Network error:[/] {ex.Message}");
            return 1;
        }
        catch (InvalidDataException ex)
        {
            AnsiConsole.MarkupLine($"[red]Data error:[/] {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
    }

    private static void ShowHelp()
    {
        AnsiConsole.MarkupLine("[bold]patchsync patch[/] - Apply delta patches to update local files");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Usage:[/]");
        AnsiConsole.MarkupLine("  patchsync patch -u <url> -p <path> [[options]]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Required:[/]");
        AnsiConsole.MarkupLine("  -u, --url <URL>           Base URL of the CDN hosting manifest and files");
        AnsiConsole.MarkupLine("  -p, --path <PATH>         Local path to the installation directory");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Options:[/]");
        AnsiConsole.MarkupLine("  -m, --manifest <PATH>     Manifest file path (default: manifest.json)");
        AnsiConsole.MarkupLine("      --delta-threshold <N> Delta vs compressed threshold (default: 0.8)");
        AnsiConsole.MarkupLine("      --verify              Verify files after patching (default: true)");
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
