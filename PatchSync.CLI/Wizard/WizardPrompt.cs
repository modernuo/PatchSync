using PatchSync.CLI.Prompts;
using Spectre.Console;

namespace PatchSync.CLI.Wizard;

/// <summary>
/// Theme-aware prompt wrappers with back/cancel navigation support.
/// Uses ShowAsync with cancellation token so Ctrl+C triggers OperationCanceledException.
/// </summary>
public static class WizardPrompt
{
    /// <summary>
    /// Show a selection prompt. Ctrl+C throws OperationCanceledException (handled by WizardRunner).
    /// </summary>
    public static async Task<WizardResult<string>> SelectionAsync(
        string title,
        IEnumerable<string> choices,
        IWizardTheme theme)
    {
        var prompt = new SelectionPrompt<string>()
            .Title(theme.FormatPrompt(title))
            .PageSize(theme.PageSize)
            .HighlightStyle(theme.HighlightStyle)
            .AddChoices(choices);

        var result = await prompt.ShowAsync(AnsiConsole.Console, InteractiveCancellation.Instance.Token);
        return WizardResult<string>.Success(result);
    }

    /// <summary>
    /// Show a text prompt. Ctrl+C throws OperationCanceledException (handled by WizardRunner).
    /// </summary>
    public static async Task<WizardResult<string>> TextAsync(
        string title,
        IWizardTheme theme,
        string? defaultValue = null,
        bool allowEmpty = false,
        Func<string, ValidationResult>? validator = null)
    {
        var prompt = new TextPrompt<string>($"{theme.FormatPrompt(title)}:");

        if (defaultValue != null)
            prompt.DefaultValue(defaultValue);

        if (allowEmpty)
            prompt.AllowEmpty();

        if (validator != null)
        {
            prompt.Validate(input =>
            {
                if (!allowEmpty && string.IsNullOrWhiteSpace(input))
                    return ValidationResult.Error("Value is required");
                return validator(input);
            });
        }
        else if (!allowEmpty)
        {
            prompt.Validate(input =>
                string.IsNullOrWhiteSpace(input)
                    ? ValidationResult.Error("Value is required")
                    : ValidationResult.Success());
        }

        var result = await prompt.ShowAsync(AnsiConsole.Console, InteractiveCancellation.Instance.Token);
        return WizardResult<string>.Success(result);
    }

    /// <summary>
    /// Show a secret (password) prompt. Ctrl+C throws OperationCanceledException (handled by WizardRunner).
    /// </summary>
    public static async Task<WizardResult<string>> SecretAsync(
        string title,
        IWizardTheme theme,
        Func<string, ValidationResult>? validator = null)
    {
        var prompt = new TextPrompt<string>($"{theme.FormatPrompt(title)}:")
            .Secret();

        prompt.Validate(input =>
        {
            if (string.IsNullOrWhiteSpace(input))
                return ValidationResult.Error("Value is required");
            return validator?.Invoke(input) ?? ValidationResult.Success();
        });

        var result = await prompt.ShowAsync(AnsiConsole.Console, InteractiveCancellation.Instance.Token);
        return WizardResult<string>.Success(result);
    }

    /// <summary>
    /// Show a numeric text prompt. Ctrl+C throws OperationCanceledException (handled by WizardRunner).
    /// </summary>
    public static async Task<WizardResult<int>> NumberAsync(
        string title,
        IWizardTheme theme,
        int? defaultValue = null,
        Func<int, ValidationResult>? validator = null)
    {
        var prompt = new TextPrompt<string>($"{theme.FormatPrompt(title)}:");

        if (defaultValue.HasValue)
            prompt.DefaultValue(defaultValue.Value.ToString());

        prompt.Validate(input =>
        {
            if (!int.TryParse(input, out var num))
                return ValidationResult.Error("Please enter a valid number");
            return validator?.Invoke(num) ?? ValidationResult.Success();
        });

        var result = await prompt.ShowAsync(AnsiConsole.Console, InteractiveCancellation.Instance.Token);
        return WizardResult<int>.Success(int.Parse(result));
    }

