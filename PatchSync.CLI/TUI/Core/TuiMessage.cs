namespace PatchSync.CLI.TUI.Core;

/// <summary>
/// Base interface for all TUI messages (commands and events).
/// Uses record types for immutability and AOT compatibility.
/// </summary>
public interface ITuiMessage;

#region Navigation Messages

/// <summary>Navigate to the next item in the current list.</summary>
public sealed record NavigateDown : ITuiMessage;

/// <summary>Navigate to the previous item in the current list.</summary>
public sealed record NavigateUp : ITuiMessage;

/// <summary>Navigate to the first item in the current list.</summary>
public sealed record NavigateFirst : ITuiMessage;

/// <summary>Navigate to the last item in the current list.</summary>
public sealed record NavigateLast : ITuiMessage;

/// <summary>Move forward one page in the list.</summary>
public sealed record PageDown : ITuiMessage;

/// <summary>Move backward one page in the list.</summary>
public sealed record PageUp : ITuiMessage;

/// <summary>Switch to the next panel.</summary>
public sealed record NextPanel : ITuiMessage;

/// <summary>Switch to the previous panel.</summary>
public sealed record PreviousPanel : ITuiMessage;

/// <summary>Switch to a specific panel by index.</summary>
public sealed record SwitchPanel(int PanelIndex) : ITuiMessage;

#endregion

#region Screen Navigation

/// <summary>Navigate to the dashboard screen.</summary>
public sealed record GoToDashboard : ITuiMessage;

/// <summary>Navigate to the versions screen.</summary>
public sealed record GoToVersions : ITuiMessage;

/// <summary>Navigate to the settings screen.</summary>
public sealed record GoToSettings : ITuiMessage;

/// <summary>Navigate to the build screen for a specific version.</summary>
public sealed record GoToBuild(string Channel, string Version) : ITuiMessage;

/// <summary>Go back to the previous screen.</summary>
public sealed record GoBack : ITuiMessage;

#endregion

#region Selection & Actions

/// <summary>Select/activate the current item.</summary>
public sealed record SelectItem : ITuiMessage;

/// <summary>Select a specific item by path.</summary>
public sealed record SelectItemByPath(string Path) : ITuiMessage;

/// <summary>Toggle selection of the current item (multi-select mode).</summary>
public sealed record ToggleSelection : ITuiMessage;

/// <summary>Select all items in the current list.</summary>
public sealed record SelectAll : ITuiMessage;

/// <summary>Clear all selections.</summary>
public sealed record ClearSelection : ITuiMessage;

#endregion

#region File Operations

/// <summary>Change the update strategy for selected file(s).</summary>
public sealed record ChangeStrategy(Common.Manifest.UpdateStrategy NewStrategy) : ITuiMessage;

/// <summary>Open strategy selection dialog for current file.</summary>
public sealed record OpenStrategyDialog : ITuiMessage;

/// <summary>Add a new file to the version.</summary>
public sealed record AddFile(string SourcePath, string? TargetPath = null) : ITuiMessage;

/// <summary>Remove selected file(s) from the version.</summary>
public sealed record RemoveFiles : ITuiMessage;

/// <summary>Refresh the file list from disk.</summary>
public sealed record RefreshFileList : ITuiMessage;

#endregion

#region Filter & Search

/// <summary>Open the filter/search input.</summary>
public sealed record OpenFilter : ITuiMessage;

/// <summary>Close the filter/search input.</summary>
public sealed record CloseFilter : ITuiMessage;

/// <summary>Update the filter text.</summary>
public sealed record SetFilter(string FilterText) : ITuiMessage;

/// <summary>Clear the current filter.</summary>
public sealed record ClearFilter : ITuiMessage;

/// <summary>Filter by file extension.</summary>
public sealed record FilterByExtension(string Extension) : ITuiMessage;

/// <summary>Filter by update strategy.</summary>
public sealed record FilterByStrategy(Common.Manifest.UpdateStrategy Strategy) : ITuiMessage;

#endregion

#region Channel & Version

/// <summary>Switch to a different channel.</summary>
public sealed record SwitchChannel(string ChannelId) : ITuiMessage;

/// <summary>Switch to a different version within the current channel.</summary>
public sealed record SwitchVersion(string Version) : ITuiMessage;

/// <summary>Create a new version in the current channel.</summary>
public sealed record CreateVersion(string Version, string? InputPath = null) : ITuiMessage;

/// <summary>Promote the current version to another channel.</summary>
public sealed record PromoteVersion(string TargetChannel, string? TargetVersion = null) : ITuiMessage;

/// <summary>Delete a version.</summary>
public sealed record DeleteVersion(string Channel, string Version) : ITuiMessage;

#endregion

#region Build Operations

