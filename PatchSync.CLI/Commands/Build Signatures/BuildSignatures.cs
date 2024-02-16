using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using PatchSync.CLI.Json;
using PatchSync.Common.Manifest;
using PatchSync.Common.Text;
using PatchSync.Manifest;
using PatchSync.SDK.Threading;
using PatchSync.Signatures;
using Spectre.Console;

namespace PatchSync.CLI.Commands;

public partial class BuildSignatures : ICommand
{
    private string? _baseFolder;
    private ProgressContext? _ctx;
    private ConcurrentQueue<ManifestFileEntry>? _manifestFileEntries;
    private string? _outputFolder;
    private string? _signatureOutputFolder;

    public string Name => "Build Signatures";

    public void ExecuteCommand(CLIContext cliContext)
    {
        _baseFolder = GetInputFolder();
        _outputFolder = GetOutputFolder(_baseFolder);
        var channel = GetChannel();

        _outputFolder = Path.Combine(_outputFolder, channel.ToString().ToLower());

        AnsiConsole.Write(new Rule("[green3]Building Signatures[/]"));

        var files = Directory.GetFiles(_baseFolder, "*", SearchOption.AllDirectories)
            .Where(
                file =>
                {
                    var fi = new FileInfo(file);

                    return fi.Extension != ".sig" && fi.Name != "manifest.json";
                }
            )
            .ToArray();

        Array.Sort(
            files,
            (a, b) =>
            {
                var af = new FileInfo(a);
                var bf = new FileInfo(b);

                // Smallest files first
                var result = (int)(af.Length - bf.Length);

                // Then alphabetical
                return result == 0 ? string.Compare(bf.Name, af.Name, StringComparison.Ordinal) : result;
            }
        );

        _manifestFileEntries = new ConcurrentQueue<ManifestFileEntry>();

        var now = DateTime.UtcNow;
        _signatureOutputFolder = Path.Combine(_outputFolder, now.ToString("yyyy-MM-dd-HH-mm-ss"));

        AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn(),
                new DownloadedColumn()
            )
            .HideCompleted(true)
            .AutoClear(true)
            .Start(
                ctx =>
                {
                    _ctx = ctx;

                    foreach (var file in files)
                    {
                        var relativeFilePath = Path.GetRelativePath(_baseFolder, file);
                        var signatureFilePath = Path.Combine(_signatureOutputFolder, relativeFilePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(signatureFilePath)!);
                    }

                    ThreadWorker<string>.MapParallel(files, DoWork);
                }
            );

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLineInterpolated($"Signatures saved to [green]{Path.GetFullPath(_signatureOutputFolder)}[/]");
        AnsiConsole.WriteLine();

        AnsiConsole.Write(new Rule("[green3]Building Manifest[/]"));

        var sortedFiles = _manifestFileEntries.ToArray();
        Array.Sort(
            sortedFiles,
            (a, b) =>
                StringComparer.OrdinalIgnoreCase.Compare(a.FilePath, b.FilePath)
        );

        var manifestPath = Path.Combine(_outputFolder, "manifest.json");
        var patchManifest = ManifestBuilder.GenerateManifest(
            channel.ToString().ToLower(),
            sortedFiles,
            now
        );

        using var stream = File.Open(manifestPath, FileMode.Create);

        JsonSerializer.Serialize(stream, patchManifest, JsonSourceGenerationContext.Default.PatchManifest);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLineInterpolated($"Signatures saved to [green]{manifestPath}[/]");
        AnsiConsole.WriteLine();

        cliContext.SetProperty(nameof(PatchManifest), (patchManifest, manifestPath));

        AnsiConsole.Write(new Rule("[green3]Finished[/]"));
    }

    private void DoWork(
        string file,
        CancellationToken? token
    )
    {
        var fi = new FileInfo(file);
        var fileSize = fi.Length;
        var task = _ctx!.AddTask($"[green]{Path.GetFileName(file)}[/]");
        var relativeFilePath = Path.GetRelativePath(_baseFolder!, file);

        // Generate Signature
        if (fileSize >= 1024)
        {
            var signatureFilePath = Path.Combine(_signatureOutputFolder!, $"{relativeFilePath}.sig");
            var chunkSize = ManifestFileEntry.GetChunkSize(fileSize);
            var (fastHash, fullHash) = SignatureGenerator.GenerateSignature(file, signatureFilePath, chunkSize, task);
            var manifestEntry = new ManifestFileEntry(
                ManifestFileCommand.DeltaUpdate,
                relativeFilePath,
                fi.Length
            )
            {
                ChunkSize = chunkSize,
                FastHash = fastHash.ToString(),
                Hash = fullHash.ToHexString()
            };

            _manifestFileEntries!.Enqueue(manifestEntry);
        }
        else
        {
            task.MaxValue(fi.Length).StartTask();
            Span<byte> buffer = stackalloc byte[32];
            using var stream = File.Open(file, FileMode.Open, FileAccess.Read);
            SHA256.HashData(stream, buffer);
            var manifestEntry = new ManifestFileEntry(
                ManifestFileCommand.UpdateIfFullHashMismatch,
                relativeFilePath,
                fi.Length
            )
            {
                Hash = buffer.ToHexString()
            };
            _manifestFileEntries!.Enqueue(manifestEntry);
            task.Increment(fi.Length);
            task.StopTask();
        }
    }
}
