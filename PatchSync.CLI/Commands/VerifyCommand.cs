using System.Security.Cryptography;
using PatchSync.CLI.Wizard;
using PatchSync.CLI.Wizard.Steps;
using PatchSync.CLI.Wizard.Themes;
using PatchSync.SDK.Client;
using PatchSync.SDK.Storage;
using Spectre.Console;

namespace PatchSync.CLI.Commands;

public static class VerifyCommand
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
            var fix = parser.GetBool("fix");

            return await ExecuteAsync(url, localPath, manifestPath, fix);
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
        var wizard = new WizardRunner("Verify Installation", new BoxTheme())
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
                prompt: "Select installation directory to verify"))
            .AddStep(new TextStep(
                key: "manifestPath",
                displayName: "Manifest File",
                prompt: "Manifest file (relative to CDN URL)",
                defaultValue: "manifest.json"))
            .AddStep(new ConfirmStep(
                key: "fix",
                displayName: "Auto-Fix",
                question: "Automatically fix mismatched files?",
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
        var fix = ctx.Get<bool>("fix");

        return await ExecuteAsync(url, localPath, manifestPath, fix);
    }

    private static async Task<int> ExecuteAsync(
        string url,
        string localPath,
        string manifestPath,
        bool fix)
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

        AnsiConsole.MarkupLine($"[blue]Verifying:[/] {localPath}");
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
            AnsiConsole.MarkupLine($"[green]Files:[/] {manifest.Files.Count}");
            AnsiConsole.WriteLine();

            var table = new Table();
            table.AddColumn("File");
            table.AddColumn("Expected");
            table.AddColumn("Actual");
            table.AddColumn("Status");

            var mismatches = new List<string>();
            var missing = new List<string>();
            var matched = 0;

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
                    var task = ctx.AddTask("Verifying files", autoStart: true);

                    for (int i = 0; i < manifest.Files.Count; i++)
                    {
                        var file = manifest.Files[i];
                        task.Description = file.Path;
                        task.Value = (double)i / manifest.Files.Count * 100;

                        var filePath = Path.Combine(
                            localPath,
                            file.Path.Replace('/', Path.DirectorySeparatorChar));

                        if (!File.Exists(filePath))
                        {
                            missing.Add(file.Path);
                            table.AddRow(
                                file.Path,
                                file.Hash[..8] + "...",
                                "[red]MISSING[/]",
                                "[red]FAIL[/]");
                            continue;
                        }

                        var actualHash = await ComputeHashAsync(filePath);

                        if (string.Equals(actualHash, file.Hash, StringComparison.OrdinalIgnoreCase))
                        {
                            matched++;
                            // Only show first 50 matches to avoid huge output
                            if (matched <= 50)
                            {
                                table.AddRow(
                                    file.Path,
                                    file.Hash[..8] + "...",
                                    actualHash[..8] + "...",
                                    "[green]OK[/]");
                            }
                        }
                        else
                        {
                            mismatches.Add(file.Path);
                            table.AddRow(
                                file.Path,
                                file.Hash[..8] + "...",
                                actualHash[..8] + "...",
                                "[red]MISMATCH[/]");
                        }
                    }

                    task.Value = 100;
                });

            if (matched > 50)
            {
                table.AddRow(
                    $"[grey]... and {matched - 50} more files[/]",
                    "",
                    "",
                    "[green]OK[/]");
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();

            // Summary
            if (missing.Count == 0 && mismatches.Count == 0)
            {
                AnsiConsole.MarkupLine("[green]:check_mark_button: Verification complete![/]");
                AnsiConsole.WriteLine();

                var summaryTable = new Table()
                    .Border(TableBorder.Rounded)
                    .AddColumn("Property")
                    .AddColumn("Value");

                summaryTable.AddRow("Version", manifest.Version);
                summaryTable.AddRow("Files Verified", manifest.Files.Count.ToString());
                summaryTable.AddRow("Status", "[green]All files match[/]");

                AnsiConsole.Write(summaryTable);
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[grey]Installation is verified.[/]");

                return 0;
            }

            AnsiConsole.MarkupLine("[yellow]Verification found issues:[/]");
            AnsiConsole.MarkupLine($"  Matched: {matched}");
            AnsiConsole.MarkupLine($"  Missing: {missing.Count}");
            AnsiConsole.MarkupLine($"  Mismatched: {mismatches.Count}");

            if (fix)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[blue]Fixing files...[/]");

                await client.PatchAsync(localPath, manifest);

                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[green]:check_mark_button: Files fixed![/]");
                AnsiConsole.MarkupLine("[grey]Installation has been repaired.[/]");
                return 0;
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]Run with --fix to repair mismatched files[/]");
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
        AnsiConsole.MarkupLine("[bold]patchsync verify[/] - Verify local files against manifest");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Usage:[/]");
        AnsiConsole.MarkupLine("  patchsync verify -u <url> -p <path> [[options]]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Required:[/]");
        AnsiConsole.MarkupLine("  -u, --url <URL>       Base URL of the CDN hosting manifest");
        AnsiConsole.MarkupLine("  -p, --path <PATH>     Local path to the installation directory");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Options:[/]");
        AnsiConsole.MarkupLine("  -m, --manifest <PATH> Manifest file path (default: manifest.json)");
        AnsiConsole.MarkupLine("      --fix             Fix mismatched files by re-downloading");
    }

    private static async Task<string> ComputeHashAsync(string filePath)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
