using PatchSync.CLI.TUI.Core;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace PatchSync.CLI.TUI.Panels;

/// <summary>
/// Channel selection panel for the Versions screen.
/// </summary>
public sealed class ChannelsPanel : IPanel
{
    private readonly Style _selectedStyle = new(Color.Black, Color.Aqua);

    public IRenderable Render(TuiState state, bool isActive)
    {
        var channels = state.Workspace.Channels;

        var table = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn(new TableColumn("Channel"));

        if (channels.Count == 0)
        {
            table.AddRow(new Markup("[dim]No channels[/]"));
        }
        else
        {
            foreach (var channel in channels)
            {
                var isSelected = channel == state.Workspace.CurrentChannel;

                if (isSelected && isActive)
                {
                    table.AddRow(new Text($"> {channel}", _selectedStyle));
                }
                else if (isSelected)
                {
                    table.AddRow(new Markup($"[aqua]> {Markup.Escape(channel)}[/]"));
                }
                else
                {
                    table.AddRow(new Markup($"  {Markup.Escape(channel)}"));
                }
            }
        }

        return new Panel(table)
        {
            Header = new PanelHeader(" Channels "),
            Border = isActive ? BoxBorder.Double : BoxBorder.Rounded,
            BorderStyle = isActive ? new Style(Color.Aqua) : Style.Plain,
            Padding = new Padding(1, 0)
        };
    }
}
