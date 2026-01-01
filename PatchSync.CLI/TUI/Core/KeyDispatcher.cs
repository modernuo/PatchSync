namespace PatchSync.CLI.TUI.Core;

/// <summary>
/// Dispatches keyboard input to appropriate TUI messages.
/// Supports Vim-style navigation (h/j/k/l) and standard keys.
/// </summary>
public sealed class KeyDispatcher
{
    private readonly Dictionary<ConsoleKey, Func<TuiState, ITuiMessage?>> _globalBindings = new();
    private readonly Dictionary<TuiScreen, Dictionary<ConsoleKey, Func<TuiState, ITuiMessage?>>> _screenBindings = new();
    private readonly Dictionary<ConsoleKey, Func<TuiState, ConsoleModifiers, ITuiMessage?>> _modifierBindings = new();

    public KeyDispatcher()
    {
        SetupDefaultBindings();
    }

    private void SetupDefaultBindings()
    {
        // Navigation - Vim style
        _globalBindings[ConsoleKey.J] = _ => new NavigateDown();
        _globalBindings[ConsoleKey.K] = _ => new NavigateUp();
        _globalBindings[ConsoleKey.DownArrow] = _ => new NavigateDown();
        _globalBindings[ConsoleKey.UpArrow] = _ => new NavigateUp();

        // Page navigation
        _globalBindings[ConsoleKey.PageDown] = _ => new PageDown();
        _globalBindings[ConsoleKey.PageUp] = _ => new PageUp();

        // First/Last
        _globalBindings[ConsoleKey.Home] = _ => new NavigateFirst();
        _globalBindings[ConsoleKey.End] = _ => new NavigateLast();

        // Panel switching
        _globalBindings[ConsoleKey.Tab] = _ => new NextPanel();
        _globalBindings[ConsoleKey.H] = state =>
            state.CurrentScreen == TuiScreen.Dashboard ? new PreviousPanel() : null;
        _globalBindings[ConsoleKey.L] = state =>
            state.CurrentScreen == TuiScreen.Dashboard ? new NextPanel() : null;
        _globalBindings[ConsoleKey.LeftArrow] = state =>
            state.CurrentScreen == TuiScreen.Dashboard ? new PreviousPanel() : null;
        _globalBindings[ConsoleKey.RightArrow] = state =>
            state.CurrentScreen == TuiScreen.Dashboard ? new NextPanel() : null;

        // Selection
        _globalBindings[ConsoleKey.Enter] = _ => new SelectItem();
        _globalBindings[ConsoleKey.Spacebar] = _ => new ToggleSelection();

        // Actions
        _globalBindings[ConsoleKey.S] = _ => new OpenStrategyDialog();
        _globalBindings[ConsoleKey.D] = _ => new RemoveFiles();
        _globalBindings[ConsoleKey.R] = _ => new RefreshFileList();

        // Filter
        _globalBindings[ConsoleKey.Divide] = _ => new OpenFilter(); // '/' key
        _globalBindings[ConsoleKey.Oem2] = _ => new OpenFilter(); // '/' on US keyboards

        // Screen navigation
        _globalBindings[ConsoleKey.D1] = _ => new GoToDashboard();
        _globalBindings[ConsoleKey.D2] = _ => new GoToVersions();
        _globalBindings[ConsoleKey.D3] = _ => new GoToSettings();

        // Back/Escape
        _globalBindings[ConsoleKey.Escape] = state =>
        {
            if (state.Dialog != null) return new CloseDialog();
            if (state.Input != null) return new ExitInputMode();
            if (!string.IsNullOrEmpty(state.FileList.Filter)) return new ClearFilter();
            if (state.ScreenHistory.Count > 0) return new GoBack();
            return new RequestQuit();
        };

        // Quit
        _globalBindings[ConsoleKey.Q] = state =>
            state.Dialog == null && state.Input == null ? new RequestQuit() : null;

        // Modifier bindings
        _modifierBindings[ConsoleKey.A] = (state, modifiers) =>
            modifiers.HasFlag(ConsoleModifiers.Control) ? new SelectAll() : null;

        _modifierBindings[ConsoleKey.G] = (state, modifiers) =>
        {
            if (modifiers.HasFlag(ConsoleModifiers.Shift))
                return new NavigateLast();
            return new NavigateFirst();
        };

        _modifierBindings[ConsoleKey.D] = (state, modifiers) =>
            modifiers.HasFlag(ConsoleModifiers.Control) ? new PageDown() : null;

        _modifierBindings[ConsoleKey.U] = (state, modifiers) =>
            modifiers.HasFlag(ConsoleModifiers.Control) ? new PageUp() : null;

        // Screen-specific bindings
        SetupDashboardBindings();
        SetupVersionsBindings();
        SetupBuildBindings();
        SetupSettingsBindings();
    }

