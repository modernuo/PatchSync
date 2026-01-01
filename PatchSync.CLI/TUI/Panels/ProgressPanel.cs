using PatchSync.CLI.TUI.Core;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace PatchSync.CLI.TUI.Panels;

/// <summary>
/// Progress panel for displaying build/upload progress with bar and stats.
/// </summary>
public sealed class ProgressPanel : IPanel
{
    public IRenderable Render(TuiState state, bool isActive)
    {
        // Determine which progress to show
        if (state.Build.IsBuilding)
        {
            return RenderBuildProgress(state);
        }

        if (state.Publish.IsPublishing)
        {
            return RenderPublishProgress(state);
        }

        return new Panel(new Markup("[dim]No operation in progress[/]"))
        {
            Header = new PanelHeader(" Progress "),
            Border = BoxBorder.Rounded,
            Padding = new Padding(1, 0)
        };
    }

    /// <summary>
    /// Render build progress as a standalone component.
    /// </summary>
    public IRenderable RenderBuildProgress(TuiState state)
    {
        var build = state.Build;
        var terminalWidth = state.Terminal.Width;

        var rows = new List<IRenderable>();

        // Progress percentage
        rows.Add(new Markup($"[dim]Progress:[/] {build.Percentage:F1}%"));

        // Progress bar
        var barWidth = Math.Max(20, terminalWidth - 24);
        rows.Add(CreateProgressBar(build.Percentage, barWidth, Color.Aqua));

        rows.Add(Text.Empty);

        // Stats
        rows.Add(new Markup($"[dim]Files:[/] {build.ProcessedFiles:N0} / {build.TotalFiles:N0}"));

        if (!string.IsNullOrEmpty(build.CurrentFile))
        {
            var truncatedFile = TruncatePath(build.CurrentFile, terminalWidth - 16);
            rows.Add(new Markup($"[dim]Current:[/] {Markup.Escape(truncatedFile)}"));
        }

        rows.Add(new Markup($"[dim]Elapsed:[/] {FormatDuration(build.Elapsed)}"));

        if (!string.IsNullOrEmpty(build.BaseVersion))
        {
            rows.Add(new Markup($"[dim]Base:[/] {Markup.Escape(build.BaseVersion)}"));
        }

        return new Panel(new Rows(rows))
        {
            Header = new PanelHeader(" Build Progress "),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Yellow),
            Padding = new Padding(1, 0)
        };
    }

    /// <summary>
    /// Render publish progress as a standalone component.
    /// </summary>
    public IRenderable RenderPublishProgress(TuiState state)
    {
        var publish = state.Publish;
        var terminalWidth = state.Terminal.Width;

        var rows = new List<IRenderable>();

        // Progress percentage
        rows.Add(new Markup($"[dim]Progress:[/] {publish.Percentage:F1}%"));

        // Progress bar
        var barWidth = Math.Max(20, terminalWidth - 24);
        rows.Add(CreateProgressBar(publish.Percentage, barWidth, Color.Green));

        rows.Add(Text.Empty);

        // Stats
        rows.Add(new Markup($"[dim]Files:[/] {publish.UploadedFiles:N0} / {publish.TotalFiles:N0}"));
        rows.Add(new Markup($"[dim]Uploaded:[/] {FormatSize(publish.UploadedBytes)} / {FormatSize(publish.TotalBytes)}"));
        rows.Add(new Markup($"[dim]Speed:[/] {FormatSize(publish.BytesPerSecond)}/s"));

        if (!string.IsNullOrEmpty(publish.CurrentFile))
        {
            var truncatedFile = TruncatePath(publish.CurrentFile, terminalWidth - 16);
            rows.Add(new Markup($"[dim]Current:[/] {Markup.Escape(truncatedFile)}"));
        }

        rows.Add(new Markup($"[dim]Elapsed:[/] {FormatDuration(publish.Elapsed)}"));

        // ETA calculation
        if (publish.Percentage > 0 && publish.Percentage < 100)
        {
            var eta = TimeSpan.FromSeconds(
                publish.Elapsed.TotalSeconds * (100 - publish.Percentage) / publish.Percentage);
            rows.Add(new Markup($"[dim]ETA:[/] {FormatDuration(eta)}"));
        }

        return new Panel(new Rows(rows))
        {
            Header = new PanelHeader(" Upload Progress "),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Green),
            Padding = new Padding(1, 0)
        };
    }

    /// <summary>
    /// Create a text-based progress bar.
    /// </summary>
    public static IRenderable CreateProgressBar(double percentage, int width, Color color)
    {
        var filled = (int)(width * percentage / 100);
        var empty = width - filled;

        var bar = new string('█', filled) + new string('░', empty);
        return new Markup($"[{color}]{bar}[/]");
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
            < 1024 => $"{bytes:N0} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
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
