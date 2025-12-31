using System.Text.Json;
using PatchSync.Common.Manifest;
using PatchSync.Common.Signatures;
using Spectre.Console;

namespace PatchSync.CLI.Commands;

public static class InfoCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var parser = new ArgParser(args);

        if (parser.HasHelp)
        {
            ShowHelp();
            return 0;
        }

        var filePath = parser.FirstPositional;
        if (filePath == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No file specified");
            AnsiConsole.WriteLine();
            ShowHelp();
            return 1;
        }

        if (!File.Exists(filePath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {filePath}");
            return 1;
        }

        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        return ext switch
        {
            ".json" => await ShowManifestInfoAsync(filePath),
            ".sig" => await ShowSignatureInfoAsync(filePath),
            _ => ShowUnknownFile(filePath)
        };
    }

    public static async Task<int> RunWizardAsync()
    {
        AnsiConsole.MarkupLine("[grey]Display information about manifest or signature files[/]\n");

        // File type selection
        var fileType = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[green]What would you like to inspect?[/]")
                .AddChoices(
                    "Manifest file (manifest.json)",
                    "Signature file (*.sig)"));

        // File path
        var defaultPath = fileType.Contains("Manifest") ? "manifest.json" : "*.sig";
        var filePath = AnsiConsole.Prompt(
            new TextPrompt<string>($"[green]File path[/] (e.g., {defaultPath}):")
                .Validate(path =>
                {
                    if (!File.Exists(path))
                        return ValidationResult.Error($"File not found: {path}");
                    return ValidationResult.Success();
                }));

        AnsiConsole.WriteLine();

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".json" => await ShowManifestInfoAsync(filePath),
            ".sig" => await ShowSignatureInfoAsync(filePath),
            _ => ShowUnknownFile(filePath)
        };
    }

    private static async Task<int> ShowManifestInfoAsync(string filePath)
    {
        try
        {
            await using var stream = File.OpenRead(filePath);
            var manifest = await JsonSerializer.DeserializeAsync(
                stream,
                ManifestJsonContext.Default.GameManifest);

            if (manifest == null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Failed to parse manifest");
                return 1;
            }

            AnsiConsole.MarkupLine("[bold blue]Game Manifest[/]");
            AnsiConsole.WriteLine();

            var table = new Table();
            table.AddColumn("Property");
            table.AddColumn("Value");

            table.AddRow("Version", manifest.Version);
            table.AddRow("Build Date", manifest.BuildDate.ToString("yyyy-MM-dd HH:mm:ss UTC"));
            table.AddRow("Base URL", manifest.BaseUrl);
            table.AddRow("Fallback URL", manifest.FallbackUrl ?? "(none)");
            table.AddRow("Preferred Algorithm", manifest.PreferredAlgorithm);
            table.AddRow("Supported Algorithms", string.Join(", ", manifest.SupportedAlgorithms));
            table.AddRow("Total Files", manifest.Files.Count.ToString());
            table.AddRow("Total Size", FormatBytes(manifest.Files.Sum(f => f.Size)));

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();

            // Strategy breakdown
            var byStrategy = manifest.Files.GroupBy(f => f.Strategy)
                .OrderByDescending(g => g.Count())
                .ToList();

            if (byStrategy.Count > 0)
            {
                AnsiConsole.MarkupLine("[bold]Files by Strategy:[/]");
                foreach (var group in byStrategy)
                {
                    var totalSize = group.Sum(f => f.Size);
                    AnsiConsole.MarkupLine($"  {group.Key}: {group.Count()} files ({FormatBytes(totalSize)})");
                }
                AnsiConsole.WriteLine();
            }

            // Top 10 largest files
            var largestFiles = manifest.Files
                .OrderByDescending(f => f.Size)
                .Take(10)
                .ToList();

            if (largestFiles.Count > 0)
            {
                AnsiConsole.MarkupLine("[bold]Largest Files:[/]");
                var fileTable = new Table();
                fileTable.AddColumn("File");
                fileTable.AddColumn("Size");
                fileTable.AddColumn("Strategy");

                foreach (var file in largestFiles)
                {
                    fileTable.AddRow(file.Path, FormatBytes(file.Size), file.Strategy.ToString());
                }

                AnsiConsole.Write(fileTable);
            }

            return 0;
        }
        catch (JsonException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error parsing manifest:[/] {ex.Message}");
            return 1;
        }
    }

    private static Task<int> ShowSignatureInfoAsync(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var signature = SignatureFile.Read(stream);

            AnsiConsole.MarkupLine("[bold blue]Signature File[/]");
            AnsiConsole.WriteLine();

            var table = new Table();
            table.AddColumn("Property");
            table.AddColumn("Value");

            table.AddRow("Algorithm", signature.AlgorithmId);
            table.AddRow("Version", signature.AlgorithmVersion.ToString());
            table.AddRow("Min Chunk Size", FormatBytes(signature.MinSize));
            table.AddRow("Avg Chunk Size", FormatBytes(signature.AverageSize));
            table.AddRow("Max Chunk Size", FormatBytes(signature.MaxSize));
            table.AddRow("Total Chunks", signature.Chunks.Count.ToString());
            table.AddRow("Total Size", FormatBytes(signature.TotalSize));

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();

            // Chunk size statistics
            if (signature.Chunks.Count > 0)
            {
                var sizes = signature.Chunks.Select(c => c.Length).ToList();
                var avgSize = sizes.Average();
                var minSize = sizes.Min();
                var maxSize = sizes.Max();

                AnsiConsole.MarkupLine("[bold]Chunk Statistics:[/]");
                AnsiConsole.MarkupLine($"  Minimum: {FormatBytes(minSize)}");
                AnsiConsole.MarkupLine($"  Average: {FormatBytes((long)avgSize)}");
                AnsiConsole.MarkupLine($"  Maximum: {FormatBytes(maxSize)}");
                AnsiConsole.WriteLine();

                // First few chunks
                AnsiConsole.MarkupLine("[bold]First 10 Chunks:[/]");
                var chunkTable = new Table();
                chunkTable.AddColumn("Offset");
                chunkTable.AddColumn("Length");
                chunkTable.AddColumn("Hash");

                foreach (var chunk in signature.Chunks.Take(10))
                {
                    chunkTable.AddRow(
                        chunk.Offset.ToString("N0"),
                        FormatBytes(chunk.Length),
                        chunk.HashHex[..16] + "...");
                }

                AnsiConsole.Write(chunkTable);
            }

            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error reading signature:[/] {ex.Message}");
            return Task.FromResult(1);
        }
    }

    private static int ShowUnknownFile(string filePath)
    {
        AnsiConsole.MarkupLine($"[yellow]Unknown file type:[/] {filePath}");
        AnsiConsole.MarkupLine("Supported formats: .json (manifest), .sig (signature)");
        return 1;
    }

    private static void ShowHelp()
    {
        AnsiConsole.MarkupLine("[bold]patchsync info[/] - Display information about files");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Usage:[/]");
        AnsiConsole.MarkupLine("  patchsync info <file>");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Supported files:[/]");
        AnsiConsole.MarkupLine("  manifest.json    Game manifest file");
        AnsiConsole.MarkupLine("  *.sig            Signature file");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Examples:[/]");
        AnsiConsole.MarkupLine("  patchsync info manifest.json");
        AnsiConsole.MarkupLine("  patchsync info game.exe.sig");
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
