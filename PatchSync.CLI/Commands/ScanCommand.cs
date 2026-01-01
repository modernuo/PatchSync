using PatchSync.CLI.Prompts;
using PatchSync.CLI.Wizard;
using PatchSync.CLI.Wizard.Steps;
using PatchSync.CLI.Wizard.Themes;
using PatchSync.Common.Manifest;
using PatchSync.SDK.Client;
using PatchSync.SDK.Storage;
using Spectre.Console;

namespace PatchSync.CLI.Commands;

public static class ScanCommand
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
            var showDetails = parser.GetBool("details", false, "d");

            return await ExecuteAsync(url, localPath, manifestPath, showDetails);
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
        var wizard = new WizardRunner("Scan Installation", new BoxTheme())
            .AddStep(new TextStep(
                key: "url",
                displayName: "CDN URL",
                prompt: "CDN URL (base URL hosting manifest)",
                validator: u =>
                {
                    if (string.IsNullOrWhiteSpace(u))
                        return ValidationResult.Error("URL is required");
                    if (!Uri.TryCreate(u, UriKind.Absolute, out _))
                        return ValidationResult.Error("Invalid URL format");
                    return ValidationResult.Success();
                }))
            .AddStep(new FolderBrowseStep(
                key: "localPath",
                displayName: "Installation Directory",
                prompt: "Select installation directory to scan"))
            .AddStep(new TextStep(
                key: "manifestPath",
                displayName: "Manifest File",
                prompt: "Manifest file (relative to CDN URL)",
                defaultValue: "manifest.json"))
            .AddStep(new ConfirmStep(
                key: "showDetails",
                displayName: "Show Details",
                question: "Show detailed file list?",
                defaultValue: false));

        if (!await wizard.RunAsync())
        {
            return 0; // User cancelled
        }

        // Extract values
        var ctx = wizard.Context;
        var url = ctx.Get<string>("url");
        var localPath = ctx.Get<string>("localPath");
        var manifestPath = ctx.Get<string>("manifestPath");
        var showDetails = ctx.Get<bool>("showDetails");

        return await ExecuteAsync(url, localPath, manifestPath, showDetails);
    }

    private static async Task<int> ExecuteAsync(
        string url,
        string localPath,
        string manifestPath,
        bool showDetails)
    {
        if (!Directory.Exists(localPath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Directory not found: {localPath}");
            return 1;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Invalid URL: {url}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[blue]Scanning:[/] {localPath}");
        AnsiConsole.MarkupLine($"[blue]Against manifest from:[/] {url}");
        AnsiConsole.WriteLine();

        try
        {
            using var storage = new HttpStorageProvider(baseUri);
            using var client = new PatchSyncClient(storage);

            // Fetch manifest
            AnsiConsole.MarkupLine("[grey]Fetching manifest...[/]");
            var manifest = await client.GetManifestAsync(manifestPath);

            AnsiConsole.MarkupLine($"[green]Version:[/] {manifest.Version}");
            AnsiConsole.MarkupLine($"[green]Total files:[/] {manifest.Files.Count}");
            AnsiConsole.MarkupLine($"[green]Total size:[/] {FormatBytes(manifest.Files.Sum(f => f.Size))}");
            AnsiConsole.WriteLine();

            // Scan with progress
            ScanResult? scanResult = null;
            await AnsiConsole.Progress()
                .AutoRefresh(true)
                .AutoClear(true)
                .HideCompleted(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("Scanning files", autoStart: true);

                    var progress = new Progress<ScanProgress>(p =>
                    {
                        task.Description = p.CurrentFile ?? "Scanning...";
                        task.Value = p.Percentage * 100;
                    });

                    scanResult = await client.ScanAsync(localPath, manifest, progress);

                    task.Value = 100;
                });

            if (scanResult == null) return 1;

            // Show summary
            AnsiConsole.Write(new Rule("[yellow]Scan Results[/]").RuleStyle("grey"));

            var summaryTable = new Table()
                .AddColumn("Status")
                .AddColumn(new TableColumn("Count").RightAligned())
                .AddColumn(new TableColumn("Size").RightAligned());

            summaryTable.AddRow(
                "[green]Up to date[/]",
                scanResult.UpToDate.Count.ToString(),
                FormatBytes(scanResult.UpToDate.Sum(f => f.Size)));

            summaryTable.AddRow(
                "[yellow]Needs update[/]",
                scanResult.NeedsUpdate.Count.ToString(),
                FormatBytes(scanResult.NeedsUpdate.Sum(f => f.Size)));

            summaryTable.AddRow(
                "[red]Missing[/]",
                scanResult.Missing.Count.ToString(),
                FormatBytes(scanResult.Missing.Sum(f => f.Size)));

            if (scanResult.ToDelete.Count > 0)
            {
                summaryTable.AddRow(
                    "[grey]To delete[/]",
                    scanResult.ToDelete.Count.ToString(),
                    FormatBytes(scanResult.ToDelete.Sum(f => f.Size)));
            }

            AnsiConsole.Write(summaryTable);
            AnsiConsole.WriteLine();

            // Show strategy breakdown with delta estimates
            if (!scanResult.IsUpToDate)
            {
                var breakdown = scanResult.GetStrategyBreakdown();

                AnsiConsole.Write(new Rule("[yellow]Download Estimate[/]").RuleStyle("grey"));

                var strategyTable = new Table()
                    .AddColumn("Strategy")
                    .AddColumn(new TableColumn("Files").RightAligned())
                    .AddColumn(new TableColumn("Worst Case").RightAligned())
                    .AddColumn(new TableColumn("Estimated").RightAligned())
                    .AddColumn(new TableColumn("Savings").RightAligned());

                if (breakdown.DeltaFileCount > 0)
                {
                    var deltaSavings = breakdown.DeltaWorstCaseBytes - breakdown.DeltaEstimatedBytes;
                    strategyTable.AddRow(
                        "[blue]Delta (CDC)[/]",
                        breakdown.DeltaFileCount.ToString(),
                        FormatBytes(breakdown.DeltaWorstCaseBytes),
                        FormatBytes(breakdown.DeltaEstimatedBytes),
                        $"[green]-{FormatBytes(deltaSavings)}[/]");
                }

                if (breakdown.VirtualDeltaFileCount > 0)
                {
                    var vdSavings = breakdown.VirtualDeltaWorstCaseBytes - breakdown.VirtualDeltaEstimatedBytes;
                    strategyTable.AddRow(
                        "[cyan]VirtualDelta (UOP)[/]",
                        breakdown.VirtualDeltaFileCount.ToString(),
                        FormatBytes(breakdown.VirtualDeltaWorstCaseBytes),
                        FormatBytes(breakdown.VirtualDeltaEstimatedBytes),
                        $"[green]-{FormatBytes(vdSavings)}[/]");
                }

                if (breakdown.FullDownloadFileCount > 0)
                {
                    strategyTable.AddRow(
                        "[grey]Full Download[/]",
                        breakdown.FullDownloadFileCount.ToString(),
                        FormatBytes(breakdown.FullDownloadBytes),
                        FormatBytes(breakdown.FullDownloadBytes),
                        "[grey]n/a[/]");
                }

                strategyTable.AddRow(
                    "[bold]Total[/]",
                    (breakdown.DeltaFileCount + breakdown.VirtualDeltaFileCount + breakdown.FullDownloadFileCount).ToString(),
                    $"[bold]{FormatBytes(breakdown.TotalWorstCaseBytes)}[/]",
                    $"[bold green]{FormatBytes(breakdown.TotalEstimatedBytes)}[/]",
                    $"[bold green]-{FormatBytes(breakdown.EstimatedSavings)} ({breakdown.SavingsPercentage:P0})[/]");

                AnsiConsole.Write(strategyTable);

                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[grey]Note: Estimates based on typical delta ratios (Delta: ~80% savings, VirtualDelta: ~90% savings)[/]");
                AnsiConsole.MarkupLine("[grey]      Actual savings depend on file content and local file state.[/]");
            }

            // Show file details if requested
            if (showDetails && (scanResult.NeedsUpdate.Count > 0 || scanResult.Missing.Count > 0))
            {
                AnsiConsole.WriteLine();
                AnsiConsole.Write(new Rule("[yellow]File Details[/]").RuleStyle("grey"));

                var detailsTable = new Table()
                    .AddColumn("File")
                    .AddColumn(new TableColumn("Size").RightAligned())
                    .AddColumn("Strategy")
                    .AddColumn("Status");

                var filesToShow = scanResult.NeedsUpdate
                    .Concat(scanResult.Missing)
                    .OrderByDescending(f => f.Size)
                    .Take(50);

                foreach (var file in filesToShow)
                {
                    var strategyColor = file.Strategy switch
                    {
                        UpdateStrategy.Delta => "blue",
                        UpdateStrategy.VirtualDelta => "cyan",
                        UpdateStrategy.HashCheck => "grey",
                        UpdateStrategy.AlwaysCompressed => "grey",
                        _ => "white"
                    };

                    var statusColor = file.Status == FileStatusType.Missing ? "red" : "yellow";

                    detailsTable.AddRow(
                        file.Path.Length > 50 ? "..." + file.Path[^47..] : file.Path,
                        FormatBytes(file.Size),
                        $"[{strategyColor}]{file.Strategy}[/]",
                        $"[{statusColor}]{file.Status}[/]");
                }

                AnsiConsole.Write(detailsTable);

                var remaining = scanResult.NeedsUpdate.Count + scanResult.Missing.Count - 50;
                if (remaining > 0)
                {
                    AnsiConsole.MarkupLine($"[grey]... and {remaining} more files[/]");
                }
            }

            // Final status
            AnsiConsole.WriteLine();
            if (scanResult.IsUpToDate)
            {
                AnsiConsole.MarkupLine("[green]Installation is up to date![/]");
                return 0;
            }
            else
            {
                var breakdown = scanResult.GetStrategyBreakdown();
                AnsiConsole.MarkupLine($"[yellow]Update required:[/] {scanResult.NeedsUpdate.Count + scanResult.Missing.Count} files");
                AnsiConsole.MarkupLine($"[yellow]Estimated download:[/] [green]{FormatBytes(breakdown.TotalEstimatedBytes)}[/] (worst case: {FormatBytes(breakdown.TotalWorstCaseBytes)})");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[grey]Run 'patchsync patch' to apply updates.[/]");
                return 1;
            }
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]Network error:[/] {ex.Message}");
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
        AnsiConsole.MarkupLine("[bold]patchsync scan[/] - Scan local installation against manifest");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Usage:[/]");
        AnsiConsole.MarkupLine("  patchsync scan -u <url> -p <path> [[options]]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Required:[/]");
        AnsiConsole.MarkupLine("  -u, --url <URL>       Base URL of the CDN hosting manifest");
        AnsiConsole.MarkupLine("  -p, --path <PATH>     Local path to the installation directory");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Options:[/]");
        AnsiConsole.MarkupLine("  -m, --manifest <PATH> Manifest file path (default: manifest.json)");
        AnsiConsole.MarkupLine("  -d, --details         Show detailed file list");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Output:[/]");
        AnsiConsole.MarkupLine("  Shows files that need updating with strategy breakdown and");
        AnsiConsole.MarkupLine("  estimated download size based on delta patching savings.");
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
