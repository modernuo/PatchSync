using System.Text.Json;
using PatchSync.CLI.Prompts;
using PatchSync.Common.Chunking;
using PatchSync.Common.Manifest;
using PatchSync.SDK.Signatures;
using Spectre.Console;

namespace PatchSync.CLI.Commands;

public static class BuildCommand
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
            var inputPath = parser.GetRequired("input", "i");
            var outputPath = parser.GetRequired("output", "o");
            var version = parser.GetRequired("version", "v");
            var baseUrl = parser.GetOrDefault("base-url", "", "u");
            var fallbackUrl = parser.Get("fallback-url");
            var minDeltaSize = parser.GetLong("min-delta-size", 64 * 1024);
            var algorithm = parser.GetOrDefault("algorithm", "fastcdc-v1", "a");
            var minChunk = parser.GetInt("min-chunk", 4096);
            var avgChunk = parser.GetInt("avg-chunk", 16384);
            var maxChunk = parser.GetInt("max-chunk", 65536);

            return await ExecuteAsync(
                inputPath, outputPath, version, baseUrl, fallbackUrl,
                minDeltaSize, algorithm, minChunk, avgChunk, maxChunk);
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
        AnsiConsole.MarkupLine("[grey]Generate signatures and manifest for a directory[/]\n");

        // Input directory - use file browser
        var useBrowser = await AnsiConsole.ConfirmAsync("Browse for input directory?");
        string inputPath;
        if (useBrowser)
        {
            inputPath = Browse.ForFolder("[green]Select input directory[/] (containing game files)");
        }
        else
        {
            inputPath = AnsiConsole.Prompt(
                new TextPrompt<string>("[green]Input directory path:[/]")
                    .Validate(path =>
                    {
                        if (!Directory.Exists(path))
                            return ValidationResult.Error($"Directory not found: {path}");
                        return ValidationResult.Success();
                    }));
        }
        AnsiConsole.MarkupLine($"[blue]Input:[/] {inputPath}\n");

        // Output directory
        useBrowser = await AnsiConsole.ConfirmAsync("Browse for output directory?");
        string outputPath;
        if (useBrowser)
        {
            outputPath = Browse.ForFolder("[green]Select output directory[/]", allowNew: true);
        }
        else
        {
            outputPath = AnsiConsole.Prompt(
                new TextPrompt<string>("[green]Output directory path:[/]")
                    .DefaultValue("./output"));
        }
        AnsiConsole.MarkupLine($"[blue]Output:[/] {outputPath}\n");

        // Version
        var version = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]Version[/] (e.g., 1.0.0):")
                .Validate(v => !string.IsNullOrWhiteSpace(v)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Version is required")));

        // Base URL
        var baseUrl = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]Base URL[/] (where files will be hosted, optional):")
                .AllowEmpty()
                .DefaultValue(""));

        // Algorithm selection
        var registry = ChunkerRegistry.Default;
        var algorithm = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[green]Chunking algorithm:[/]")
                .AddChoices(registry.SupportedAlgorithms));

        // Advanced options
        var useAdvanced = await AnsiConsole.ConfirmAsync("Configure advanced chunking options?", false);

        int minChunk = 4096;
        int avgChunk = 16384;
        int maxChunk = 65536;
        long minDeltaSize = 64 * 1024;

        if (useAdvanced)
        {
            minChunk = AnsiConsole.Prompt(
                new TextPrompt<int>("[green]Minimum chunk size[/] (bytes):")
                    .DefaultValue(4096));

            avgChunk = AnsiConsole.Prompt(
                new TextPrompt<int>("[green]Average chunk size[/] (bytes):")
                    .DefaultValue(16384));

            maxChunk = AnsiConsole.Prompt(
                new TextPrompt<int>("[green]Maximum chunk size[/] (bytes):")
                    .DefaultValue(65536));

            minDeltaSize = AnsiConsole.Prompt(
                new TextPrompt<long>("[green]Minimum delta size[/] (bytes):")
                    .DefaultValue(65536L));
        }

        AnsiConsole.WriteLine();

        return await ExecuteAsync(
            inputPath, outputPath, version, baseUrl, null,
            minDeltaSize, algorithm, minChunk, avgChunk, maxChunk);
    }

    private static async Task<int> ExecuteAsync(
        string inputPath,
        string outputPath,
        string version,
        string baseUrl,
        string? fallbackUrl,
        long minDeltaSize,
        string algorithm,
        int minChunk,
        int avgChunk,
        int maxChunk)
    {
        // Validate paths
        if (!Directory.Exists(inputPath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Input directory not found: {inputPath}");
            return 1;
        }

        // Get chunker
        var registry = ChunkerRegistry.Default;
        if (!registry.TryGetChunker(algorithm, out var chunker))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Unknown algorithm: {algorithm}");
            AnsiConsole.MarkupLine($"Available algorithms: {string.Join(", ", registry.SupportedAlgorithms)}");
            return 1;
        }

        var options = new ChunkingOptions
        {
            MinSize = minChunk,
            AverageSize = avgChunk,
            MaxSize = maxChunk
        };

        try
        {
            options.Validate();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Invalid chunking options: {ex.Message}");
            return 1;
        }

        // Create output directories
        Directory.CreateDirectory(outputPath);
        var sigDir = Path.Combine(outputPath, "signatures");
        Directory.CreateDirectory(sigDir);

        var manifestGenerator = new ManifestGenerator(chunker!, options);

        var generatorOptions = new ManifestGeneratorOptions
        {
            MinDeltaSize = minDeltaSize,
            GenerateSignatures = true,
            SignatureOutputDirectory = sigDir,
            FallbackUrl = fallbackUrl
        };

        AnsiConsole.MarkupLine($"[blue]Building manifest for:[/] {inputPath}");
        AnsiConsole.MarkupLine($"[blue]Output directory:[/] {outputPath}");
        AnsiConsole.MarkupLine($"[blue]Version:[/] {version}");
        AnsiConsole.MarkupLine($"[blue]Algorithm:[/] {algorithm}");
        AnsiConsole.WriteLine();

        var manifest = await AnsiConsole.Progress()
            .AutoRefresh(true)
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("Processing files", autoStart: true);

                var progress = new Progress<ManifestGeneratorProgress>(p =>
                {
                    task.Description = p.CurrentFile ?? "Processing...";
                    task.Value = p.Percentage * 100;
                });

                var result = await manifestGenerator.GenerateManifestAsync(
                    inputPath,
                    version,
                    baseUrl,
                    generatorOptions,
                    progress);

                task.Value = 100;
                task.Description = "Complete";

                return result;
            });

        // Write manifest
        var manifestPath = Path.Combine(outputPath, "manifest.json");
        await using var manifestStream = File.Create(manifestPath);
        await JsonSerializer.SerializeAsync(
            manifestStream,
            manifest,
            ManifestJsonContext.Default.GameManifest);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]Build complete![/]");
        AnsiConsole.MarkupLine($"  Files processed: {manifest.Files.Count}");
        AnsiConsole.MarkupLine($"  Total size: {FormatBytes(manifest.Files.Sum(f => f.Size))}");
        AnsiConsole.MarkupLine($"  Manifest: {manifestPath}");
        AnsiConsole.MarkupLine($"  Signatures: {sigDir}");

        return 0;
    }

    private static void ShowHelp()
    {
        AnsiConsole.MarkupLine("[bold]patchsync build[/] - Generate signatures and manifest");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Usage:[/]");
        AnsiConsole.MarkupLine("  patchsync build -i <input> -o <output> -v <version> [[options]]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Required:[/]");
        AnsiConsole.MarkupLine("  -i, --input <PATH>        Input directory containing files to process");
        AnsiConsole.MarkupLine("  -o, --output <PATH>       Output directory for manifest and signatures");
        AnsiConsole.MarkupLine("  -v, --version <VERSION>   Version string for the manifest");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Options:[/]");
        AnsiConsole.MarkupLine("  -u, --base-url <URL>      Base URL where files will be hosted");
        AnsiConsole.MarkupLine("      --fallback-url <URL>  Fallback URL for full archive download");
        AnsiConsole.MarkupLine("  -a, --algorithm <NAME>    Chunking algorithm (default: fastcdc-v1)");
        AnsiConsole.MarkupLine("      --min-delta-size <N>  Min file size for delta (default: 65536)");
        AnsiConsole.MarkupLine("      --min-chunk <N>       Minimum chunk size (default: 4096)");
        AnsiConsole.MarkupLine("      --avg-chunk <N>       Average chunk size (default: 16384)");
        AnsiConsole.MarkupLine("      --max-chunk <N>       Maximum chunk size (default: 65536)");
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
