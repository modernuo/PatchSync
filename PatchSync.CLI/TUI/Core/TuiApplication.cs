using System.Threading.Channels;
using PatchSync.CLI.Workspace;
using PatchSync.Common.Manifest;
using Spectre.Console;

namespace PatchSync.CLI.TUI.Core;

/// <summary>
/// Main TUI application with event loop using Spectre.Console Live display.
/// Uses Elm-style architecture: State → Render → Event → Update → State
/// </summary>
public sealed class TuiApplication : IDisposable
{
    private readonly WorkspaceManager _workspace;
    private readonly KeyDispatcher _keyDispatcher;
    private readonly TuiRenderer _renderer;
    private readonly CancellationTokenSource _cts;
    private readonly Channel<ITuiMessage> _messageChannel;

    private TuiState _state;
    private bool _disposed;

    public TuiApplication(WorkspaceManager workspace)
    {
        _workspace = workspace;
        _keyDispatcher = new KeyDispatcher();
        _renderer = new TuiRenderer();
        _cts = new CancellationTokenSource();
        _messageChannel = Channel.CreateUnbounded<ITuiMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _state = TuiState.CreateFromWorkspace(workspace);
    }

    /// <summary>
    /// Run the TUI application until exit.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        var token = linkedCts.Token;

        // Initial file list load
        await LoadFilesAsync(token);

        // Start input handling on a background thread
        _ = Task.Run(() => HandleInputAsync(token), token);

