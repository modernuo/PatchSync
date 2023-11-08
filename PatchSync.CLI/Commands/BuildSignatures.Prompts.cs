using PatchSync.Manifest;
using Spectre.Console;

namespace PatchSync.CLI.Commands;

public partial class BuildSignatures
{
  private static string GetInputFolder()
  {
    return AnsiConsole.Prompt(
      new TextPrompt<string>("Please specify the folder with files to create [green]signatures[/]:")
        .PromptStyle("blue")
        .Validate(folder =>
        {
          if (!Directory.Exists(folder))
          {
            return ValidationResult.Error("[red]You must specify an existing folder.[/]");
          }

          if (Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any())
          {
            return ValidationResult.Success();
          }
          
          return ValidationResult.Error("[red]You must specify a folder with content.[/]");
        }));
  }
  
  private static string GetOutputFolder()
  {
    return AnsiConsole.Prompt(
      new TextPrompt<string>("Please specify the folder to save the [green]signatures[/]:")
        .DefaultValue("output")
        .PromptStyle("blue")
        .Validate(folder =>
        {
          try
          {
            Directory.CreateDirectory(folder);
            return ValidationResult.Success();
          }
          catch
          {
            return ValidationResult.Error("[red]Could not validate or create the folder.[/]");
          }
        }));
  }

  private static PatchChannel GetChannel()
  {
    var result = AnsiConsole.Prompt(
      new SelectionPrompt<PatchChannel>()
        .Title("Select the patch [green]channel[/]:")
        .AddChoices(Enum.GetValues<PatchChannel>())
    );
    
    AnsiConsole.MarkupLineInterpolated($"Select the patch [green]channel[/]: [blue]{result}[/]");

    return result;
  }
}