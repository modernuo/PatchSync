using PatchSync.Common.Manifest;
using Spectre.Console;

namespace PatchSync.CLI.Commands;

public partial class BuildSignatures
{
    private static string GetInputFolder() =>
        new FileBrowser
            {
                Title = "Please choose the folder to build [green]signatures[/]",
                PromptMessage = "Please choose the folder to build [green]signatures[/]"
            }
            .SelectDirectory()
            .GetPath();

    private static string GetOutputFolder(string workingDirectory) =>
        new FileBrowser
            {
                Title = "Please choose the folder to save the [green]signatures[/]",
                PromptMessage = "Please choose the folder to save the [green]signatures[/]",
                WorkingDirectory = workingDirectory
            }
            .SelectDirectory()
            .GetPath();

    private static PatchChannel GetChannel()
    {
        var result = AnsiConsole.Prompt(
            new SelectionPrompt<PatchChannel>()
                .Title("Select the [green]patch channel[/]:")
                .AddChoices(Enum.GetValues<PatchChannel>())
        );

        AnsiConsole.MarkupLineInterpolated($"Select the [green]patch channel[/]: [blue]{result}[/]");

        return result;
    }
}