    private void SetupDashboardBindings()
    {
        var bindings = new Dictionary<ConsoleKey, Func<TuiState, ITuiMessage?>>();

        bindings[ConsoleKey.B] = _ => new StartBuild(string.Empty);
        bindings[ConsoleKey.P] = _ => new StartPublish();
        bindings[ConsoleKey.A] = state => state.FileList.SelectedFile != null ? new AddFile(string.Empty) : null;

        _screenBindings[TuiScreen.Dashboard] = bindings;
    }

    private void SetupVersionsBindings()
    {
        var bindings = new Dictionary<ConsoleKey, Func<TuiState, ITuiMessage?>>();

        bindings[ConsoleKey.N] = _ => new CreateVersion(string.Empty);
        bindings[ConsoleKey.P] = state =>
            !string.IsNullOrEmpty(state.Workspace.CurrentVersion)
                ? new PromoteVersion(string.Empty)
                : null;

        _screenBindings[TuiScreen.Versions] = bindings;
    }

    private void SetupBuildBindings()
    {
        var bindings = new Dictionary<ConsoleKey, Func<TuiState, ITuiMessage?>>();

        bindings[ConsoleKey.C] = state =>
            state.Build.IsBuilding ? new CancelBuild() : null;

        _screenBindings[TuiScreen.Build] = bindings;
    }

    private void SetupSettingsBindings()
    {
        var bindings = new Dictionary<ConsoleKey, Func<TuiState, ITuiMessage?>>();

        bindings[ConsoleKey.C] = _ => new LaunchCdnSetupWizard();

        _screenBindings[TuiScreen.Settings] = bindings;
    }

    /// <summary>
    /// Dispatch a key press to the appropriate message.
    /// </summary>
    public ITuiMessage? Dispatch(ConsoleKeyInfo keyInfo, TuiState state)
    {
        // Handle input mode specially
        if (state.Input != null)
        {
            return HandleInputMode(keyInfo, state);
        }

        // Handle dialog mode specially
        if (state.Dialog != null)
        {
            return HandleDialogMode(keyInfo, state);
        }

        // Check modifier bindings first
        if (keyInfo.Modifiers != 0)
        {
            if (_modifierBindings.TryGetValue(keyInfo.Key, out var modifierHandler))
            {
                var result = modifierHandler(state, keyInfo.Modifiers);
                if (result != null) return result;
            }
        }

        // Check screen-specific bindings
        if (_screenBindings.TryGetValue(state.CurrentScreen, out var screenBindings))
        {
            if (screenBindings.TryGetValue(keyInfo.Key, out var screenHandler))
            {
                var result = screenHandler(state);
                if (result != null) return result;
            }
        }

        // Check global bindings
        if (_globalBindings.TryGetValue(keyInfo.Key, out var globalHandler))
        {
            return globalHandler(state);
        }

        return null;
    }