    /// <summary>
    /// Show a confirmation prompt. Ctrl+C throws OperationCanceledException (handled by WizardRunner).
    /// </summary>
    public static async Task<WizardResult<bool>> ConfirmAsync(
        string question,
        IWizardTheme theme,
        bool defaultValue = true)
    {
        var choices = new List<string>();

        // Order based on default
        if (defaultValue)
        {
            choices.Add(":check_mark: Yes");
            choices.Add(":cross_mark_button: No");
        }
        else
        {
            choices.Add(":cross_mark_button: No");
            choices.Add(":check_mark: Yes");
        }

        var prompt = new SelectionPrompt<string>()
            .Title(theme.FormatPrompt(question))
            .HighlightStyle(theme.HighlightStyle)
            .AddChoices(choices);

        var result = await prompt.ShowAsync(AnsiConsole.Console, InteractiveCancellation.Instance.Token);
        return WizardResult<bool>.Success(result.Contains("Yes"));
    }

    /// <summary>
    /// Show a multi-selection prompt. Ctrl+C throws OperationCanceledException (handled by WizardRunner).
    /// </summary>
    public static async Task<WizardResult<List<string>>> MultiSelectAsync(
        string title,
        IEnumerable<string> choices,
        IWizardTheme theme,
        IEnumerable<string>? preselected = null,
        bool required = true)
    {
        var choiceList = choices.ToList();

        var multiPrompt = new MultiSelectionPrompt<string>()
            .Title(theme.FormatPrompt(title))
            .PageSize(theme.PageSize)
            .HighlightStyle(theme.HighlightStyle)
            .InstructionsText("[grey](Space to toggle, Enter to confirm)[/]")
            .AddChoices(choiceList);

        if (!required)
            multiPrompt.NotRequired();

        // Pre-select items
        if (preselected != null)
        {
            foreach (var item in preselected.Where(p => choiceList.Contains(p)))
                multiPrompt.Select(item);
        }

        var selected = await multiPrompt.ShowAsync(AnsiConsole.Console, InteractiveCancellation.Instance.Token);

        // If required and nothing selected, re-prompt
        if (required && selected.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Please select at least one item.[/]");
            return await MultiSelectAsync(title, choices, theme, preselected, required);
        }

        return WizardResult<List<string>>.Success(selected);
    }

    /// <summary>
    /// Browse for a folder. Ctrl+C throws OperationCanceledException (handled by WizardRunner).
    /// </summary>
    public static async Task<WizardResult<string>> BrowseFolderAsync(
        string title,
        IWizardTheme theme,
        string? startPath = null,
        bool allowNew = false)
    {
        var navChoices = new List<string>
        {
            ":file_folder: Browse for folder",
            ":keyboard: Enter path manually"
        };

        var navPrompt = new SelectionPrompt<string>()
            .Title(theme.FormatPrompt(title))
            .HighlightStyle(theme.HighlightStyle)
            .AddChoices(navChoices);

        var navResult = await navPrompt.ShowAsync(AnsiConsole.Console, InteractiveCancellation.Instance.Token);

        if (navResult.Contains("Browse"))
        {
            var path = await Browse.ForFolderAsync(title, startPath, allowNew, InteractiveCancellation.Instance.Token);
            return WizardResult<string>.Success(path);
        }
        else
        {
            return await TextAsync("Enter path", theme, startPath,
                validator: path =>
                {
                    if (string.IsNullOrWhiteSpace(path))
                        return ValidationResult.Error("Path is required");
                    if (!allowNew && !Directory.Exists(path))
                        return ValidationResult.Error($"Directory not found: {path}");
                    return ValidationResult.Success();
                });
        }
    }

