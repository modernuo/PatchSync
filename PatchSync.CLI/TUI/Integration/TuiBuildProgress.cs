using PatchSync.CLI.TUI.Core;

namespace PatchSync.CLI.TUI.Integration;

/// <summary>
/// Adapter that bridges build progress reporting to the TUI message system.
/// Can be used with IProgress&lt;ParallelBuildProgress&gt; or direct method calls.
/// </summary>
public sealed class TuiBuildProgress : IProgress<Build.ParallelBuildProgress>
{
    private readonly TuiApplication _app;
    private readonly object _lock = new();
    private int _processedFiles;
    private int _totalFiles;
    private long _processedBytes;
    private long _totalBytes;
    private string? _currentFile;
    private DateTime _lastUpdate = DateTime.MinValue;
    private readonly TimeSpan _updateInterval = TimeSpan.FromMilliseconds(50);

    public TuiBuildProgress(TuiApplication app)
    {
        _app = app;
    }

    /// <summary>
    /// Set the total counts before starting the build.
    /// </summary>
    public void Initialize(int totalFiles, long totalBytes)
    {
        lock (_lock)
        {
            _totalFiles = totalFiles;
            _totalBytes = totalBytes;
            _processedFiles = 0;
            _processedBytes = 0;
            _currentFile = null;
        }

        SendUpdate();
    }

    /// <summary>
    /// Report progress on a file being processed.
    /// </summary>
    public void ReportFileStarted(string relativePath)
    {
        lock (_lock)
        {
            _currentFile = relativePath;
        }

        ThrottledUpdate();
    }

    /// <summary>
    /// Report that a file has been completed.
    /// </summary>
    public void ReportFileCompleted(string relativePath, long fileSize)
    {
        lock (_lock)
        {
            _processedFiles++;
            _processedBytes += fileSize;
            _currentFile = null;
        }

        SendUpdate();
    }

    /// <summary>
    /// Report that a file has failed.
    /// </summary>
    public void ReportFileFailed(string relativePath, string error)
    {
        lock (_lock)
        {
            _processedFiles++;
            _currentFile = null;
        }

        // Show error as status message
        _app.PostMessage(new ShowStatus($"Failed: {relativePath} - {error}", StatusType.Error));
        SendUpdate();
    }

    /// <summary>
    /// IProgress implementation for ParallelBuildProgress.
    /// </summary>
    void IProgress<Build.ParallelBuildProgress>.Report(Build.ParallelBuildProgress value)
    {
        lock (_lock)
        {
            _processedFiles = value.FilesComplete;
            _totalFiles = value.FilesTotal;
            _processedBytes = value.BytesProcessed;
            _totalBytes = value.BytesTotal;
            _currentFile = value.FileName;
        }

        ThrottledUpdate();
    }

    /// <summary>
    /// Report build completion.
    /// </summary>
    public void ReportCompleted(TimeSpan duration, int fileCount, long totalSize)
    {
        _app.PostMessage(new BuildCompleted(duration, fileCount, totalSize));
    }

    /// <summary>
    /// Report build failure.
    /// </summary>
    public void ReportFailed(string error)
    {
        _app.PostMessage(new BuildFailed(error));
    }

    private void SendUpdate()
    {
        int processed, total;
        string? currentFile;

        lock (_lock)
        {
            processed = _processedFiles;
            total = _totalFiles;
            currentFile = _currentFile;
            _lastUpdate = DateTime.UtcNow;
        }

        var percentage = total > 0 ? (double)processed / total * 100 : 0;

        _app.PostMessage(new BuildProgress(processed, total, currentFile ?? "", percentage));
    }

    private void ThrottledUpdate()
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _lastUpdate < _updateInterval)
            {
                return;
            }
        }

        SendUpdate();
    }
}

/// <summary>
/// Adapter for publish progress reporting to the TUI.
/// </summary>
public sealed class TuiPublishProgress
{
    private readonly TuiApplication _app;
    private readonly object _lock = new();
    private int _uploadedFiles;
    private int _totalFiles;
    private long _uploadedBytes;
    private long _totalBytes;
    private string? _currentFile;
    private DateTime _lastUpdate = DateTime.MinValue;
    private readonly TimeSpan _updateInterval = TimeSpan.FromMilliseconds(100);

    public TuiPublishProgress(TuiApplication app)
    {
        _app = app;
    }

    /// <summary>
    /// Initialize with total counts.
    /// </summary>
    public void Initialize(int totalFiles, long totalBytes)
    {
        lock (_lock)
        {
            _totalFiles = totalFiles;
            _totalBytes = totalBytes;
            _uploadedFiles = 0;
            _uploadedBytes = 0;
            _currentFile = null;
        }

        SendUpdate();
    }

    /// <summary>
    /// Report file upload started.
    /// </summary>
    public void ReportFileStarted(string relativePath)
    {
        lock (_lock)
        {
            _currentFile = relativePath;
        }

        ThrottledUpdate();
    }

    /// <summary>
    /// Report bytes uploaded for current file.
    /// </summary>
    public void ReportBytesUploaded(long bytes)
    {
        lock (_lock)
        {
            _uploadedBytes += bytes;
        }

        ThrottledUpdate();
    }

    /// <summary>
    /// Report file upload completed.
    /// </summary>
    public void ReportFileCompleted(string relativePath, long fileSize)
    {
        lock (_lock)
        {
            _uploadedFiles++;
            _currentFile = null;
        }

        SendUpdate();
    }

    /// <summary>
    /// Report file upload failed.
    /// </summary>
    public void ReportFileFailed(string relativePath, string error)
    {
        lock (_lock)
        {
            _currentFile = null;
        }

        _app.PostMessage(new ShowStatus($"Upload failed: {relativePath}", StatusType.Error));
    }

    /// <summary>
    /// Report publish completion.
    /// </summary>
    public void ReportCompleted(string manifestUrl)
    {
        _app.PostMessage(new PublishCompleted(manifestUrl));
    }

    /// <summary>
    /// Report publish failure.
    /// </summary>
    public void ReportFailed(string error, int failedFiles)
    {
        _app.PostMessage(new PublishFailed(error, failedFiles));
    }

    private void SendUpdate()
    {
        int uploaded, total;
        long uploadedBytes, totalBytes;
        string? currentFile;

        lock (_lock)
        {
            uploaded = _uploadedFiles;
            total = _totalFiles;
            uploadedBytes = _uploadedBytes;
            totalBytes = _totalBytes;
            currentFile = _currentFile;
            _lastUpdate = DateTime.UtcNow;
        }

        _app.PostMessage(new PublishProgress(uploaded, total, uploadedBytes, totalBytes, currentFile ?? ""));
    }

    private void ThrottledUpdate()
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _lastUpdate < _updateInterval)
            {
                return;
            }
        }

        SendUpdate();
    }
}