        // Main render loop
        await AnsiConsole.Live(_renderer.Render(_state))
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .Cropping(VerticalOverflowCropping.Top)
            .StartAsync(async ctx =>
            {
                while (_state.IsRunning && !token.IsCancellationRequested)
                {
                    // Check for terminal resize
                    var newSize = new TerminalSize(Console.WindowWidth, Console.WindowHeight);
                    if (newSize != _state.Terminal)
                    {
                        _state = _state with { Terminal = newSize };
                    }

                    // Process all pending messages
                    while (_messageChannel.Reader.TryRead(out var message))
                    {
                        _state = await UpdateAsync(_state, message, token);
                    }

                    // Update status expiration
                    if (_state.Status?.IsExpired == true)
                    {
                        _state = _state with { Status = null };
                    }

                    // Render
                    ctx.UpdateTarget(_renderer.Render(_state));

                    // Small delay to prevent busy-waiting
                    await Task.Delay(16, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            });
    }

    /// <summary>
    /// Post a message to be processed by the update loop.
    /// </summary>
    public void PostMessage(ITuiMessage message)
    {
        _messageChannel.Writer.TryWrite(message);
    }

    private async Task HandleInputAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _state.IsRunning)
        {
            if (!Console.KeyAvailable)
            {
                await Task.Delay(10, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                continue;
            }

            // Skip input when wizard is active
            if (_state.IsWizardActive)
            {
                await Task.Delay(50, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                continue;
            }

            var keyInfo = Console.ReadKey(intercept: true);
            var message = _keyDispatcher.Dispatch(keyInfo, _state);

            if (message != null)
            {
                PostMessage(message);
            }
        }
    }

    /// <summary>
    /// Process a message and return updated state.
    /// This is the core reducer function.
    /// </summary>
    private async Task<TuiState> UpdateAsync(TuiState state, ITuiMessage message, CancellationToken token)
    {
        return message switch
        {
            // Navigation
            NavigateDown => HandleNavigateDown(state),
            NavigateUp => HandleNavigateUp(state),
            NavigateFirst => HandleNavigateFirst(state),
            NavigateLast => HandleNavigateLast(state),
            PageDown => HandlePageDown(state),
            PageUp => HandlePageUp(state),
            NextPanel => HandleNextPanel(state),
            PreviousPanel => HandlePreviousPanel(state),

            // Screen navigation
            GoToDashboard => HandleGoToScreen(state, TuiScreen.Dashboard),
            GoToVersions => HandleGoToScreen(state, TuiScreen.Versions),
            GoToSettings => HandleGoToScreen(state, TuiScreen.Settings),
            GoToBuild(var channel, var version) => HandleGoToBuild(state, channel, version),
            GoBack => HandleGoBack(state),

            // Selection
            SelectItem => await HandleSelectItemAsync(state, token),
            ToggleSelection => HandleToggleSelection(state),
            SelectAll => HandleSelectAll(state),
            ClearSelection => HandleClearSelection(state),

            // File operations
            OpenStrategyDialog => HandleOpenStrategyDialog(state),
            ChangeStrategy(var strategy) => await HandleChangeStrategyAsync(state, strategy, token),
            RemoveFiles => await HandleRemoveFilesAsync(state, token),
            RefreshFileList => await HandleRefreshFileListAsync(state, token),

            // Filter
            OpenFilter => HandleOpenFilter(state),
            SetFilter(var text) => HandleSetFilter(state, text),
            ClearFilter => HandleClearFilter(state),

            // Channel/Version
            SwitchChannel(var channelId) => await HandleSwitchChannelAsync(state, channelId, token),
            SwitchVersion(var version) => await HandleSwitchVersionAsync(state, version, token),

            // Dialog
            ShowConfirmDialog(var title, var msg, var onConfirm, var onCancel) =>
                HandleShowConfirmDialog(state, title, msg, onConfirm, onCancel),
            ShowErrorDialog(var title, var msg) => HandleShowErrorDialog(state, title, msg),
            ShowInfoDialog(var title, var msg) => HandleShowInfoDialog(state, title, msg),
            CloseDialog => HandleCloseDialog(state),
            DialogConfirmed => await HandleDialogConfirmedAsync(state, token),
            DialogCancelled => HandleDialogCancelled(state),

            // Input mode
            EnterInputMode(var prompt, var onSubmit, var onCancel) =>
                HandleEnterInputMode(state, prompt, onSubmit, onCancel),
            ExitInputMode => HandleExitInputMode(state),
            InputChanged(var text) => HandleInputChanged(state, text),
            InputSubmitted(var text) => HandleInputSubmitted(state, text),

            // Wizard
            LaunchCdnSetupWizard => await HandleLaunchCdnSetupWizardAsync(state, token),
            WizardCompleted => HandleWizardCompleted(state),
            WizardCancelled => HandleWizardCancelled(state),

            // Application lifecycle
            RequestQuit => HandleRequestQuit(state),
            ConfirmQuit => state with { IsRunning = false },
            RefreshUI => state,
            TerminalResized(var width, var height) => state with
            {
                Terminal = new TerminalSize(width, height)
            },
            ClearStatus => state with { Status = null },
            ShowStatus(var text, var type) => state with
            {
                Status = new StatusMessage { Text = text, Type = type, Duration = TimeSpan.FromSeconds(3) }
            },

            // Build messages
            StartBuild(var inputPath, var baseVersion) => await HandleStartBuildAsync(state, inputPath, baseVersion, token),
            CancelBuild => HandleCancelBuild(state),
            BuildProgress(var processed, var total, var currentFile, var percentage) =>
                HandleBuildProgress(state, processed, total, currentFile, percentage),
            BuildCompleted(var duration, var fileCount, var totalSize) =>
                HandleBuildCompleted(state, duration, fileCount, totalSize),
            BuildFailed(var error) => HandleBuildFailed(state, error),

            // Publish messages
            StartPublish => await HandleStartPublishAsync(state, token),
            CancelPublish => HandleCancelPublish(state),
            PublishProgress(var uploadedFiles, var totalFiles, var uploadedBytes, var totalBytes, var currentFile) =>
                HandlePublishProgress(state, uploadedFiles, totalFiles, uploadedBytes, totalBytes, currentFile),
            PublishCompleted(var manifestUrl) => HandlePublishCompleted(state, manifestUrl),
            PublishFailed(var error, var failedFiles) => HandlePublishFailed(state, error, failedFiles),

            // Unhandled
            _ => state
        };
    }

    #region Navigation Handlers

    private static TuiState HandleNavigateDown(TuiState state)
    {
        var fileList = state.FileList;
        if (fileList.FilteredFiles.Count == 0) return state;

        var newIndex = Math.Min(fileList.SelectedIndex + 1, fileList.FilteredFiles.Count - 1);
        var newOffset = fileList.ScrollOffset;

        // Scroll down if needed
        if (newIndex >= newOffset + fileList.VisibleCount)
        {
            newOffset = newIndex - fileList.VisibleCount + 1;
        }

        return state with
        {
            FileList = fileList with
            {
                SelectedIndex = newIndex,
                ScrollOffset = newOffset
            }
        };
    }

    private static TuiState HandleNavigateUp(TuiState state)
    {
        var fileList = state.FileList;
        if (fileList.FilteredFiles.Count == 0) return state;

        var newIndex = Math.Max(0, fileList.SelectedIndex - 1);
        var newOffset = fileList.ScrollOffset;

        // Scroll up if needed
        if (newIndex < newOffset)
        {
            newOffset = newIndex;
        }

        return state with
        {
            FileList = fileList with
            {
                SelectedIndex = newIndex,
                ScrollOffset = newOffset
            }
        };
    }

    private static TuiState HandleNavigateFirst(TuiState state)
    {
        return state with
        {
            FileList = state.FileList with
            {
                SelectedIndex = 0,
                ScrollOffset = 0
            }
        };
    }

    private static TuiState HandleNavigateLast(TuiState state)
    {
        var fileList = state.FileList;
        if (fileList.FilteredFiles.Count == 0) return state;

        var lastIndex = fileList.FilteredFiles.Count - 1;
        var newOffset = Math.Max(0, lastIndex - fileList.VisibleCount + 1);

        return state with
        {
            FileList = fileList with
            {
                SelectedIndex = lastIndex,
                ScrollOffset = newOffset
            }
        };
    }

    private static TuiState HandlePageDown(TuiState state)
    {
        var fileList = state.FileList;
        if (fileList.FilteredFiles.Count == 0) return state;

        var pageSize = fileList.VisibleCount;
        var newIndex = Math.Min(fileList.SelectedIndex + pageSize, fileList.FilteredFiles.Count - 1);
        var newOffset = Math.Max(0, newIndex - pageSize + 1);

        return state with
        {
            FileList = fileList with
            {
                SelectedIndex = newIndex,
                ScrollOffset = Math.Min(newOffset, Math.Max(0, fileList.FilteredFiles.Count - pageSize))
            }
        };
    }

    private static TuiState HandlePageUp(TuiState state)
    {
        var fileList = state.FileList;
        if (fileList.FilteredFiles.Count == 0) return state;

        var pageSize = fileList.VisibleCount;
        var newIndex = Math.Max(0, fileList.SelectedIndex - pageSize);

        return state with
        {
            FileList = fileList with
            {
                SelectedIndex = newIndex,
                ScrollOffset = Math.Max(0, fileList.ScrollOffset - pageSize)
            }
        };
    }

    private static TuiState HandleNextPanel(TuiState state)
    {
        var panels = state.CurrentScreen switch
        {
            TuiScreen.Dashboard => new[] { TuiPanel.FileList, TuiPanel.Details },
            TuiScreen.Versions => new[] { TuiPanel.Channels, TuiPanel.Versions },
            _ => new[] { state.ActivePanel }
        };

        var currentIndex = Array.IndexOf(panels, state.ActivePanel);
        var nextIndex = (currentIndex + 1) % panels.Length;

        return state with { ActivePanel = panels[nextIndex] };
    }

    private static TuiState HandlePreviousPanel(TuiState state)
    {
        var panels = state.CurrentScreen switch
        {
            TuiScreen.Dashboard => new[] { TuiPanel.FileList, TuiPanel.Details },
            TuiScreen.Versions => new[] { TuiPanel.Channels, TuiPanel.Versions },
            _ => new[] { state.ActivePanel }
        };

        var currentIndex = Array.IndexOf(panels, state.ActivePanel);
        var prevIndex = (currentIndex - 1 + panels.Length) % panels.Length;

        return state with { ActivePanel = panels[prevIndex] };
    }

    #endregion

    #region Screen Navigation Handlers

    private static TuiState HandleGoToScreen(TuiState state, TuiScreen screen)
    {
        if (state.CurrentScreen == screen) return state;

        var history = state.ScreenHistory.Append(state.CurrentScreen).ToList();

        return state with
        {
            CurrentScreen = screen,
            ScreenHistory = history,
            ActivePanel = screen switch
            {
                TuiScreen.Dashboard => TuiPanel.FileList,
                TuiScreen.Versions => TuiPanel.Channels,
                _ => TuiPanel.FileList
            }
        };
    }

    private static TuiState HandleGoToBuild(TuiState state, string channel, string version)
    {
        var history = state.ScreenHistory.Append(state.CurrentScreen).ToList();

        return state with
        {
            CurrentScreen = TuiScreen.Build,
            ScreenHistory = history,
            Workspace = state.Workspace with
            {
                CurrentChannel = channel,
                CurrentVersion = version
            }
        };
    }

    private static TuiState HandleGoBack(TuiState state)
    {
        if (state.ScreenHistory.Count == 0) return state;

        var history = state.ScreenHistory.ToList();
        var previousScreen = history[^1];
        history.RemoveAt(history.Count - 1);

        return state with
        {
            CurrentScreen = previousScreen,
            ScreenHistory = history
        };
    }

    #endregion

    #region Selection Handlers

    private async Task<TuiState> HandleSelectItemAsync(TuiState state, CancellationToken token)
    {
        if (state.CurrentScreen == TuiScreen.Versions)
        {
            if (state.ActivePanel == TuiPanel.Versions && state.Workspace.Versions.Count > 0)
            {
                // Select version and go to dashboard
                var version = state.Workspace.Versions[state.FileList.SelectedIndex];
                state = state with
                {
                    Workspace = state.Workspace with { CurrentVersion = version }
                };
                return await HandleSwitchVersionAsync(state, version, token);
            }
        }

        return state;
    }

    private static TuiState HandleToggleSelection(TuiState state)
    {
        var file = state.FileList.SelectedFile;
        if (file == null) return state;

        var paths = state.FileList.SelectedPaths.ToHashSet();
        if (paths.Contains(file.Path))
        {
            paths.Remove(file.Path);
        }
        else
        {
            paths.Add(file.Path);
        }

        return state with
        {
            FileList = state.FileList with
            {
                SelectedPaths = paths,
                IsMultiSelect = paths.Count > 0
            }
        };
    }

    private static TuiState HandleSelectAll(TuiState state)
    {
        var paths = state.FileList.FilteredFiles.Select(f => f.Path).ToHashSet();

        return state with
        {
            FileList = state.FileList with
            {
                SelectedPaths = paths,
                IsMultiSelect = true
            }
        };
    }

    private static TuiState HandleClearSelection(TuiState state)
    {
        return state with
        {
            FileList = state.FileList with
            {
                SelectedPaths = new HashSet<string>(),
                IsMultiSelect = false
            }
        };
    }

    #endregion

    #region File Operation Handlers

    private static TuiState HandleOpenStrategyDialog(TuiState state)
    {
        var file = state.FileList.SelectedFile;
        if (file == null) return state;

        var strategies = new List<string>
        {
            "Delta - Download only changed chunks",
            "AlwaysCompressed - Always download compressed",
            "HashCheck - Download if hash differs",
            "CreateOnly - Only create if missing",
            "UpdateIfNotModified - Update if not locally modified",
            "Delete - Mark for deletion"
        };

        return state with
        {
            Dialog = new DialogState
            {
                Title = $"Strategy for {file.FileName}",
                Message = "Select update strategy:",
                Type = DialogType.Selection,
                Choices = strategies
            }
        };
    }

    private async Task<TuiState> HandleChangeStrategyAsync(TuiState state, Common.Manifest.UpdateStrategy strategy, CancellationToken token)
    {
        // TODO: Integrate with FileOperationsService to persist change
        return state with
        {
            Status = new StatusMessage
            {
                Text = $"Changed strategy to {strategy}",
                Type = StatusType.Success,
                Duration = TimeSpan.FromSeconds(2)
            }
        };
    }

    private async Task<TuiState> HandleRemoveFilesAsync(TuiState state, CancellationToken token)
    {
        var paths = state.FileList.IsMultiSelect
            ? state.FileList.SelectedPaths.ToList()
            : state.FileList.SelectedFile != null
                ? [state.FileList.SelectedFile.Path]
                : [];

        if (paths.Count == 0) return state;

        return state with
        {
            Dialog = new DialogState
            {
                Title = "Remove Files",
                Message = $"Remove {paths.Count} file(s) from version?",
                Type = DialogType.Confirm,
                OnConfirm = new RemoveFiles()
            }
        };
    }

    private async Task<TuiState> HandleRefreshFileListAsync(TuiState state, CancellationToken token)
    {
        await LoadFilesAsync(token);

        return _state with
        {
            Status = new StatusMessage
            {
                Text = "File list refreshed",
                Type = StatusType.Info,
                Duration = TimeSpan.FromSeconds(2)
            }
        };
    }

    #endregion

    #region Filter Handlers

    private static TuiState HandleOpenFilter(TuiState state)
    {
        return state with
        {
            Input = new InputState
            {
                Prompt = "Filter",
                Text = state.FileList.Filter,
                OnSubmit = _ => { }, // Handled by InputSubmitted message
                OnCancel = () => { }
            }
        };
    }

    private static TuiState HandleSetFilter(TuiState state, string text)
    {
        return state with
        {
            FileList = state.FileList.WithFilter(text)
        };
    }

    private static TuiState HandleClearFilter(TuiState state)
    {
        return state with
        {
            FileList = state.FileList.WithFilter(string.Empty)
        };
    }

    #endregion

    #region Channel/Version Handlers

    private async Task<TuiState> HandleSwitchChannelAsync(TuiState state, string channelId, CancellationToken token)
    {
        // Load versions for this channel
        var versionsPath = Path.Combine(_workspace.WorkspacePath, ".patchsync", "channels", channelId);
        var versions = new List<string>();

        if (Directory.Exists(versionsPath))
        {
            versions = Directory.GetDirectories(versionsPath)
                .Select(Path.GetFileName)
                .Where(v => v != null)
                .Cast<string>()
                .OrderByDescending(v => v)
                .ToList();
        }

        return state with
        {
            Workspace = state.Workspace with
            {
                CurrentChannel = channelId,
                Versions = versions,
                CurrentVersion = versions.FirstOrDefault()
            }
        };
    }

    private async Task<TuiState> HandleSwitchVersionAsync(TuiState state, string version, CancellationToken token)
    {
        state = state with
        {
            Workspace = state.Workspace with { CurrentVersion = version }
        };

        await LoadFilesAsync(token);

        return _state with
        {
            CurrentScreen = TuiScreen.Dashboard,
            ActivePanel = TuiPanel.FileList
        };
    }

    #endregion

    #region Dialog Handlers

    private static TuiState HandleShowConfirmDialog(TuiState state, string title, string message,
        ITuiMessage onConfirm, ITuiMessage? onCancel)
    {
        return state with
        {
            Dialog = new DialogState
            {
                Title = title,
                Message = message,
                Type = DialogType.Confirm,
                OnConfirm = onConfirm,
                OnCancel = onCancel
            }
        };
    }

    private static TuiState HandleShowErrorDialog(TuiState state, string title, string message)
    {
        return state with
        {
            Dialog = new DialogState
            {
                Title = title,
                Message = message,
                Type = DialogType.Error
            }
        };
    }

    private static TuiState HandleShowInfoDialog(TuiState state, string title, string message)
    {
        return state with
        {
            Dialog = new DialogState
            {
                Title = title,
                Message = message,
                Type = DialogType.Info
            }
        };
    }

    private static TuiState HandleCloseDialog(TuiState state)
    {
        return state with { Dialog = null };
    }

    private async Task<TuiState> HandleDialogConfirmedAsync(TuiState state, CancellationToken token)
    {
        var onConfirm = state.Dialog?.OnConfirm;
        state = state with { Dialog = null };

        if (onConfirm != null)
        {
            return await UpdateAsync(state, onConfirm, token);
        }

        return state;
    }

    private static TuiState HandleDialogCancelled(TuiState state)
    {
        var onCancel = state.Dialog?.OnCancel;
        var newState = state with { Dialog = null };

        // OnCancel is handled if it's a message
        return newState;
    }

    #endregion

    #region Input Mode Handlers

    private static TuiState HandleEnterInputMode(TuiState state, string prompt,
        Action<string> onSubmit, Action? onCancel)
    {
        return state with
        {
            Input = new InputState
            {
                Prompt = prompt,
                OnSubmit = onSubmit,
                OnCancel = onCancel
            }
        };
    }

    private static TuiState HandleExitInputMode(TuiState state)
    {
        state.Input?.OnCancel?.Invoke();
        return state with { Input = null };
    }

    private static TuiState HandleInputChanged(TuiState state, string text)
    {
        if (state.Input == null) return state;

        // For filter input, update filter in real-time
        if (state.Input.Prompt == "Filter")
        {
            return state with
            {
                Input = state.Input with { Text = text },
                FileList = state.FileList.WithFilter(text)
            };
        }

        return state with
        {
            Input = state.Input with { Text = text }
        };
    }

    private static TuiState HandleInputSubmitted(TuiState state, string text)
    {
        if (state.Input == null) return state;

        state.Input.OnSubmit(text);

        return state with { Input = null };
    }

    #endregion

    #region Wizard Handlers

    private async Task<TuiState> HandleLaunchCdnSetupWizardAsync(TuiState state, CancellationToken token)
    {
        // Mark wizard as active so input handling is paused
        state = state with { IsWizardActive = true };

        // TODO: Run CdnSetupWizard here
        // For now, just show a message
        await Task.Delay(100, token);

        return state with
        {
            IsWizardActive = false,
            Status = new StatusMessage
            {
                Text = "CDN wizard would launch here",
                Type = StatusType.Info,
                Duration = TimeSpan.FromSeconds(3)
            }
        };
    }

    private static TuiState HandleWizardCompleted(TuiState state)
    {
        return state with
        {
            IsWizardActive = false,
            Status = new StatusMessage
            {
                Text = "Configuration saved",
                Type = StatusType.Success,
                Duration = TimeSpan.FromSeconds(3)
            }
        };
    }

    private static TuiState HandleWizardCancelled(TuiState state)
    {
        return state with
        {
            IsWizardActive = false,
            Status = new StatusMessage
            {
                Text = "Configuration cancelled",
                Type = StatusType.Warning,
                Duration = TimeSpan.FromSeconds(2)
            }
        };
    }

    #endregion

    #region Application Lifecycle Handlers

    private static TuiState HandleRequestQuit(TuiState state)
    {
        // If there's unsaved state, show confirmation
        // For now, just quit directly
        return state with { IsRunning = false };
    }

    #endregion

    #region Build Handlers

    private async Task<TuiState> HandleStartBuildAsync(TuiState state, string inputPath, string? baseVersion, CancellationToken token)
    {
        // Navigate to build screen and start build
        state = state with
        {
            CurrentScreen = TuiScreen.Build,
            ScreenHistory = state.ScreenHistory.Append(state.CurrentScreen).ToList(),
            Build = new BuildState
            {
                IsBuilding = true,
                StartTime = DateTime.UtcNow,
                BaseVersion = baseVersion
            }
        };

        // TODO: Integrate with ParallelSignatureBuilder
        return state;
    }

    private static TuiState HandleCancelBuild(TuiState state)
    {
        // TODO: Actually cancel the build operation
        return state with
        {
            Build = state.Build with
            {
                IsBuilding = false,
                Error = "Build cancelled by user"
            }
        };
    }

    private static TuiState HandleBuildProgress(TuiState state, int processed, int total, string currentFile, double percentage)
    {
        return state with
        {
            Build = state.Build with
            {
                ProcessedFiles = processed,
                TotalFiles = total,
                CurrentFile = currentFile,
                Percentage = percentage
            }
        };
    }

    private static TuiState HandleBuildCompleted(TuiState state, TimeSpan duration, int fileCount, long totalSize)
    {
        return state with
        {
            Build = state.Build with
            {
                IsBuilding = false,
                IsCompleted = true,
                TotalFiles = fileCount
            },
            Status = new StatusMessage
            {
                Text = $"Build completed: {fileCount} files",
                Type = StatusType.Success,
                Duration = TimeSpan.FromSeconds(5)
            }
        };
    }

    private static TuiState HandleBuildFailed(TuiState state, string error)
    {
        return state with
        {
            Build = state.Build with
            {
                IsBuilding = false,
                Error = error
            }
        };
    }

    #endregion

    #region Publish Handlers

    private async Task<TuiState> HandleStartPublishAsync(TuiState state, CancellationToken token)
    {
        state = state with
        {
            CurrentScreen = TuiScreen.Publish,
            ScreenHistory = state.ScreenHistory.Append(state.CurrentScreen).ToList(),
            Publish = new PublishState
            {
                IsConfirming = true,
                TotalFiles = state.FileList.AllFiles.Count,
                TotalBytes = state.FileList.AllFiles.Sum(f => f.Size)
            }
        };

        return state;
    }

    private static TuiState HandleCancelPublish(TuiState state)
    {
        return state with
        {
            Publish = new PublishState()
        };
    }

    private static TuiState HandlePublishProgress(TuiState state, int uploadedFiles, int totalFiles,
        long uploadedBytes, long totalBytes, string currentFile)
    {
        return state with
        {
            Publish = state.Publish with
            {
                UploadedFiles = uploadedFiles,
                TotalFiles = totalFiles,
                UploadedBytes = uploadedBytes,
                TotalBytes = totalBytes,
                CurrentFile = currentFile
            }
        };
    }

    private static TuiState HandlePublishCompleted(TuiState state, string manifestUrl)
    {
        return state with
        {
            Publish = state.Publish with
            {
                IsPublishing = false,
                ManifestUrl = manifestUrl
            },
            Status = new StatusMessage
            {
                Text = "Publish completed",
                Type = StatusType.Success,
                Duration = TimeSpan.FromSeconds(5)
            }
        };
    }

    private static TuiState HandlePublishFailed(TuiState state, string error, int failedFiles)
    {
        return state with
        {
            Publish = state.Publish with
            {
                IsPublishing = false,
                Error = error,
                FailedFiles = failedFiles
            }
        };
    }

    #endregion

    #region Data Loading

    private async Task LoadFilesAsync(CancellationToken token)
    {
        try
        {
            var channel = _state.Workspace.CurrentChannel;
            var version = _state.Workspace.CurrentVersion;

            if (string.IsNullOrEmpty(channel) || string.IsNullOrEmpty(version))
            {
                _state = _state with
                {
                    FileList = new FileListState()
                };
                return;
            }

            var manifestPath = Path.Combine(
                _workspace.WorkspacePath,
                ".patchsync",
                "channels",
                channel,
                version,
                "manifest.json");

            if (!File.Exists(manifestPath))
            {
                _state = _state with
                {
                    FileList = new FileListState()
                };
                return;
            }

            await using var stream = File.OpenRead(manifestPath);
            var manifest = await System.Text.Json.JsonSerializer.DeserializeAsync(
                stream,
                ManifestJsonContext.Default.GameManifest,
                token);

            if (manifest == null)
            {
                _state = _state with
                {
                    FileList = new FileListState()
                };
                return;
            }

            var files = manifest.Files
                .Select(f => new FileListItem
                {
                    Path = f.Path,
                    Size = f.Size,
                    Hash = f.Hash,
                    Strategy = f.Strategy,
                    BaseHash = f.BaseHash
                })
                .OrderBy(f => f.Path)
                .ToList();

            _state = _state with
            {
                FileList = new FileListState
                {
                    AllFiles = files,
                    FilteredFiles = files,
                    VisibleCount = _state.Terminal.MaxVisibleFiles
                }
            };
        }
        catch (Exception ex)
        {
            _state = _state with
            {
                Status = new StatusMessage
                {
                    Text = $"Error loading files: {ex.Message}",
                    Type = StatusType.Error,
                    Duration = TimeSpan.FromSeconds(5)
                }
            };
        }
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _cts.Dispose();
        _messageChannel.Writer.Complete();
    }
}