    /// <summary>
    /// Browse for a file. Ctrl+C throws OperationCanceledException (handled by WizardRunner).
    /// </summary>
    public static async Task<WizardResult<string>> BrowseFileAsync(
        string title,
        IWizardTheme theme,
        string? startPath = null,
        string? pattern = null)
    {
        var navChoices = new List<string>
        {
            ":page_facing_up: Browse for file",
            ":keyboard: Enter path manually"
        };

        var navPrompt = new SelectionPrompt<string>()
            .Title(theme.FormatPrompt(title))
            .HighlightStyle(theme.HighlightStyle)
            .AddChoices(navChoices);

        var navResult = await navPrompt.ShowAsync(AnsiConsole.Console, InteractiveCancellation.Instance.Token);

        if (navResult.Contains("Browse"))
        {
            var path = await Browse.ForFileAsync(title, pattern, startPath, InteractiveCancellation.Instance.Token);
            return WizardResult<string>.Success(path);
        }
        else
        {
            return await TextAsync("Enter file path", theme, startPath,
                validator: path =>
                {
                    if (string.IsNullOrWhiteSpace(path))
                        return ValidationResult.Error("Path is required");
                    if (!File.Exists(path))
                        return ValidationResult.Error($"File not found: {path}");
                    return ValidationResult.Success();
                });
        }
    }

    #region Synchronous wrappers for backward compatibility

    /// <summary>
    /// Show a selection prompt (sync wrapper).
    /// </summary>
    public static WizardResult<string> Selection(
        string title,
        IEnumerable<string> choices,
        IWizardTheme theme)
        => SelectionAsync(title, choices, theme).GetAwaiter().GetResult();

    /// <summary>
    /// Show a text prompt (sync wrapper).
    /// </summary>
    public static WizardResult<string> Text(
        string title,
        IWizardTheme theme,
        string? defaultValue = null,
        bool allowEmpty = false,
        Func<string, ValidationResult>? validator = null)
        => TextAsync(title, theme, defaultValue, allowEmpty, validator).GetAwaiter().GetResult();

    /// <summary>
    /// Show a secret prompt (sync wrapper).
    /// </summary>
    public static WizardResult<string> Secret(
        string title,
        IWizardTheme theme,
        Func<string, ValidationResult>? validator = null)
        => SecretAsync(title, theme, validator).GetAwaiter().GetResult();

    /// <summary>
    /// Show a number prompt (sync wrapper).
    /// </summary>
    public static WizardResult<int> Number(
        string title,
        IWizardTheme theme,
        int? defaultValue = null,
        Func<int, ValidationResult>? validator = null)
        => NumberAsync(title, theme, defaultValue, validator).GetAwaiter().GetResult();

    /// <summary>
    /// Show a confirmation prompt (sync wrapper).
    /// </summary>
    public static WizardResult<bool> Confirm(
        string question,
        IWizardTheme theme,
        bool defaultValue = true)
        => ConfirmAsync(question, theme, defaultValue).GetAwaiter().GetResult();

    /// <summary>
    /// Show a multi-select prompt (sync wrapper).
    /// </summary>
    public static WizardResult<List<string>> MultiSelect(
        string title,
        IEnumerable<string> choices,
        IWizardTheme theme,
        IEnumerable<string>? preselected = null,
        bool required = true)
        => MultiSelectAsync(title, choices, theme, preselected, required).GetAwaiter().GetResult();

    /// <summary>
    /// Browse for folder (sync wrapper).
    /// </summary>
    public static WizardResult<string> BrowseFolder(
        string title,
        IWizardTheme theme,
        string? startPath = null,
        bool allowNew = false)
        => BrowseFolderAsync(title, theme, startPath, allowNew).GetAwaiter().GetResult();

    /// <summary>
    /// Browse for file (sync wrapper).
    /// </summary>
    public static WizardResult<string> BrowseFile(
        string title,
        IWizardTheme theme,
        string? startPath = null,
        string? pattern = null)
        => BrowseFileAsync(title, theme, startPath, pattern).GetAwaiter().GetResult();

    #endregion
}
