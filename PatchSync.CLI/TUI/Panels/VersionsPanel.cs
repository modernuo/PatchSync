using PatchSync.CLI.TUI.Core;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace PatchSync.CLI.TUI.Panels;

/// <summary>
/// Version list panel for the Versions screen.
/// </summary>
public sealed class VersionsPanel : IPanel
{
    private readonly Style _selectedStyle = new(Color.Black, Color.Aqua);

    public IRenderable Render(TuiState state, bool isActive)
    {
        var versions = state.Workspace.Versions;
        var currentChannel = state.Workspace.CurrentChannel;

        var table = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn(new TableColumn("Version"));

        if (versions.Count == 0)
        {
            table.AddRow(new Markup("[dim]No versions[/]"));
        }
        else
        {
            foreach (var version in versions)
            {
                var isSelected = version == state.Workspace.CurrentVersion;

                if (isSelected && isActive)
                {
                    table.AddRow(new Text($"> {version}", _selectedStyle));
                }
                else if (isSelected)
                {
                    table.AddRow(new Markup($"[aqua]> {Markup.Escape(version)}[/]"));
                }
                else
                {
                    table.AddRow(new Markup($"  {Markup.Escape(version)}"));
                }
            }
        }

        var title = string.IsNullOrEmpty(currentChannel)
            ? "Versions"
            : $"Versions ({Markup.Escape(currentChannel)})";

        return new Panel(table)
        {
            Header = new PanelHeader($" {title} "),
            Border = isActive ? BoxBorder.Double : BoxBorder.Rounded,
            BorderStyle = isActive ? new Style(Color.Aqua) : Style.Plain,
            Padding = new Padding(1, 0)
        };
    }

    /// <summary>
    /// Render version details for metadata display.
    /// </summary>
    public static IRenderable RenderVersionDetails(WorkspaceState workspace)
    {
        if (string.IsNullOrEmpty(workspace.CurrentVersion))
        {
            return new Markup("[dim]Select a version to view details[/]");
        }

        var metadata = workspace.CurrentMetadata;
        if (metadata == null)
        {
            return new Markup("[dim]Loading version metadata...[/]");
        }

        var details = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn(new TableColumn("Label").Width(14))
            .AddColumn(new TableColumn("Value"));

        details.AddRow("[dim]Version[/]", Markup.Escape(workspace.CurrentVersion));

        if (metadata.BasedOn != null)
        {
            var basedOn = $"{metadata.BasedOn.Channel}:{metadata.BasedOn.Version}";
            details.AddRow("[dim]Based on[/]", Markup.Escape(basedOn));
        }

        if (metadata.PromotedFrom != null)
        {
            var promotedFrom = $"{metadata.PromotedFrom.SourceChannel}:{metadata.PromotedFrom.SourceVersion}";
            details.AddRow("[dim]Promoted from[/]", Markup.Escape(promotedFrom));
        }

        if (metadata.FileOverrides?.Count > 0)
        {
            details.AddRow("[dim]Overrides[/]", $"{metadata.FileOverrides.Count} files");
        }

        return new Panel(details)
        {
            Header = new PanelHeader(" Version Details "),
            Border = BoxBorder.Rounded,
            Padding = new Padding(1, 0)
        };
    }
}
