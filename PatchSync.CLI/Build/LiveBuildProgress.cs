using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace PatchSync.CLI.Build;

/// <summary>
/// State for a single worker slot.
/// </summary>
internal sealed class SlotState
{
    public string? FileName { get; set; }
    public double Progress { get; set; }
    public bool IsActive => FileName != null;
}

/// <summary>
/// Live table display for parallel build progress.
/// Thread-safe updates from multiple workers.
/// </summary>
public sealed class LiveBuildProgress
{
    private readonly LiveDisplayContext _ctx;
    private readonly int _slotCount;
    private readonly ConcurrentDictionary<int, SlotState> _slots;
    private readonly Stopwatch _stopwatch;
    private readonly object _renderLock = new();

    private int _filesComplete;
    private int _filesTotal;
    private long _bytesProcessed;
    private long _bytesTotal;
    private int _signaturesGenerated;

    public LiveBuildProgress(LiveDisplayContext ctx, int slotCount)
    {
        _ctx = ctx;
        _slotCount = slotCount;
        _slots = new ConcurrentDictionary<int, SlotState>();
        _stopwatch = Stopwatch.StartNew();

        // Initialize slots
        for (int i = 0; i < slotCount; i++)
        {
            _slots[i] = new SlotState();
        }
    }

    /// <summary>
    /// Updates progress from a worker. Thread-safe.
    /// </summary>
    public void Update(ParallelBuildProgress progress)
    {
        // Update slot state
        if (progress.SlotIndex >= 0 && progress.SlotIndex < _slotCount)
        {
            var slot = _slots.GetOrAdd(progress.SlotIndex, _ => new SlotState());
            slot.FileName = progress.FileName;
            slot.Progress = progress.FileProgress;
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

        table.AddColumn(new TableColumn("[grey]Slot[/]").Width(6).Centered());
        table.AddColumn(new TableColumn("[grey]File[/]"));
        table.AddColumn(new TableColumn("[grey]Progress[/]").Width(30));

        // Add slot rows
        for (int i = 0; i < _slotCount; i++)
        {
            var slot = _slots.GetValueOrDefault(i) ?? new SlotState();
            AddSlotRow(table, i + 1, slot);
        }

        // Add separator and summary
        table.AddEmptyRow();

        var overallPct = _filesTotal > 0 ? (double)_filesComplete / _filesTotal : 0;
        var overallBar = BuildProgressBar(overallPct, 25);
        var elapsed = _stopwatch.Elapsed;

        table.AddRow(
            new Markup("[yellow]Overall[/]"),
            new Markup($"[white]{_filesComplete}[/][grey]/[/][white]{_filesTotal}[/] [grey]files[/]"),
            new Markup($"{overallBar} [white]{overallPct:P0}[/]"));

        // Stats row
        var sigSize = FormatBytes(_bytesProcessed);
        var elapsedStr = elapsed.TotalSeconds < 60
            ? $"{elapsed.TotalSeconds:F1}s"
            : $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";

        var rate = elapsed.TotalSeconds > 0 ? _bytesProcessed / elapsed.TotalSeconds : 0;
        var rateStr = FormatBytes((long)rate) + "/s";

        table.AddRow(
            new Markup("[grey]Stats[/]"),
            new Markup($"[cyan]{_signaturesGenerated}[/] [grey]signatures[/]  [grey]•[/]  [cyan]{sigSize}[/] [grey]processed[/]"),
            new Markup($"[grey]{elapsedStr}[/]  [grey]•[/]  [cyan]{rateStr}[/]"));

        return table;
    }

    private static void AddSlotRow(Table table, int slotNum, SlotState slot)
    {
        var slotLabel = $"[cyan][[{slotNum}]][/]";

        if (slot.IsActive)
        {
            var fileName = slot.FileName!;
            // Truncate long filenames
            if (fileName.Length > 45)
            {
                fileName = "..." + fileName[^42..];
            }

            var progressBar = BuildProgressBar(slot.Progress, 25);
            table.AddRow(
                new Markup(slotLabel),
                new Markup($"[white]{Markup.Escape(fileName)}[/]"),
                new Markup($"{progressBar} [grey]{slot.Progress:P0}[/]"));
        }
        else
        {
            table.AddRow(
                new Markup(slotLabel),
                new Markup("[grey](idle)[/]"),
                new Markup("[grey]" + new string('░', 25) + "[/]"));
        }
    }

    private static string BuildProgressBar(double percentage, int width)
    {
        var filled = (int)(percentage * width);
        var empty = width - filled;

        var sb = new StringBuilder();
        sb.Append("[green]");
        sb.Append('█', filled);
        sb.Append("[/][grey]");
        sb.Append('░', empty);
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
