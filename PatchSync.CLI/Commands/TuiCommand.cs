using PatchSync.CLI.TUI.Core;
using PatchSync.CLI.Workspace;
using Spectre.Console;

namespace PatchSync.CLI.Commands;

/// <summary>
/// Launches the full Terminal User Interface (TUI) for workspace management.
/// </summary>
public static class TuiCommand
{
    /// <summary>
    /// Run the TUI with the specified workspace path.
    /// </summary>
    public static async Task<int> RunAsync(string? workspacePath, CancellationToken cancellationToken)
    {
        // Find workspace
        var workspace = FindWorkspace(workspacePath);

        if (workspace == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No workspace found.");
            AnsiConsole.MarkupLine("[dim]Initialize a workspace with:[/] patchsync init");
            AnsiConsole.MarkupLine("[dim]Or specify a workspace path with:[/] patchsync tui --workspace <path>");
            return 1;
        }

        if (!workspace.Exists)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Workspace not found at '{workspace.WorkspacePath}'");
            return 1;
        }

        // Load workspace configuration
        try
        {
            await workspace.LoadConfigAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error loading workspace:[/] {ex.Message}");
            return 1;
        }

        // Check terminal size
        if (Console.WindowWidth < 60 || Console.WindowHeight < 15)
        {
            AnsiConsole.MarkupLine("[yellow]Warning:[/] Terminal size is small. Recommended: 80x24 or larger.");
            AnsiConsole.MarkupLine($"[dim]Current size:[/] {Console.WindowWidth}x{Console.WindowHeight}");
        }

        // Launch TUI
        try
        {
            using var app = new TuiApplication(workspace);
            await app.RunAsync(cancellationToken);
            return 0;
        }
        catch (OperationCanceledException)
        {
            // User cancelled with Ctrl+C
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine($"[red]TUI Error:[/] {ex.Message}");
            AnsiConsole.MarkupLine($"[dim]{ex.GetType().Name}: {ex.Message}[/]");
            if (ex.StackTrace != null)
            {
                AnsiConsole.MarkupLine($"[dim]{Markup.Escape(ex.StackTrace.Split('\n').FirstOrDefault() ?? "")}[/]");
            }
            return 1;
        }
    }

    private static WorkspaceManager? FindWorkspace(string? explicitPath)
    {
        // If explicit path provided, use it
        if (!string.IsNullOrEmpty(explicitPath))
        {
            return WorkspaceManager.ForPath(explicitPath);
        }

        // Try to find workspace from current directory
        var currentDir = Directory.GetCurrentDirectory();

        // Check if current directory is a workspace
        var currentWorkspace = WorkspaceManager.ForPath(currentDir);
        if (currentWorkspace.Exists)
        {
            return currentWorkspace;
        }

        // Search parent directories for .patchsync folder
        var dir = new DirectoryInfo(currentDir);
        while (dir.Parent != null)
        {
            dir = dir.Parent;
            var candidate = WorkspaceManager.ForPath(dir.FullName);
            if (candidate.Exists)
            {
                return candidate;
            }
        }

        return null;
    }
}
