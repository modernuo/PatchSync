using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace PatchSync.CLI.Build;

/// <summary>
/// Completed file entry for display.
/// </summary>
internal readonly record struct CompletedFile(string Path, long Size, bool HasSignature);

/// <summary>
/// Active file entry with start time for sorting.
/// </summary>
internal sealed class ActiveFile
{
    public required string Path { get; init; }
    public long StartTicks { get; init; } = Stopwatch.GetTimestamp();
}

/// <summary>
/// Live display for parallel build progress.
/// Shows streaming file completions instead of idle slots.
/// Thread-safe updates from multiple workers.
/// Implements IProgress directly for synchronous updates (avoids Progress&lt;T&gt; async posting).
/// </summary>
public sealed class LiveBuildProgress : IProgress<ParallelBuildProgress>
{
    private readonly LiveDisplayContext _ctx;
    private readonly int _maxRecentFiles;
    private readonly ConcurrentQueue<CompletedFile> _recentFiles;
    private readonly ConcurrentDictionary<int, ActiveFile> _activeFiles;
    private readonly Stopwatch _stopwatch;
    private readonly object _renderLock = new();

    private int _filesComplete;
    private int _filesTotal;
    private long _bytesProcessed;
    private long _bytesTotal;
    private int _signaturesGenerated;

    public LiveBuildProgress(LiveDisplayContext ctx, int maxRecentFiles = 12)
    {
        _ctx = ctx;
        _maxRecentFiles = maxRecentFiles;
        _recentFiles = new ConcurrentQueue<CompletedFile>();
        _activeFiles = new ConcurrentDictionary<int, ActiveFile>();
        _stopwatch = Stopwatch.StartNew();
    }

    /// <summary>
    /// IProgress implementation - synchronous update.
    /// </summary>
    void IProgress<ParallelBuildProgress>.Report(ParallelBuildProgress value) => Update(value);

    /// <summary>
    /// Updates progress from a worker. Thread-safe.
    /// </summary>
    public void Update(ParallelBuildProgress progress)
    {
        // Track active files by slot
        if (progress.SlotIndex >= 0)
        {
            if (progress.IsCompletion)
            {
                // File completed - record it and clear slot
                if (progress.FileName != null)
                {
                    RecordCompletion(progress.FileName, progress.FileSize, progress.HasSignature);
                }
                _activeFiles.TryRemove(progress.SlotIndex, out _);
            }
            else if (progress.FileName != null)
            {
                // File started - track with start time
                _activeFiles[progress.SlotIndex] = new ActiveFile { Path = progress.FileName };
            }
        }

        // Update overall counters
        Interlocked.Exchange(ref _filesComplete, progress.FilesComplete);
        Interlocked.Exchange(ref _filesTotal, progress.FilesTotal);
        Interlocked.Exchange(ref _bytesProcessed, progress.BytesProcessed);
        Interlocked.Exchange(ref _bytesTotal, progress.BytesTotal);
        Interlocked.Exchange(ref _signaturesGenerated, progress.SignaturesGenerated);

        // Render (throttled by Spectre's refresh rate)
        Render();
    }

    /// <summary>
    /// Records a completed file for display.
    /// </summary>
    public void RecordCompletion(string relativePath, long size, bool hasSignature)
    {
        _recentFiles.Enqueue(new CompletedFile(relativePath, size, hasSignature));

        // Keep queue bounded
        while (_recentFiles.Count > _maxRecentFiles)
        {
            _recentFiles.TryDequeue(out _);
        }
    }

    private void Render()
    {
        lock (_renderLock)
        {
            var table = BuildTable();
            _ctx.UpdateTarget(table);
        }
    }

    private IRenderable BuildTable()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Blue)
            .Expand();

        table.AddColumn(new TableColumn("[grey]Status[/]").Width(10));
        table.AddColumn(new TableColumn("[grey]File[/]"));
        table.AddColumn(new TableColumn("[grey]Size[/]").Width(12).RightAligned());

        // Summary row at top
        var overallPct = _filesTotal > 0 ? (double)_filesComplete / _filesTotal : 0;
        var progressBar = BuildProgressBar(overallPct, 30);
        var elapsed = _stopwatch.Elapsed;
        var rate = elapsed.TotalSeconds > 0 ? _bytesProcessed / elapsed.TotalSeconds : 0;

        table.AddRow(
            new Markup($"[yellow]Progress[/]"),
            new Markup($"{progressBar} [white]{_filesComplete}[/][grey]/[/][white]{_filesTotal}[/]"),
            new Markup($"[cyan]{FormatBytes((long)rate)}/s[/]"));

        table.AddEmptyRow();

        // Show currently active files (sorted by start time - oldest first at top)
        var activeFiles = _activeFiles.Values
            .OrderBy(f => f.StartTicks)
            .ToList();

        if (activeFiles.Count > 0)
        {
            var activeCount = Math.Min(activeFiles.Count, 6); // Show up to 6 active
            for (int i = 0; i < activeCount; i++)
            {
                var file = activeFiles[i];
                var fileName = TruncatePath(file.Path, 55);
                // Older files (at top) show processing time
                var fileElapsed = Stopwatch.GetElapsedTime(file.StartTicks);
                var timeStr = fileElapsed.TotalSeconds >= 1 ? $"{fileElapsed.TotalSeconds:F1}s" : "";
                table.AddRow(
                    new Markup("[cyan]Working[/]"),
                    new Markup($"[white]{Markup.Escape(fileName)}[/]"),
                    new Markup($"[grey]{timeStr}[/]"));
            }
            if (activeFiles.Count > 6)
            {
                table.AddRow(
                    new Markup("[grey]...[/]"),
                    new Markup($"[grey]+{activeFiles.Count - 6} more[/]"),
                    new Markup(""));
            }
            table.AddEmptyRow();
        }

        // Show recent completions (streaming out)
        var recentList = _recentFiles.ToArray();
        foreach (var file in recentList.Reverse().Take(8)) // Show last 8 completed
        {
            var fileName = TruncatePath(file.Path, 55);
            var sigMarker = file.HasSignature ? "[green]:check_mark:[/]" : "[grey]-[/]";
            table.AddRow(
                new Markup($"[green]Done[/] {sigMarker}"),
                new Markup($"[grey]{Markup.Escape(fileName)}[/]"),
                new Markup($"[grey]{FormatBytes(file.Size)}[/]"));
        }

        // Stats row at bottom
        table.AddEmptyRow();
        var elapsedStr = elapsed.TotalSeconds < 60
            ? $"{elapsed.TotalSeconds:F1}s"
            : $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";

        table.AddRow(
            new Markup("[grey]Stats[/]"),
            new Markup($"[cyan]{_signaturesGenerated}[/] [grey]signatures[/]  [grey]•[/]  [cyan]{FormatBytes(_bytesProcessed)}[/] [grey]processed[/]"),
            new Markup($"[grey]{elapsedStr}[/]"));

        return table;
    }

    private static string TruncatePath(string path, int maxLength)
    {
        if (path.Length <= maxLength)
            return path;
        return "..." + path[^(maxLength - 3)..];
    }

    private static string BuildProgressBar(double percentage, int width)
    {
        // Use ceiling for >= 100% to ensure full bar, round otherwise
        var filled = percentage >= 1.0
            ? width
            : (int)Math.Round(percentage * width);
        var empty = width - filled;

        var sb = new StringBuilder();
        sb.Append("[green]");
        sb.Append('█', Math.Max(0, filled));
        sb.Append("[/][grey]");
        sb.Append('░', Math.Max(0, empty));
        sb.Append("[/]");

        return sb.ToString();
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
        };
    }
}
