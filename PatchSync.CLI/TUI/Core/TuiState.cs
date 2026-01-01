using PatchSync.CLI.Build;
using PatchSync.CLI.Workspace;
using PatchSync.Common.Manifest;

namespace PatchSync.CLI.TUI.Core;

/// <summary>
/// Root application state for the TUI.
/// Immutable record for AOT compatibility and predictable state management.
/// </summary>
public sealed record TuiState
{
    /// <summary>Current active screen.</summary>
    public TuiScreen CurrentScreen { get; init; } = TuiScreen.Dashboard;

    /// <summary>Screen navigation history for back navigation.</summary>
    public IReadOnlyList<TuiScreen> ScreenHistory { get; init; } = [];

    /// <summary>Currently active panel.</summary>
    public TuiPanel ActivePanel { get; init; } = TuiPanel.FileList;

    /// <summary>Workspace information.</summary>
    public WorkspaceState Workspace { get; init; } = new();

    /// <summary>File list state.</summary>
    public FileListState FileList { get; init; } = new();

    /// <summary>Build operation state.</summary>
    public BuildState Build { get; init; } = new();

    /// <summary>Publish operation state.</summary>
    public PublishState Publish { get; init; } = new();

    /// <summary>Dialog/modal state.</summary>
    public DialogState? Dialog { get; init; }

    /// <summary>Input mode state.</summary>
    public InputState? Input { get; init; }

    /// <summary>Current status message.</summary>
    public StatusMessage? Status { get; init; }

    /// <summary>Terminal dimensions.</summary>
    public TerminalSize Terminal { get; init; } = new(80, 24);

    /// <summary>Whether the TUI is running.</summary>
    public bool IsRunning { get; init; } = true;

    /// <summary>Whether a wizard overlay is active.</summary>
    public bool IsWizardActive { get; init; }

    /// <summary>Creates initial state from a workspace.</summary>
    public static TuiState CreateFromWorkspace(WorkspaceManager workspace)
    {
        var config = workspace.Config;
        var defaultChannel = config?.Channels
            .FirstOrDefault(c => c.Value.IsDefault).Key
            ?? config?.Channels.Keys.FirstOrDefault()
            ?? "prod";

        return new TuiState
        {
            Workspace = new WorkspaceState
            {
                Path = workspace.WorkspacePath,
                ProjectName = config?.Project.Name ?? "Unknown",
                ProjectId = config?.Project.Id ?? "unknown",
                CurrentChannel = defaultChannel,
                Channels = config?.Channels.Keys.ToList() ?? [],
                HasPublishProfiles = config?.PublishProfiles.Count > 0
            }
        };
    }
}

/// <summary>
/// State related to the workspace and current selection.
/// </summary>
public sealed record WorkspaceState
{
    /// <summary>Path to the workspace root.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Project display name.</summary>
    public string ProjectName { get; init; } = string.Empty;

    /// <summary>Project identifier.</summary>
    public string ProjectId { get; init; } = string.Empty;

    /// <summary>Currently selected channel.</summary>
    public string CurrentChannel { get; init; } = string.Empty;

    /// <summary>Currently selected version (null if none).</summary>
    public string? CurrentVersion { get; init; }

    /// <summary>Available channels.</summary>
    public IReadOnlyList<string> Channels { get; init; } = [];

    /// <summary>Available versions in current channel.</summary>
    public IReadOnlyList<string> Versions { get; init; } = [];

    /// <summary>Whether publish profiles are configured.</summary>
    public bool HasPublishProfiles { get; init; }

    /// <summary>Version metadata for current selection.</summary>
    public VersionMetadata? CurrentMetadata { get; init; }
}

/// <summary>
/// State for the file list panel.
/// </summary>
public sealed record FileListState
{
    /// <summary>All files in the current version.</summary>
    public IReadOnlyList<FileListItem> AllFiles { get; init; } = [];

    /// <summary>Filtered files based on current filter.</summary>
    public IReadOnlyList<FileListItem> FilteredFiles { get; init; } = [];

