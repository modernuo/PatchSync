using PatchSync.CLI.TUI.Core;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace PatchSync.CLI.TUI.Panels;

/// <summary>
/// Top status bar showing workspace, channel, version, and screen tabs.
/// </summary>
public sealed class StatusBar : IPanel
{
    public IRenderable Render(TuiState state, bool isActive)
    {
        var parts = new List<string>
        {
            $"[aqua]{Markup.Escape(state.Workspace.ProjectName)}[/]",
            $"[dim]|[/] {Markup.Escape(state.Workspace.CurrentChannel)}"
        };

        if (!string.IsNullOrEmpty(state.Workspace.CurrentVersion))
        {
            parts.Add($"[dim]|[/] [green]{Markup.Escape(state.Workspace.CurrentVersion)}[/]");
        }

        // Screen tabs
        var tabs = BuildScreenTabs(state.CurrentScreen);
        parts.Add($"[dim]|[/] {tabs}");

        // Status message
        if (state.Status != null && !state.Status.IsExpired)
        {
            var statusStyle = state.Status.Type switch
            {
                StatusType.Success => "green",
                StatusType.Warning => "yellow",
                StatusType.Error => "red",
                _ => "dim"
            };
            parts.Add($"[dim]|[/] [{statusStyle}]{Markup.Escape(state.Status.Text)}[/]");
        }

        return new Markup(string.Join(" ", parts));
    }

    private static string BuildScreenTabs(TuiScreen currentScreen)
    {
        var screens = new[] { "1:Dashboard", "2:Versions", "3:Settings" };
        var screenIndex = currentScreen switch
        {
            TuiScreen.Dashboard => 0,
            TuiScreen.Versions => 1,
            TuiScreen.Settings => 2,
            _ => -1
        };

        return string.Join(" ", screens.Select((s, i) =>
            i == screenIndex ? $"[aqua underline]{s}[/]" : $"[dim]{s}[/]"));
    }
}