/// <summary>Start a build operation.</summary>
public sealed record StartBuild(string InputPath, string? BaseVersion = null) : ITuiMessage;

/// <summary>Cancel the current build operation.</summary>
public sealed record CancelBuild : ITuiMessage;

/// <summary>Build progress update.</summary>
public sealed record BuildProgress(int Processed, int Total, string CurrentFile, double Percentage) : ITuiMessage;

/// <summary>Build completed successfully.</summary>
public sealed record BuildCompleted(TimeSpan Duration, int FileCount, long TotalSize) : ITuiMessage;

/// <summary>Build failed with error.</summary>
public sealed record BuildFailed(string Error) : ITuiMessage;

#endregion

#region Publish Operations

/// <summary>Start the publish process.</summary>
public sealed record StartPublish : ITuiMessage;

/// <summary>Confirm and execute publish.</summary>
public sealed record ConfirmPublish : ITuiMessage;

/// <summary>Cancel the publish process.</summary>
public sealed record CancelPublish : ITuiMessage;

/// <summary>Publish progress update.</summary>
public sealed record PublishProgress(int UploadedFiles, int TotalFiles, long UploadedBytes, long TotalBytes, string CurrentFile) : ITuiMessage;

/// <summary>Publish completed.</summary>
public sealed record PublishCompleted(string ManifestUrl) : ITuiMessage;

/// <summary>Publish failed.</summary>
public sealed record PublishFailed(string Error, int FailedFiles) : ITuiMessage;

/// <summary>Retry failed uploads.</summary>
public sealed record RetryFailedUploads : ITuiMessage;

#endregion

#region Dialog & Modal

/// <summary>Show a confirmation dialog.</summary>
public sealed record ShowConfirmDialog(string Title, string Message, ITuiMessage OnConfirm, ITuiMessage? OnCancel = null) : ITuiMessage;

/// <summary>Show an error dialog.</summary>
public sealed record ShowErrorDialog(string Title, string Message) : ITuiMessage;

/// <summary>Show an info dialog.</summary>
public sealed record ShowInfoDialog(string Title, string Message) : ITuiMessage;

/// <summary>Close the current dialog/modal.</summary>
public sealed record CloseDialog : ITuiMessage;

/// <summary>Dialog was confirmed.</summary>
public sealed record DialogConfirmed : ITuiMessage;

/// <summary>Dialog was cancelled.</summary>
public sealed record DialogCancelled : ITuiMessage;

#endregion

#region Wizard Integration

/// <summary>Launch the CDN setup wizard.</summary>
public sealed record LaunchCdnSetupWizard : ITuiMessage;

/// <summary>Launch the init wizard.</summary>
public sealed record LaunchInitWizard : ITuiMessage;

/// <summary>Wizard completed successfully.</summary>
public sealed record WizardCompleted : ITuiMessage;

/// <summary>Wizard was cancelled.</summary>
public sealed record WizardCancelled : ITuiMessage;

#endregion

#region Application Lifecycle

/// <summary>Request to quit the application.</summary>
public sealed record RequestQuit : ITuiMessage;

/// <summary>Confirm quit and exit.</summary>
public sealed record ConfirmQuit : ITuiMessage;

/// <summary>Refresh the entire UI.</summary>
public sealed record RefreshUI : ITuiMessage;

/// <summary>Resize event from terminal.</summary>
public sealed record TerminalResized(int Width, int Height) : ITuiMessage;

/// <summary>An error occurred.</summary>
public sealed record ErrorOccurred(string Message, Exception? Exception = null) : ITuiMessage;

/// <summary>Clear the current error/status message.</summary>
public sealed record ClearStatus : ITuiMessage;

/// <summary>Show a status message.</summary>
public sealed record ShowStatus(string Message, StatusType Type = StatusType.Info) : ITuiMessage;

#endregion

#region Input Mode

/// <summary>Enter text input mode.</summary>
public sealed record EnterInputMode(string Prompt, Action<string> OnSubmit, Action? OnCancel = null) : ITuiMessage;

/// <summary>Exit text input mode.</summary>
public sealed record ExitInputMode : ITuiMessage;

/// <summary>Text input changed.</summary>
public sealed record InputChanged(string Text) : ITuiMessage;

/// <summary>Text input submitted.</summary>
public sealed record InputSubmitted(string Text) : ITuiMessage;

#endregion

/// <summary>
/// Type of status message.
/// </summary>
public enum StatusType
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>
/// Active screen in the TUI.
/// </summary>
public enum TuiScreen
{
    Dashboard,
    Versions,
    Build,
    Settings,
    Publish
}

/// <summary>
/// Active panel in a multi-panel screen.
/// </summary>
public enum TuiPanel
{
    FileList,
    Details,
    Channels,
    Versions
}