    /// <summary>Currently selected file index.</summary>
    public int SelectedIndex { get; init; }

    /// <summary>First visible item index (for scrolling).</summary>
    public int ScrollOffset { get; init; }

    /// <summary>Number of visible items.</summary>
    public int VisibleCount { get; init; } = 20;

    /// <summary>Current filter text.</summary>
    public string Filter { get; init; } = string.Empty;

    /// <summary>Selected file paths (for multi-select).</summary>
    public IReadOnlySet<string> SelectedPaths { get; init; } = new HashSet<string>();

    /// <summary>Whether multi-select mode is active.</summary>
    public bool IsMultiSelect { get; init; }

    /// <summary>Currently selected file (convenience property).</summary>
    public FileListItem? SelectedFile =>
        SelectedIndex >= 0 && SelectedIndex < FilteredFiles.Count
            ? FilteredFiles[SelectedIndex]
            : null;

    /// <summary>Apply filter and return new state.</summary>
    public FileListState WithFilter(string filter)
    {
        var filtered = string.IsNullOrWhiteSpace(filter)
            ? AllFiles
            : AllFiles.Where(f => f.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        return this with
        {
            Filter = filter,
            FilteredFiles = filtered,
            SelectedIndex = Math.Min(SelectedIndex, Math.Max(0, filtered.Count - 1)),
            ScrollOffset = 0
        };
    }
}

/// <summary>
/// A file item in the file list.
/// </summary>
public sealed record FileListItem
{
    /// <summary>Relative file path.</summary>
    public required string Path { get; init; }

    /// <summary>File size in bytes.</summary>
    public long Size { get; init; }

    /// <summary>File hash (SHA256).</summary>
    public string Hash { get; init; } = string.Empty;

    /// <summary>Update strategy.</summary>
    public UpdateStrategy Strategy { get; init; }

    /// <summary>Whether strategy is a manual override.</summary>
    public bool IsOverride { get; init; }

    /// <summary>Base hash for UpdateIfNotModified.</summary>
    public string? BaseHash { get; init; }

    /// <summary>File extension.</summary>
    public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();

    /// <summary>File name without path.</summary>
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>Directory portion of path.</summary>
    public string Directory => System.IO.Path.GetDirectoryName(Path)?.Replace('\\', '/') ?? string.Empty;

    /// <summary>Create from VersionFileInfo.</summary>
    public static FileListItem FromVersionFileInfo(VersionFileInfo info) => new()
    {
        Path = info.Path,
        Size = info.Size,
        Hash = info.Hash,
        Strategy = info.Strategy,
        IsOverride = info.IsOverride,
        BaseHash = info.BaseHash
    };
}

/// <summary>
/// State for build operations.
/// </summary>
public sealed record BuildState
{
    /// <summary>Whether a build is in progress.</summary>
    public bool IsBuilding { get; init; }

    /// <summary>Current file being processed.</summary>
    public string? CurrentFile { get; init; }

    /// <summary>Number of files processed.</summary>
    public int ProcessedFiles { get; init; }

    /// <summary>Total number of files.</summary>
    public int TotalFiles { get; init; }

    /// <summary>Build progress percentage.</summary>
    public double Percentage { get; init; }

    /// <summary>Build start time.</summary>
    public DateTime? StartTime { get; init; }

    /// <summary>Build error message (if failed).</summary>
    public string? Error { get; init; }

    /// <summary>Base version being compared against.</summary>
    public string? BaseVersion { get; init; }

    /// <summary>Comparison result.</summary>
    public BuildComparisonResult? ComparisonResult { get; init; }

    /// <summary>Whether the build completed successfully.</summary>
    public bool IsCompleted { get; init; }

    /// <summary>Elapsed time since build started.</summary>
    public TimeSpan Elapsed => StartTime.HasValue
        ? DateTime.UtcNow - StartTime.Value
        : TimeSpan.Zero;
}

/// <summary>
/// State for publish operations.
/// </summary>
public sealed record PublishState
{
    /// <summary>Whether a publish is in progress.</summary>
    public bool IsPublishing { get; init; }

