using PatchSync.CLI.TUI.Core;
using PatchSync.CLI.TUI.Panels;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace PatchSync.CLI.TUI.Screens;

/// <summary>
/// Build progress screen showing active or completed build status.
/// </summary>
public sealed class BuildScreen : IScreen
{
    private readonly ProgressPanel _progressPanel = new();
    private readonly StatusBar _statusBar = new();
    private readonly ActionBar _actionBar = new();

    public TuiScreen ScreenType => TuiScreen.Build;

    public IRenderable Render(TuiState state)
    {
        var layout = new Layout("Root")
            .SplitRows(
                new Layout("StatusBar").Size(1),
                new Layout("Main"),
                new Layout("ActionBar").Size(1));

        layout["StatusBar"].Update(_statusBar.Render(state, false));
        layout["ActionBar"].Update(_actionBar.Render(state, false));

        var content = RenderBuildContent(state);

        var panel = new Panel(content)
        {
            Header = new PanelHeader(" Build "),
            Border = BoxBorder.Rounded,
            Padding = new Padding(2, 1)
        };

        layout["Main"].Update(panel);

        return layout;
    }

    private IRenderable RenderBuildContent(TuiState state)
    {
        var build = state.Build;
        var rows = new List<IRenderable>();

        if (build.IsBuilding)
        {
            rows.Add(new Rule($"[yellow]Building {Markup.Escape(state.Workspace.CurrentVersion ?? "...")}[/]"));
            rows.Add(Text.Empty);

            // Progress bar from ProgressPanel
            rows.Add(_progressPanel.RenderBuildProgress(state));
        }
        else if (build.IsCompleted)
        {
            rows.Add(new Rule("[green]Build Complete[/]"));
            rows.Add(Text.Empty);
            rows.Add(new Markup($"[green]✓[/] Built {build.TotalFiles:N0} files"));
            rows.Add(new Markup($"[dim]Duration:[/] {FormatDuration(build.Elapsed)}"));

            if (build.ComparisonResult != null)
            {
                rows.Add(Text.Empty);
                rows.Add(new Markup("[dim]Changes from base:[/]"));
                rows.Add(new Markup($"  [green]+{build.ComparisonResult.NewFiles.Count}[/] new"));
                rows.Add(new Markup($"  [yellow]~{build.ComparisonResult.ModifiedFiles.Count}[/] modified"));
                rows.Add(new Markup($"  [red]-{build.ComparisonResult.DeletedFiles.Count}[/] deleted"));
            }

            rows.Add(Text.Empty);
            rows.Add(new Markup("[dim]Press [/][yellow]Esc[/][dim] to return to dashboard[/]"));
        }
        else if (!string.IsNullOrEmpty(build.Error))
        {
            rows.Add(new Rule("[red]Build Failed[/]"));
            rows.Add(Text.Empty);
            rows.Add(new Markup($"[red]Error:[/] {Markup.Escape(build.Error)}"));
            rows.Add(Text.Empty);
            rows.Add(new Markup("[dim]Press [/][yellow]Esc[/][dim] to return to dashboard[/]"));
        }
        else
        {
            rows.Add(new Markup("[dim]No build in progress[/]"));
            rows.Add(Text.Empty);
            rows.Add(new Markup("[dim]Press [/][yellow]b[/][dim] from the dashboard to start a build[/]"));
        }

        return new Rows(rows);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}";
        }
        return $"{duration.Minutes:D2}:{duration.Seconds:D2}";
    }
}
