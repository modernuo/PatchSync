using PatchSync.CLI.TUI.Core;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace PatchSync.CLI.TUI.Panels;

/// <summary>
/// Bottom action bar showing context-sensitive keyboard shortcuts.
/// </summary>
public sealed class ActionBar : IPanel
{
    public IRenderable Render(TuiState state, bool isActive)
    {
        var actionBar = KeyDispatcher.GetActionBar(state);
        return new Markup($"[dim]{Markup.Escape(actionBar)}[/]");
    }

    /// <summary>
    /// Get expanded help text for current context.
    /// </summary>
    public static IEnumerable<(string Key, string Description)> GetExpandedHelp(TuiState state)
    {
        if (state.Dialog != null)
        {
            if (state.Dialog.Type == DialogType.Confirm)
            {
                yield return ("y", "Confirm");
                yield return ("n", "Cancel");
            }
            else if (state.Dialog.Choices != null)
            {
                yield return ("↑/↓", "Select option");
                yield return ("Enter", "Confirm selection");
            }
            else
            {
                yield return ("Enter", "OK");
            }
            yield return ("Esc", "Close");
            yield break;
        }

        if (state.Input != null)
        {
            yield return ("Enter", "Submit");
            yield return ("Esc", "Cancel");
            yield return ("Backspace", "Delete character");
            yield break;
        }

        // Navigation
        yield return ("j/k", "Navigate up/down");
        yield return ("g/G", "First/last item");
        yield return ("Ctrl+d/u", "Page down/up");
        yield return ("Tab/h/l", "Switch panel");
        yield return ("Enter", "Select");

        // Screen-specific
        switch (state.CurrentScreen)
        {
            case TuiScreen.Dashboard:
                if (state.FileList.SelectedFile != null)
                {
                    yield return ("s", "Change strategy");
                    yield return ("d", "Delete file");
                    yield return ("Space", "Toggle selection");
                    yield return ("Ctrl+a", "Select all");
                }
                yield return ("/", "Filter");
                if (state.Workspace.CurrentVersion != null)
                {
                    yield return ("b", "Start build");
                    yield return ("p", "Publish");
                }
                break;

            case TuiScreen.Versions:
                yield return ("n", "New version");
                if (state.Workspace.CurrentVersion != null)
                {
                    yield return ("p", "Promote version");
                }
                break;

            case TuiScreen.Build:
                if (state.Build.IsBuilding)
                {
                    yield return ("c", "Cancel build");
                }
                break;

            case TuiScreen.Settings:
                yield return ("c", "CDN setup wizard");
                break;
        }

        // Global
        yield return ("1/2/3", "Switch screen");
        yield return ("Esc", "Back/cancel");
        yield return ("q", "Quit");
    }
}
