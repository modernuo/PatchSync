using Spectre.Console;
using Spectre.Console.Rendering;

namespace PatchSync.CLI.TUI.Core;

/// <summary>
/// Renders TUI screens using Spectre.Console Layout composition.
/// Produces IRenderable objects for Live display updates.
/// </summary>
public sealed class TuiRenderer
{
    private readonly Style _selectedStyle = new(Color.Black, Color.Aqua);
    private readonly Style _headerStyle = new(Color.White, Color.Blue);
    private readonly Style _dimStyle = new(Color.Grey);
    private readonly Style _errorStyle = new(Color.Red);
    private readonly Style _successStyle = new(Color.Green);
    private readonly Style _warningStyle = new(Color.Yellow);

    /// <summary>
    /// Render the complete TUI based on current state.
    /// </summary>
    public IRenderable Render(TuiState state)
    {
        if (state.Terminal.IsTooSmall)
        {
            return RenderTerminalTooSmall(state.Terminal);
        }

        return state.CurrentScreen switch
        {
            TuiScreen.Dashboard => RenderDashboard(state),
            TuiScreen.Versions => RenderVersions(state),
            TuiScreen.Build => RenderBuild(state),
            TuiScreen.Settings => RenderSettings(state),
            TuiScreen.Publish => RenderPublish(state),
            _ => RenderDashboard(state)
        };
    }

    private IRenderable RenderTerminalTooSmall(TerminalSize size)
    {
        return new Panel(
            new Markup($"[red]Terminal too small[/]\n\nCurrent: {size.Width}x{size.Height}\nMinimum: 60x15"))
        {
            Border = BoxBorder.Rounded,
            Padding = new Padding(2, 1)
        };
    }

    #region Dashboard Screen

    private IRenderable RenderDashboard(TuiState state)
    {
        var layout = new Layout("Root")
            .SplitRows(
                new Layout("StatusBar").Size(1),
                new Layout("Main"),
                new Layout("ActionBar").Size(1));

        layout["StatusBar"].Update(RenderStatusBar(state));
        layout["ActionBar"].Update(RenderActionBar(state));

        // Main area splits into file list and details
        var mainLayout = new Layout("MainContent")
            .SplitColumns(
                new Layout("FileList").Ratio(2),
                new Layout("Details").Ratio(1));

        mainLayout["FileList"].Update(RenderFileListPanel(state));
        mainLayout["Details"].Update(RenderDetailsPanel(state));

        layout["Main"].Update(mainLayout);

        // Overlay dialog if present
        if (state.Dialog != null)
        {
            return RenderWithDialogOverlay(layout, state.Dialog);
        }

        // Overlay input if present
        if (state.Input != null)
        {
            return RenderWithInputOverlay(layout, state.Input);
        }

        return layout;
    }

    private IRenderable RenderFileListPanel(TuiState state)
    {
        var fileList = state.FileList;
        var isActive = state.ActivePanel == TuiPanel.FileList;

        var table = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn(new TableColumn("").NoWrap())
            .AddColumn(new TableColumn("File").Width(40))
            .AddColumn(new TableColumn("Size").RightAligned())
            .AddColumn(new TableColumn("Strategy"));

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
            var pathMarkup = TruncatePath(file.Path, 38);
            var sizeMarkup = FormatSize(file.Size);
            var strategyMarkup = FormatStrategy(file.Strategy, file.IsOverride);

            if (isSelected && isActive)
            {
                table.AddRow(
                    new Markup(marker),
                    new Text(pathMarkup, _selectedStyle),
                    new Text(sizeMarkup, _selectedStyle),
                    new Text(strategyMarkup, _selectedStyle));
            }
            else
            {
                table.AddRow(
                    new Markup(marker),
                    new Text(pathMarkup),
                    new Text(sizeMarkup, _dimStyle),
                    new Markup(strategyMarkup));
            }
        }

        var title = string.IsNullOrEmpty(fileList.Filter)
            ? $"Files ({fileList.FilteredFiles.Count})"
            : $"Files ({fileList.FilteredFiles.Count}/{fileList.AllFiles.Count}) [dim]filter: {fileList.Filter}[/]";

        var panel = new Panel(table)
        {
            Header = new PanelHeader($" {title} "),
            Border = isActive ? BoxBorder.Double : BoxBorder.Rounded,
            BorderStyle = isActive ? new Style(Color.Aqua) : Style.Plain,
            Padding = new Padding(1, 0)
        };

