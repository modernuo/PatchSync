using PatchSync.CLI.TUI.Core;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace PatchSync.CLI.TUI.Panels;

/// <summary>
/// Details panel showing information about the selected file.
/// </summary>
public sealed class DetailsPanel : IPanel
{
    public IRenderable Render(TuiState state, bool isActive)
    {
        var file = state.FileList.SelectedFile;

        IRenderable content;
        if (file == null)
        {
            content = new Markup("[dim]No file selected[/]");
        }
        else
        {
            var details = new Table()
                .Border(TableBorder.None)
                .HideHeaders()
                .AddColumn(new TableColumn("Label").Width(12))
                .AddColumn(new TableColumn("Value"));

            details.AddRow("[dim]Path[/]", Markup.Escape(file.Path));
            details.AddRow("[dim]Name[/]", Markup.Escape(file.FileName));
            details.AddRow("[dim]Directory[/]", Markup.Escape(file.Directory));
            details.AddRow("[dim]Size[/]", FormatSizeFull(file.Size));
            details.AddRow("[dim]Hash[/]", TruncateHash(file.Hash));
            details.AddRow("[dim]Strategy[/]", FormatStrategy(file.Strategy, file.IsOverride));

            if (!string.IsNullOrEmpty(file.BaseHash))
            {
                details.AddRow("[dim]Base Hash[/]", TruncateHash(file.BaseHash));
            }

            content = details;
        }

        return new Panel(content)
        {
            Header = new PanelHeader(" Details "),
            Border = isActive ? BoxBorder.Double : BoxBorder.Rounded,
            BorderStyle = isActive ? new Style(Color.Aqua) : Style.Plain,
            Padding = new Padding(1, 0)
        };
    }

    private static string TruncateHash(string hash)
    {
        if (string.IsNullOrEmpty(hash)) return "[dim]none[/]";
        return hash.Length > 16 ? hash[..16] + "..." : hash;
    }

    private static string FormatSizeFull(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes:N0} bytes",
            < 1024 * 1024 => $"{bytes / 1024.0:F2} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F2} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
        };
    }

    private static string FormatStrategy(Common.Manifest.UpdateStrategy strategy, bool isOverride)
    {
        var name = strategy switch
        {
            Common.Manifest.UpdateStrategy.Delta => "[cyan]Delta[/]",
            Common.Manifest.UpdateStrategy.AlwaysCompressed => "[blue]Compressed[/]",
            Common.Manifest.UpdateStrategy.HashCheck => "[yellow]HashCheck[/]",
            Common.Manifest.UpdateStrategy.CreateOnly => "[green]CreateOnly[/]",
            Common.Manifest.UpdateStrategy.Delete => "[red]Delete[/]",
            Common.Manifest.UpdateStrategy.VirtualDelta => "[magenta]VirtualDelta[/]",
            Common.Manifest.UpdateStrategy.UpdateIfNotModified => "[aqua]IfNotMod[/]",
            _ => strategy.ToString()
        };

        return isOverride ? $"{name}[dim]*[/]" : name;
    }
}
