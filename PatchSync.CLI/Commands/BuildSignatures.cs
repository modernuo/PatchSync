using System.Collections.Concurrent;
using System.Text.Json;
using PatchSync.CLI.Json;
using PatchSync.Common.Manifest;
using PatchSync.Manifest;
using PatchSync.Signatures;
using Spectre.Console;

namespace PatchSync.CLI.Commands;

public partial class BuildSignatures : ICommand
{
  private string? _baseFolder;
  private string? _outputFolder;
  private ConcurrentQueue<ManifestFileEntry>? _signatureQueue;
  private ProgressContext? _ctx;

  public string Name => "Build Signatures";

  public void ExecuteCommand(CLIContext cliContext)
  {
    _baseFolder = GetInputFolder();
    _outputFolder = GetOutputFolder();
    var channel = GetChannel();
    
    _outputFolder = Path.Combine(_outputFolder, channel.ToString().ToLower());

    AnsiConsole.Write(new Rule("[green3]Building Signatures[/]"));
    
    var files = Directory.GetFiles(_baseFolder, "*", SearchOption.AllDirectories)
      .Where(file => new FileInfo(file).Length >= 1024 * 1024).ToArray();

    Array.Sort(files,
      (a, b) =>
      {
        var af = new FileInfo(a);
        var bf = new FileInfo(b);

        // Largest files first
        var result = (int)(bf.Length - af.Length);
    
        // Then alphabetical
        return result == 0 ? string.Compare(bf.Name, af.Name, StringComparison.Ordinal) : result;
      });

    _signatureQueue = new ConcurrentQueue<ManifestFileEntry>();

    AnsiConsole.Progress()
      .Columns(
        new TaskDescriptionColumn(),
        new ProgressBarColumn(),
        new PercentageColumn(),
        new SpinnerColumn(),
        new DownloadedColumn()
      )
      .Start(
        ctx =>
        {
          _ctx = ctx;
          
          var concurrency = Math.Max(Environment.ProcessorCount, 1);
          foreach (var file in files)
          {
            var relativeFilePath = Path.GetRelativePath(_baseFolder, file);
            var signatureFilePath = Path.Combine(_outputFolder, relativeFilePath);
            Directory.CreateDirectory(Path.GetDirectoryName(signatureFilePath)!);
          }
      
          Parallel.ForEach(files,
            new ParallelOptions{ MaxDegreeOfParallelism = concurrency },
            DoWork
          );
        }
      );

    AnsiConsole.Write("Signatures saved to ");
    CLIText.WriteFilePathText(Path.GetFullPath(_outputFolder));
    AnsiConsole.WriteLine("");

    AnsiConsole.Write(new Rule("[green3]Building Manifest[/]"));

    var manifestPath = Path.Combine(_outputFolder, "manifest.json");
    var patchManifest = ManifestBuilder.GenerateManifest(
      channel.ToString()
        .ToLower(),
      _signatureQueue.ToArray()
    );
    
    using var stream = File.Open(manifestPath, FileMode.Create);
    
    JsonSerializer.Serialize(stream, patchManifest, JsonSourceGenerationContext.Default.PatchManifest);

    AnsiConsole.WriteLine("");
    AnsiConsole.Write("Manifest saved to ");
    CLIText.WriteFilePathText(manifestPath);
    AnsiConsole.WriteLine("");
    
    cliContext.SetProperty(nameof(PatchManifest), (patchManifest, manifestPath));

    AnsiConsole.Write(new Rule("[green3]Finished[/]"));
  }
  
  private void DoWork(
    string file
  )
  {
    var task = _ctx!.AddTask($"[green]{Path.GetFileName(file)}[/]");
    var relativeFilePath = Path.GetRelativePath(_baseFolder!, file);
    var signatureFilePath = Path.Combine(_outputFolder!, $"{relativeFilePath}.sig");
    var totalHash = SignatureGenerator.GenerateSignature(file, signatureFilePath, 1024, task);
    _signatureQueue!.Enqueue(new ManifestFileEntry(ManifestFileCommand.DeltaUpdate, relativeFilePath, (uint)new FileInfo(file).Length, totalHash.ToString()));
  }
}