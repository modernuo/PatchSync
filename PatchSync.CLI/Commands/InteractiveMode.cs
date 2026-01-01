using PatchSync.CLI.Workspace;
using Spectre.Console;

namespace PatchSync.CLI.Commands;

/// <summary>
/// Handles interactive mode when PatchSync is run without arguments.
/// Separated from System.CommandLine to allow proper Ctrl+C navigation handling.
/// </summary>
public static class InteractiveMode
{
    /// <summary>
    /// Run the interactive mode.
    /// </summary>
    /// <param name="workspacePath">Optional workspace path to open directly.</param>
    public static async Task<int> RunAsync(string? workspacePath)
    {
        if (!string.IsNullOrEmpty(workspacePath))
        {
            var manager = WorkspaceManager.ForPath(workspacePath);
            if (!manager.Exists)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] No workspace found at '{workspacePath}'");
                return 1;
            }
            return await WorkspaceMenu.ShowWorkspaceMenuAsync(manager);
        }

        return await WorkspaceMenu.ShowMainMenuAsync();
    }
}
