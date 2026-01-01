using PatchSync.CLI.TUI.Core;
using PatchSync.CLI.TUI.Panels;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace PatchSync.CLI.TUI.Screens;

/// <summary>
/// Publish screen for upload progress and confirmation.
/// </summary>
public sealed class PublishScreen : IScreen
{
    private readonly ProgressPanel _progressPanel = new();
    private readonly StatusBar _statusBar = new();
    private readonly ActionBar _actionBar = new();

    public TuiScreen ScreenType => TuiScreen.Publish;

    public IRenderable Render(TuiState state)
    {
        var layout = new Layout("Root")
            .SplitRows(
                new Layout("StatusBar").Size(1),
                new Layout("Main"),
                new Layout("ActionBar").Size(1));

        layout["StatusBar"].Update(_statusBar.Render(state, false));
        layout["ActionBar"].Update(_actionBar.Render(state, false));

        var content = RenderPublishContent(state);

        var panel = new Panel(content)
        {
            Header = new PanelHeader(" Publish "),
            Border = BoxBorder.Rounded,
            Padding = new Padding(2, 1)
        };

        layout["Main"].Update(panel);

        return layout;
    }

    private IRenderable RenderPublishContent(TuiState state)
    {
        var publish = state.Publish;
        var rows = new List<IRenderable>();

        if (publish.IsPublishing)
        {
            rows.Add(new Rule("[yellow]Uploading[/]"));
            rows.Add(Text.Empty);
            rows.Add(_progressPanel.RenderPublishProgress(state));
        }
        else if (!string.IsNullOrEmpty(publish.ManifestUrl))
        {
            rows.Add(new Rule("[green]Publish Complete[/]"));
            rows.Add(Text.Empty);
            rows.Add(new Markup($"[green]✓[/] Uploaded {publish.UploadedFiles:N0} files"));
            rows.Add(new Markup($"[dim]Total size:[/] {FormatSize(publish.TotalBytes)}"));
            rows.Add(new Markup($"[dim]Duration:[/] {FormatDuration(publish.Elapsed)}"));
            rows.Add(Text.Empty);
            rows.Add(new Markup($"[dim]Manifest:[/]"));
            rows.Add(new Markup($"[aqua]{Markup.Escape(publish.ManifestUrl)}[/]"));

            if (publish.FailedFiles > 0)
            {
                rows.Add(Text.Empty);
                rows.Add(new Markup($"[yellow]⚠[/] {publish.FailedFiles} files failed to upload"));
                rows.Add(new Markup("[dim]Press [/][yellow]r[/][dim] to retry failed uploads[/]"));
            }

            rows.Add(Text.Empty);
            rows.Add(new Markup("[dim]Press [/][yellow]Esc[/][dim] to return to dashboard[/]"));
        }
        else if (!string.IsNullOrEmpty(publish.Error))
        {
            rows.Add(new Rule("[red]Publish Failed[/]"));
            rows.Add(Text.Empty);
            rows.Add(new Markup($"[red]Error:[/] {Markup.Escape(publish.Error)}"));

            if (publish.FailedFiles > 0)
            {
                rows.Add(new Markup($"[dim]Failed files:[/] {publish.FailedFiles}"));
                rows.Add(Text.Empty);
                rows.Add(new Markup("[dim]Press [/][yellow]r[/][dim] to retry failed uploads[/]"));
            }

            rows.Add(Text.Empty);
            rows.Add(new Markup("[dim]Press [/][yellow]Esc[/][dim] to return to dashboard[/]"));
        }
        else if (publish.IsConfirming)
        {
            rows.Add(new Rule("[yellow]Confirm Publish[/]"));
            rows.Add(Text.Empty);

            rows.Add(new Markup($"Ready to publish version [aqua]{Markup.Escape(state.Workspace.CurrentVersion ?? "...")}[/]"));
            rows.Add(new Markup($"to channel [aqua]{Markup.Escape(state.Workspace.CurrentChannel)}[/]"));
            rows.Add(Text.Empty);

            // Summary table
            var summary = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Item")
                .AddColumn("Value");

            summary.AddRow("Files", $"{publish.TotalFiles:N0}");
            summary.AddRow("Total Size", FormatSize(publish.TotalBytes));
            summary.AddRow("Channel", state.Workspace.CurrentChannel);
            summary.AddRow("Version", state.Workspace.CurrentVersion ?? "N/A");

            rows.Add(summary);
            rows.Add(Text.Empty);

            if (state.Workspace.HasPublishProfiles)
            {
                rows.Add(new Markup("[green]✓[/] Publish profile configured"));
            }
            else
            {
                rows.Add(new Markup("[yellow]⚠[/] No publish profile configured"));
                rows.Add(new Markup("[dim]Run [/]patchsync cdn setup[dim] to configure[/]"));
            }

            rows.Add(Text.Empty);
            rows.Add(new Markup("[dim]Press [/][yellow]Enter[/][dim] to confirm or [/][yellow]Esc[/][dim] to cancel[/]"));
        }
        else
        {
            rows.Add(new Markup("[dim]No publish in progress[/]"));
            rows.Add(Text.Empty);
            rows.Add(new Markup("[dim]Press [/][yellow]p[/][dim] from the dashboard to start publishing[/]"));
        }

        return new Rows(rows);
    }

    private static string FormatSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes:N0} bytes",
            < 1024 * 1024 => $"{bytes / 1024.0:F2} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F2} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
        };
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
