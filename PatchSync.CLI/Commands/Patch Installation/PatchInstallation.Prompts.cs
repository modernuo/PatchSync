using Spectre.Console;

namespace PatchSync.CLI.Commands.Patch_Installation;

public partial class PatchInstallation
{
    private static string GetInputFolder() =>
        new FileBrowser
            {
                Title = "Please choose the folder to patch [green]files[/]",
                PromptMessage = "Please choose the folder to patch [green]files[/]"
            }
            .SelectDirectory()
            .GetPath();

    private static string GetManifestFile() =>
        new FileBrowser
            {
                Title = "Please select the manifest file [green]manifest[/]",
                SearchPattern = "manifest.json"
            }
            .SelectFile()
            .GetPath();

    private static bool PromptReadyToPatch() =>
        AnsiConsole.Prompt(
            new ConfirmationPrompt("Press yes to start [green]patching[/]:")
            {
                DefaultValue = true
            }
        );
}