    /// <summary>Whether in confirmation stage.</summary>
    public bool IsConfirming { get; init; }

    /// <summary>Current file being uploaded.</summary>
    public string? CurrentFile { get; init; }

    /// <summary>Number of files uploaded.</summary>
    public int UploadedFiles { get; init; }

    /// <summary>Total number of files.</summary>
    public int TotalFiles { get; init; }

    /// <summary>Bytes uploaded.</summary>
    public long UploadedBytes { get; init; }

    /// <summary>Total bytes to upload.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Publish start time.</summary>
    public DateTime? StartTime { get; init; }

    /// <summary>Publish error message (if failed).</summary>
    public string? Error { get; init; }

    /// <summary>Number of failed file uploads.</summary>
    public int FailedFiles { get; init; }

    /// <summary>Manifest URL (if completed).</summary>
    public string? ManifestUrl { get; init; }

    /// <summary>Upload percentage.</summary>
    public double Percentage => TotalBytes > 0
        ? (double)UploadedBytes / TotalBytes * 100
        : 0;

    /// <summary>Elapsed time since publish started.</summary>
    public TimeSpan Elapsed => StartTime.HasValue
        ? DateTime.UtcNow - StartTime.Value
        : TimeSpan.Zero;

    /// <summary>Estimated upload speed (bytes/sec).</summary>
    public long BytesPerSecond => Elapsed.TotalSeconds > 0
        ? (long)(UploadedBytes / Elapsed.TotalSeconds)
        : 0;
}

/// <summary>
/// State for dialog/modal display.
/// </summary>
public sealed record DialogState
{
    /// <summary>Dialog title.</summary>
    public required string Title { get; init; }

    /// <summary>Dialog message.</summary>
    public required string Message { get; init; }

    /// <summary>Dialog type.</summary>
    public DialogType Type { get; init; } = DialogType.Info;

    /// <summary>Message to dispatch on confirm.</summary>
    public ITuiMessage? OnConfirm { get; init; }

    /// <summary>Message to dispatch on cancel.</summary>
    public ITuiMessage? OnCancel { get; init; }

    /// <summary>Available choices (for selection dialogs).</summary>
    public IReadOnlyList<string>? Choices { get; init; }

    /// <summary>Selected choice index.</summary>
    public int SelectedChoice { get; init; }
}

/// <summary>
/// Type of dialog.
/// </summary>
public enum DialogType
{
    Info,
    Warning,
    Error,
    Confirm,
    Selection
}

/// <summary>
/// State for text input mode.
/// </summary>
public sealed record InputState
{
    /// <summary>Input prompt text.</summary>
    public required string Prompt { get; init; }

    /// <summary>Current input text.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Cursor position.</summary>
    public int CursorPosition { get; init; }

    /// <summary>Callback when input is submitted.</summary>
    public required Action<string> OnSubmit { get; init; }

    /// <summary>Callback when input is cancelled.</summary>
    public Action? OnCancel { get; init; }
}

/// <summary>
/// Current status message.
/// </summary>
public sealed record StatusMessage
{
    /// <summary>Message text.</summary>
    public required string Text { get; init; }

    /// <summary>Message type.</summary>
    public StatusType Type { get; init; } = StatusType.Info;

    /// <summary>When the message was created.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Duration to display (null = until dismissed).</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Whether the message has expired.</summary>
    public bool IsExpired => Duration.HasValue &&
        DateTime.UtcNow - CreatedAt > Duration.Value;
}

/// <summary>
/// Terminal size.
/// </summary>
public sealed record TerminalSize(int Width, int Height)
{
    /// <summary>Whether the terminal is too small for the TUI.</summary>
    public bool IsTooSmall => Width < 60 || Height < 15;

    /// <summary>Maximum visible file list items based on height.</summary>
    public int MaxVisibleFiles => Math.Max(5, Height - 8);
}