        return panel;
    }

    private IRenderable RenderDetailsPanel(TuiState state)
    {
        var file = state.FileList.SelectedFile;
        var isActive = state.ActivePanel == TuiPanel.Details;

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

        var panel = new Panel(content)
        {
            Header = new PanelHeader(" Details "),
            Border = isActive ? BoxBorder.Double : BoxBorder.Rounded,
            BorderStyle = isActive ? new Style(Color.Aqua) : Style.Plain,
            Padding = new Padding(1, 0)
        };

        return panel;
    }

    #endregion

    #region Versions Screen

    private IRenderable RenderVersions(TuiState state)
    {
        var layout = new Layout("Root")
            .SplitRows(
                new Layout("StatusBar").Size(1),
                new Layout("Main"),
                new Layout("ActionBar").Size(1));

        layout["StatusBar"].Update(RenderStatusBar(state));
        layout["ActionBar"].Update(RenderActionBar(state));

        // Main area splits into channels and versions
        var mainLayout = new Layout("MainContent")
            .SplitColumns(
                new Layout("Channels").Ratio(1),
                new Layout("Versions").Ratio(2));

        mainLayout["Channels"].Update(RenderChannelsPanel(state));
        mainLayout["Versions"].Update(RenderVersionsPanel(state));

        layout["Main"].Update(mainLayout);

        if (state.Dialog != null)
        {
            return RenderWithDialogOverlay(layout, state.Dialog);
        }

        return layout;
    }

    private IRenderable RenderChannelsPanel(TuiState state)
    {
        var isActive = state.ActivePanel == TuiPanel.Channels;
        var channels = state.Workspace.Channels;

        var table = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn(new TableColumn("Channel"));

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

        return new Panel(table)
        {
            Header = new PanelHeader(" Channels "),
            Border = isActive ? BoxBorder.Double : BoxBorder.Rounded,
            BorderStyle = isActive ? new Style(Color.Aqua) : Style.Plain,
            Padding = new Padding(1, 0)
        };
    }

    private IRenderable RenderVersionsPanel(TuiState state)
    {
        var isActive = state.ActivePanel == TuiPanel.Versions;
        var versions = state.Workspace.Versions;

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

        return new Panel(table)
        {
            Header = new PanelHeader($" Versions ({state.Workspace.CurrentChannel}) "),
            Border = isActive ? BoxBorder.Double : BoxBorder.Rounded,
            BorderStyle = isActive ? new Style(Color.Aqua) : Style.Plain,
            Padding = new Padding(1, 0)
        };
    }

    #endregion

    #region Build Screen

    private IRenderable RenderBuild(TuiState state)
    {
        var build = state.Build;

        var layout = new Layout("Root")
            .SplitRows(
                new Layout("StatusBar").Size(1),
                new Layout("Main"),
                new Layout("ActionBar").Size(1));

        layout["StatusBar"].Update(RenderStatusBar(state));
        layout["ActionBar"].Update(RenderActionBar(state));

        var content = new List<IRenderable>();

        if (build.IsBuilding)
        {
            content.Add(new Rule($"[yellow]Building {state.Workspace.CurrentVersion}[/]"));
            content.Add(Text.Empty);

            // Progress bar
            var progressBar = new ProgressBarColumn();
            var percentage = build.Percentage;
            content.Add(new Markup($"[dim]Progress:[/] {percentage:F1}%"));

            var barWidth = Math.Max(20, state.Terminal.Width - 20);
            var filled = (int)(barWidth * percentage / 100);
            var bar = new string('█', filled) + new string('░', barWidth - filled);
            content.Add(new Markup($"[aqua]{bar}[/]"));

            content.Add(Text.Empty);
            content.Add(new Markup($"[dim]Files:[/] {build.ProcessedFiles} / {build.TotalFiles}"));
            content.Add(new Markup($"[dim]Current:[/] {Markup.Escape(build.CurrentFile ?? "...")}"));
            content.Add(new Markup($"[dim]Elapsed:[/] {build.Elapsed:mm\\:ss}"));

            if (build.BaseVersion != null)
            {
                content.Add(new Markup($"[dim]Base Version:[/] {Markup.Escape(build.BaseVersion)}"));
            }
        }
        else if (build.IsCompleted)
        {
            content.Add(new Rule("[green]Build Complete[/]"));
            content.Add(Text.Empty);
            content.Add(new Markup($"[green]✓[/] Built {build.TotalFiles} files"));
            content.Add(new Markup($"[dim]Duration:[/] {build.Elapsed:mm\\:ss}"));

            if (build.ComparisonResult != null)
            {
                content.Add(Text.Empty);
                content.Add(new Markup("[dim]Changes from base:[/]"));
                content.Add(new Markup($"  [green]+{build.ComparisonResult.NewFiles.Count}[/] new"));
                content.Add(new Markup($"  [yellow]~{build.ComparisonResult.ModifiedFiles.Count}[/] modified"));
                content.Add(new Markup($"  [red]-{build.ComparisonResult.DeletedFiles.Count}[/] deleted"));
            }
        }
        else if (build.Error != null)
        {
            content.Add(new Rule("[red]Build Failed[/]"));
            content.Add(Text.Empty);
            content.Add(new Markup($"[red]Error:[/] {Markup.Escape(build.Error)}"));
        }
        else
        {
            content.Add(new Markup("[dim]No build in progress[/]"));
        }

        var panel = new Panel(new Rows(content))
        {
            Header = new PanelHeader(" Build "),
            Border = BoxBorder.Rounded,
            Padding = new Padding(2, 1)
        };

        layout["Main"].Update(panel);
        return layout;
    }

    #endregion

    #region Publish Screen

    private IRenderable RenderPublish(TuiState state)
    {
        var publish = state.Publish;

        var layout = new Layout("Root")
            .SplitRows(
                new Layout("StatusBar").Size(1),
                new Layout("Main"),
                new Layout("ActionBar").Size(1));

        layout["StatusBar"].Update(RenderStatusBar(state));
        layout["ActionBar"].Update(RenderActionBar(state));

        var content = new List<IRenderable>();

        if (publish.IsPublishing)
        {
            content.Add(new Rule("[yellow]Publishing[/]"));
            content.Add(Text.Empty);

            content.Add(new Markup($"[dim]Progress:[/] {publish.Percentage:F1}%"));

            var barWidth = Math.Max(20, state.Terminal.Width - 20);
            var filled = (int)(barWidth * publish.Percentage / 100);
            var bar = new string('█', filled) + new string('░', barWidth - filled);
            content.Add(new Markup($"[aqua]{bar}[/]"));

            content.Add(Text.Empty);
            content.Add(new Markup($"[dim]Files:[/] {publish.UploadedFiles} / {publish.TotalFiles}"));
            content.Add(new Markup($"[dim]Uploaded:[/] {FormatSizeFull(publish.UploadedBytes)} / {FormatSizeFull(publish.TotalBytes)}"));
            content.Add(new Markup($"[dim]Speed:[/] {FormatSizeFull(publish.BytesPerSecond)}/s"));
            content.Add(new Markup($"[dim]Current:[/] {Markup.Escape(publish.CurrentFile ?? "...")}"));
            content.Add(new Markup($"[dim]Elapsed:[/] {publish.Elapsed:mm\\:ss}"));
        }
        else if (publish.ManifestUrl != null)
        {
            content.Add(new Rule("[green]Publish Complete[/]"));
            content.Add(Text.Empty);
            content.Add(new Markup($"[green]✓[/] Uploaded {publish.UploadedFiles} files"));
            content.Add(new Markup($"[dim]Total size:[/] {FormatSizeFull(publish.TotalBytes)}"));
            content.Add(new Markup($"[dim]Duration:[/] {publish.Elapsed:mm\\:ss}"));
            content.Add(Text.Empty);
            content.Add(new Markup($"[dim]Manifest:[/] {Markup.Escape(publish.ManifestUrl)}"));

            if (publish.FailedFiles > 0)
            {
                content.Add(Text.Empty);
                content.Add(new Markup($"[yellow]⚠[/] {publish.FailedFiles} files failed to upload"));
            }
        }
        else if (publish.Error != null)
        {
            content.Add(new Rule("[red]Publish Failed[/]"));
            content.Add(Text.Empty);
            content.Add(new Markup($"[red]Error:[/] {Markup.Escape(publish.Error)}"));

            if (publish.FailedFiles > 0)
            {
                content.Add(new Markup($"[dim]Failed files:[/] {publish.FailedFiles}"));
            }
        }
        else if (publish.IsConfirming)
        {
            content.Add(new Rule("[yellow]Confirm Publish[/]"));
            content.Add(Text.Empty);
            content.Add(new Markup($"Ready to publish [aqua]{state.Workspace.CurrentVersion}[/] to [aqua]{state.Workspace.CurrentChannel}[/]"));
            content.Add(Text.Empty);
            content.Add(new Markup($"[dim]Files:[/] {publish.TotalFiles}"));
            content.Add(new Markup($"[dim]Total size:[/] {FormatSizeFull(publish.TotalBytes)}"));
            content.Add(Text.Empty);
            content.Add(new Markup("[dim]Press [/][yellow]Enter[/][dim] to confirm or [/][yellow]Esc[/][dim] to cancel[/]"));
        }
        else
        {
            content.Add(new Markup("[dim]No publish in progress[/]"));
        }

        var panel = new Panel(new Rows(content))
        {
            Header = new PanelHeader(" Publish "),
            Border = BoxBorder.Rounded,
            Padding = new Padding(2, 1)
        };

        layout["Main"].Update(panel);
        return layout;
    }

    #endregion

    #region Settings Screen

    private IRenderable RenderSettings(TuiState state)
    {
        var layout = new Layout("Root")
            .SplitRows(
                new Layout("StatusBar").Size(1),
                new Layout("Main"),
                new Layout("ActionBar").Size(1));

        layout["StatusBar"].Update(RenderStatusBar(state));
        layout["ActionBar"].Update(RenderActionBar(state));

        var content = new List<IRenderable>
        {
            new Rule("Workspace"),
            new Markup($"[dim]Path:[/] {Markup.Escape(state.Workspace.Path)}"),
            new Markup($"[dim]Project:[/] {Markup.Escape(state.Workspace.ProjectName)}"),
            new Markup($"[dim]ID:[/] {Markup.Escape(state.Workspace.ProjectId)}"),
            Text.Empty,
            new Rule("CDN Configuration"),
            new Markup(state.Workspace.HasPublishProfiles
                ? "[green]✓[/] Publish profiles configured"
                : "[yellow]⚠[/] No publish profiles configured"),
            Text.Empty,
            new Markup("[dim]Press [/][yellow]c[/][dim] to configure CDN settings[/]")
        };

        var panel = new Panel(new Rows(content))
        {
            Header = new PanelHeader(" Settings "),
            Border = BoxBorder.Rounded,
            Padding = new Padding(2, 1)
        };

        layout["Main"].Update(panel);
        return layout;
    }

    #endregion

    #region Status and Action Bars

    private IRenderable RenderStatusBar(TuiState state)
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
        var screens = new[] { "1:Dashboard", "2:Versions", "3:Settings" };
        var screenIndex = state.CurrentScreen switch
        {
            TuiScreen.Dashboard => 0,
            TuiScreen.Versions => 1,
            TuiScreen.Settings => 2,
            _ => -1
        };

        var tabs = string.Join(" ", screens.Select((s, i) =>
            i == screenIndex ? $"[aqua underline]{s}[/]" : $"[dim]{s}[/]"));

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

    private IRenderable RenderActionBar(TuiState state)
    {
        var actionBar = KeyDispatcher.GetActionBar(state);
        return new Markup($"[dim]{Markup.Escape(actionBar)}[/]");
    }

    #endregion

    #region Dialog and Input Overlays

    private IRenderable RenderWithDialogOverlay(IRenderable background, DialogState dialog)
    {
        var dialogStyle = dialog.Type switch
        {
            DialogType.Error => new Style(Color.Red),
            DialogType.Warning => new Style(Color.Yellow),
            DialogType.Confirm => new Style(Color.Aqua),
            _ => Style.Plain
        };

        var content = new List<IRenderable>
        {
            new Markup(Markup.Escape(dialog.Message))
        };

        if (dialog.Type == DialogType.Confirm)
        {
            content.Add(Text.Empty);
            content.Add(new Markup("[dim]Press [/][yellow]y[/][dim] to confirm or [/][yellow]n[/][dim] to cancel[/]"));
        }
        else if (dialog.Choices != null && dialog.Choices.Count > 0)
        {
            content.Add(Text.Empty);
            for (int i = 0; i < dialog.Choices.Count; i++)
            {
                var choice = dialog.Choices[i];
                var isSelected = i == dialog.SelectedChoice;
                content.Add(new Markup(isSelected
                    ? $"[aqua]> {Markup.Escape(choice)}[/]"
                    : $"  {Markup.Escape(choice)}"));
            }
        }

        var dialogPanel = new Panel(new Rows(content))
        {
            Header = new PanelHeader($" {dialog.Title} "),
            Border = BoxBorder.Double,
            BorderStyle = dialogStyle,
            Padding = new Padding(2, 1)
        };

        // Center the dialog over the background
        return new Layout("Overlay")
            .SplitRows(
                new Layout("Top").Ratio(1).Update(background),
                new Layout("Dialog").Size(10).Update(
                    new Padder(dialogPanel, new Padding(10, 0))));
    }

    private IRenderable RenderWithInputOverlay(IRenderable background, InputState input)
    {
        var inputPanel = new Panel(
            new Markup($"{Markup.Escape(input.Text)}[blink]_[/]"))
        {
            Header = new PanelHeader($" {input.Prompt} "),
            Border = BoxBorder.Double,
            BorderStyle = new Style(Color.Aqua),
            Padding = new Padding(1, 0)
        };

        return new Layout("Overlay")
            .SplitRows(
                new Layout("Top").Ratio(1).Update(background),
                new Layout("Input").Size(3).Update(
                    new Padder(inputPanel, new Padding(10, 0))));
    }

    #endregion

    #region Formatting Helpers

    private static string TruncatePath(string path, int maxLength)
    {
        if (path.Length <= maxLength) return path;
        return "..." + path[^(maxLength - 3)..];
    }

    private static string TruncateHash(string hash)
    {
        if (string.IsNullOrEmpty(hash)) return "[dim]none[/]";
        return hash.Length > 16 ? hash[..16] + "..." : hash;
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

    #endregion
}