    private static ITuiMessage? HandleInputMode(ConsoleKeyInfo keyInfo, TuiState state)
    {
        switch (keyInfo.Key)
        {
            case ConsoleKey.Enter:
                return new InputSubmitted(state.Input!.Text);

            case ConsoleKey.Escape:
                return new ExitInputMode();

            case ConsoleKey.Backspace:
                if (state.Input!.Text.Length > 0)
                {
                    var newText = state.Input.Text[..^1];
                    return new InputChanged(newText);
                }
                return null;

            default:
                // Handle printable characters
                if (!char.IsControl(keyInfo.KeyChar) && keyInfo.KeyChar != '\0')
                {
                    var newText = state.Input!.Text + keyInfo.KeyChar;
                    return new InputChanged(newText);
                }
                return null;
        }
    }

    private static ITuiMessage? HandleDialogMode(ConsoleKeyInfo keyInfo, TuiState state)
    {
        var dialog = state.Dialog!;

        switch (keyInfo.Key)
        {
            case ConsoleKey.Enter:
            case ConsoleKey.Y:
                return dialog.Type == DialogType.Confirm
                    ? new DialogConfirmed()
                    : new CloseDialog();

            case ConsoleKey.Escape:
            case ConsoleKey.N:
                return dialog.Type == DialogType.Confirm
                    ? new DialogCancelled()
                    : new CloseDialog();

            case ConsoleKey.UpArrow:
            case ConsoleKey.K:
                if (dialog.Choices != null && dialog.SelectedChoice > 0)
                {
                    // Would need to update dialog selection
                    return null;
                }
                return null;

            case ConsoleKey.DownArrow:
            case ConsoleKey.J:
                if (dialog.Choices != null && dialog.SelectedChoice < dialog.Choices.Count - 1)
                {
                    // Would need to update dialog selection
                    return null;
                }
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Get help text for available key bindings.
    /// </summary>
    public static IEnumerable<(string Key, string Description)> GetHelpBindings(TuiScreen screen)
    {
        yield return ("j/k or ↑/↓", "Navigate");
        yield return ("Enter", "Select");
        yield return ("Tab", "Switch panel");
        yield return ("/", "Filter");
        yield return ("Esc", "Back/Cancel");
        yield return ("q", "Quit");

        switch (screen)
        {
            case TuiScreen.Dashboard:
                yield return ("s", "Change strategy");
                yield return ("d", "Delete file");
                yield return ("b", "Build");
                yield return ("p", "Publish");
                break;

            case TuiScreen.Versions:
                yield return ("n", "New version");
                yield return ("p", "Promote");
                break;

            case TuiScreen.Build:
                yield return ("c", "Cancel build");
                break;

            case TuiScreen.Settings:
                yield return ("c", "CDN setup");
                break;
        }
    }

    /// <summary>
    /// Get a compact action bar string for the current context.
    /// </summary>
    public static string GetActionBar(TuiState state)
    {
        var actions = new List<string>();

        if (state.Dialog != null)
        {
            if (state.Dialog.Type == DialogType.Confirm)
            {
                actions.Add("[y] Yes");
                actions.Add("[n] No");
            }
            else
            {
                actions.Add("[Enter] OK");
            }
            return string.Join("  ", actions);
        }

        if (state.Input != null)
        {
            actions.Add("[Enter] Submit");
            actions.Add("[Esc] Cancel");
            return string.Join("  ", actions);
        }

        actions.Add("[j/k] nav");
        actions.Add("[Enter] select");

        switch (state.CurrentScreen)
        {
            case TuiScreen.Dashboard:
                if (state.FileList.SelectedFile != null)
                {
                    actions.Add("[s] strategy");
                }
                if (state.Workspace.CurrentVersion != null)
                {
                    actions.Add("[p] publish");
                }
                break;

            case TuiScreen.Versions:
                actions.Add("[n] new");
                if (state.Workspace.CurrentVersion != null)
                {
                    actions.Add("[p] promote");
                }
                break;
        }

        actions.Add("[Tab] panel");
        actions.Add("[q] quit");

        return string.Join("  ", actions);
    }
}
