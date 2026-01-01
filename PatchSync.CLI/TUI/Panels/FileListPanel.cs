using PatchSync.CLI.TUI.Core;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace PatchSync.CLI.TUI.Panels;

/// <summary>
/// Scrollable file list panel with filter support and multi-select.
/// </summary>
public sealed class FileListPanel : IPanel
{
    private readonly Style _selectedStyle = new(Color.Black, Color.Aqua);
    private readonly Style _dimStyle = new(Color.Grey);

    public IRenderable Render(TuiState state, bool isActive)
    {
        var fileList = state.FileList;

        var table = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn(new TableColumn("").NoWrap().Width(2))
            .AddColumn(new TableColumn("File"))
            .AddColumn(new TableColumn("Size").RightAligned().Width(10))
            .AddColumn(new TableColumn("Strategy").Width(12));

        var visibleFiles = fileList.FilteredFiles
            .Skip(fileList.ScrollOffset)
            .Take(fileList.VisibleCount)
            .ToList();

        for (int i = 0; i < visibleFiles.Count; i++)
        {
            var file = visibleFiles[i];
            var actualIndex = fileList.ScrollOffset + i;
            var isSelected = actualIndex == fileList.SelectedIndex;
            var isMultiSelected = fileList.SelectedPaths.Contains(file.Path);

            var marker = isMultiSelected ? "[green]●[/]" : (isSelected ? ">" : " ");
            var pathText = TruncatePath(file.Path, 38);
            var sizeText = FormatSize(file.Size);
            var strategyText = FormatStrategy(file.Strategy, file.IsOverride);

            if (isSelected && isActive)
            {
                table.AddRow(
                    new Markup(marker),
                    new Text(pathText, _selectedStyle),
                    new Text(sizeText, _selectedStyle),
                    new Text(GetStrategyPlainText(file.Strategy, file.IsOverride), _selectedStyle));
            }
            else
            {
                table.AddRow(
                    new Markup(marker),
                    new Text(pathText),
                    new Text(sizeText, _dimStyle),
                    new Markup(strategyText));
            }
        }

        // Show empty state
        if (visibleFiles.Count == 0)
        {
            table.AddRow(new Markup("[dim]No files[/]"), Text.Empty, Text.Empty, Text.Empty);
        }

        var title = BuildTitle(fileList);

        var panel = new Panel(table)
        {
            Header = new PanelHeader($" {title} "),
            Border = isActive ? BoxBorder.Double : BoxBorder.Rounded,
            BorderStyle = isActive ? new Style(Color.Aqua) : Style.Plain,
            Padding = new Padding(1, 0)
        };

        return panel;
    }

    private static string BuildTitle(FileListState fileList)
    {
        var count = fileList.FilteredFiles.Count;
        var total = fileList.AllFiles.Count;

        if (string.IsNullOrEmpty(fileList.Filter))
        {
            return $"Files ({count})";
        }

        return $"Files ({count}/{total}) [dim]filter: {Markup.Escape(fileList.Filter)}[/]";
    }

    private static string TruncatePath(string path, int maxLength)
    {
        if (path.Length <= maxLength) return path;
        return "..." + path[^(maxLength - 3)..];
    }

    private static string FormatSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
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

    private static string GetStrategyPlainText(Common.Manifest.UpdateStrategy strategy, bool isOverride)
    {
        var name = strategy switch
        {
            Common.Manifest.UpdateStrategy.Delta => "Delta",
            Common.Manifest.UpdateStrategy.AlwaysCompressed => "Compressed",
            Common.Manifest.UpdateStrategy.HashCheck => "HashCheck",
            Common.Manifest.UpdateStrategy.CreateOnly => "CreateOnly",
            Common.Manifest.UpdateStrategy.Delete => "Delete",
            Common.Manifest.UpdateStrategy.VirtualDelta => "VirtualDelta",
            Common.Manifest.UpdateStrategy.UpdateIfNotModified => "IfNotMod",
            _ => strategy.ToString()
        };

        return isOverride ? $"{name}*" : name;
    }
}
