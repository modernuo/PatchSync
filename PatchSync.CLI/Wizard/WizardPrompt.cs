using PatchSync.CLI.Prompts;
using Spectre.Console;

namespace PatchSync.CLI.Wizard;

/// <summary>
/// Theme-aware prompt wrappers with back/cancel navigation support.
/// </summary>
public static class WizardPrompt
{
    /// <summary>
    /// Show a selection prompt with back/cancel options.
    /// </summary>
    public static WizardResult<string> Selection(
        string title,
        IEnumerable<string> choices,
        IWizardTheme theme,
        bool allowBack = true)
    {
        var allChoices = new List<string>();

        if (allowBack)
        {
            allChoices.Add(theme.BackMarker);
            if (!string.IsNullOrEmpty(theme.Separator))
                allChoices.Add(theme.Separator);
        }

        allChoices.AddRange(choices);

        if (!string.IsNullOrEmpty(theme.Separator))
            allChoices.Add(theme.Separator);
        allChoices.Add(theme.CancelMarker);

        var prompt = new SelectionPrompt<string>()
            .Title(theme.FormatPrompt(title))
            .PageSize(theme.PageSize)
            .HighlightStyle(theme.HighlightStyle)
            .AddChoices(allChoices);

        // Set disabled style for separator items
        if (!string.IsNullOrEmpty(theme.Separator))
        {
            prompt.DisabledStyle = theme.DimStyle;
        }

        var result = AnsiConsole.Prompt(prompt);

        if (result == theme.BackMarker)
            return WizardResult<string>.Back;
        if (result == theme.CancelMarker)
            return WizardResult<string>.Cancel;
        if (result == theme.Separator)
        {
            // User somehow selected separator, re-prompt
            return Selection(title, choices, theme, allowBack);
        }

        return WizardResult<string>.Success(result);
    }

    /// <summary>
    /// Show a text prompt with back/cancel support via special input.
    /// </summary>
    public static WizardResult<string> Text(
        string title,
        IWizardTheme theme,
        string? defaultValue = null,
        bool allowEmpty = false,
        bool allowBack = true,
        Func<string, ValidationResult>? validator = null)
    {
        var promptText = allowBack
            ? $"{theme.FormatPrompt(title)} {theme.NavigationHint}:"
            : $"{theme.FormatPrompt(title)}:";

        var prompt = new TextPrompt<string>(promptText);

        if (defaultValue != null)
            prompt.DefaultValue(defaultValue);

        if (allowEmpty)
            prompt.AllowEmpty();

        // Wrap validator to allow navigation commands
        prompt.Validate(input =>
        {
            var lower = input.Trim().ToLowerInvariant();
            if (allowBack && (lower == "back" || lower == "b"))
                return ValidationResult.Success();
            if (lower == "cancel" || lower == "c" || lower == "exit" || lower == "quit")
                return ValidationResult.Success();

            if (!allowEmpty && string.IsNullOrWhiteSpace(input))
                return ValidationResult.Error("Value is required");

            return validator?.Invoke(input) ?? ValidationResult.Success();
        });

        var result = AnsiConsole.Prompt(prompt);
        var trimmedLower = result.Trim().ToLowerInvariant();

        if (allowBack && (trimmedLower == "back" || trimmedLower == "b"))
            return WizardResult<string>.Back;
        if (trimmedLower == "cancel" || trimmedLower == "c" || trimmedLower == "exit" || trimmedLower == "quit")
            return WizardResult<string>.Cancel;

        return WizardResult<string>.Success(result);
    }

    /// <summary>
    /// Show a numeric text prompt with back/cancel support.
    /// </summary>
    public static WizardResult<int> Number(
        string title,
        IWizardTheme theme,
        int? defaultValue = null,
        bool allowBack = true,
        Func<int, ValidationResult>? validator = null)
    {
        var promptText = allowBack
            ? $"{theme.FormatPrompt(title)} {theme.NavigationHint}:"
            : $"{theme.FormatPrompt(title)}:";

        var prompt = new TextPrompt<string>(promptText);

        if (defaultValue.HasValue)
            prompt.DefaultValue(defaultValue.Value.ToString());

        prompt.Validate(input =>
        {
            var lower = input.Trim().ToLowerInvariant();
            if (allowBack && (lower == "back" || lower == "b"))
                return ValidationResult.Success();
            if (lower == "cancel" || lower == "c" || lower == "exit" || lower == "quit")
                return ValidationResult.Success();

            if (!int.TryParse(input, out var num))
                return ValidationResult.Error("Please enter a valid number");

            return validator?.Invoke(num) ?? ValidationResult.Success();
        });

        var result = AnsiConsole.Prompt(prompt);
        var trimmedLower = result.Trim().ToLowerInvariant();

        if (allowBack && (trimmedLower == "back" || trimmedLower == "b"))
            return WizardResult<int>.Back;
        if (trimmedLower == "cancel" || trimmedLower == "c" || trimmedLower == "exit" || trimmedLower == "quit")
            return WizardResult<int>.Cancel;

        return WizardResult<int>.Success(int.Parse(result));
    }

    /// <summary>
    /// Show a confirmation prompt with back/cancel options.
    /// Converted to selection for navigation support.
    /// </summary>
    public static WizardResult<bool> Confirm(
        string question,
        IWizardTheme theme,
        bool defaultValue = true,
        bool allowBack = true)
    {
        var choices = new List<string>();

        if (allowBack)
        {
            choices.Add(theme.BackMarker);
            if (!string.IsNullOrEmpty(theme.Separator))
                choices.Add(theme.Separator);
        }

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

        if (!string.IsNullOrEmpty(theme.Separator))
            choices.Add(theme.Separator);
        choices.Add(theme.CancelMarker);

        var prompt = new SelectionPrompt<string>()
            .Title(theme.FormatPrompt(question))
            .HighlightStyle(theme.HighlightStyle)
            .AddChoices(choices);

        var result = AnsiConsole.Prompt(prompt);

        if (result == theme.BackMarker)
            return WizardResult<bool>.Back;
        if (result == theme.CancelMarker)
            return WizardResult<bool>.Cancel;
        if (result == theme.Separator)
            return Confirm(question, theme, defaultValue, allowBack);

        return WizardResult<bool>.Success(result.Contains("Yes"));
    }

    /// <summary>
    /// Show a multi-selection prompt with back/cancel.
    /// </summary>
    public static WizardResult<List<string>> MultiSelect(
        string title,
        IEnumerable<string> choices,
        IWizardTheme theme,
        IEnumerable<string>? preselected = null,
        bool allowBack = true,
        bool required = true)
    {
        // First, show a navigation prompt
        var navChoices = new List<string>();
        if (allowBack)
        {
            navChoices.Add(theme.BackMarker);
            if (!string.IsNullOrEmpty(theme.Separator))
                navChoices.Add(theme.Separator);
        }
        navChoices.Add(":ballot_box_with_check: Make selections");
        if (!string.IsNullOrEmpty(theme.Separator))
            navChoices.Add(theme.Separator);
        navChoices.Add(theme.CancelMarker);

        var navPrompt = new SelectionPrompt<string>()
            .Title(theme.FormatPrompt(title))
            .HighlightStyle(theme.HighlightStyle)
            .AddChoices(navChoices);

        var navResult = AnsiConsole.Prompt(navPrompt);

        if (navResult == theme.BackMarker)
            return WizardResult<List<string>>.Back;
        if (navResult == theme.CancelMarker)
            return WizardResult<List<string>>.Cancel;

        // Show the actual multi-select
        var multiPrompt = new MultiSelectionPrompt<string>()
            .Title("[grey]Space to toggle, Enter to confirm[/]")
            .PageSize(theme.PageSize)
            .HighlightStyle(theme.HighlightStyle)
            .AddChoices(choices);

        if (!required)
            multiPrompt.NotRequired();

        if (preselected != null)
        {
            foreach (var item in preselected)
                multiPrompt.Select(item);
        }

        var selected = AnsiConsole.Prompt(multiPrompt);
        return WizardResult<List<string>>.Success(selected.ToList());
    }

    /// <summary>
    /// Browse for a folder with back/cancel support.
    /// </summary>
    public static WizardResult<string> BrowseFolder(
        string title,
        IWizardTheme theme,
        string? startPath = null,
        bool allowNew = false,
        bool allowBack = true)
    {
        var navChoices = new List<string>();
        if (allowBack)
        {
            navChoices.Add(theme.BackMarker);
            if (!string.IsNullOrEmpty(theme.Separator))
                navChoices.Add(theme.Separator);
        }
        navChoices.Add(":file_folder: Browse for folder");
        navChoices.Add(":keyboard: Enter path manually");
        if (!string.IsNullOrEmpty(theme.Separator))
            navChoices.Add(theme.Separator);
        navChoices.Add(theme.CancelMarker);

        var navResult = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title(theme.FormatPrompt(title))
                .HighlightStyle(theme.HighlightStyle)
                .AddChoices(navChoices));

        if (navResult == theme.BackMarker)
            return WizardResult<string>.Back;
        if (navResult == theme.CancelMarker)
            return WizardResult<string>.Cancel;

        if (navResult.Contains("Browse"))
        {
            var path = Browse.ForFolder(title, startPath, allowNew);
            return WizardResult<string>.Success(path);
        }
        else
        {
            // Manual entry - no back from here (they can just type 'back')
            return Text("Enter path", theme, startPath, allowBack: true,
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
    /// Browse for a file with back/cancel support.
    /// </summary>
    public static WizardResult<string> BrowseFile(
        string title,
        IWizardTheme theme,
        string? startPath = null,
        string? pattern = null,
        bool allowBack = true)
    {
        var navChoices = new List<string>();
        if (allowBack)
        {
            navChoices.Add(theme.BackMarker);
            if (!string.IsNullOrEmpty(theme.Separator))
                navChoices.Add(theme.Separator);
        }
        navChoices.Add(":page_facing_up: Browse for file");
        navChoices.Add(":keyboard: Enter path manually");
        if (!string.IsNullOrEmpty(theme.Separator))
            navChoices.Add(theme.Separator);
        navChoices.Add(theme.CancelMarker);

        var navResult = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title(theme.FormatPrompt(title))
                .HighlightStyle(theme.HighlightStyle)
                .AddChoices(navChoices));

        if (navResult == theme.BackMarker)
            return WizardResult<string>.Back;
        if (navResult == theme.CancelMarker)
            return WizardResult<string>.Cancel;

        if (navResult.Contains("Browse"))
        {
            var path = Browse.ForFile(title, pattern, startPath);
            return WizardResult<string>.Success(path);
        }
        else
        {
            return Text("Enter file path", theme, startPath, allowBack: true,
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
}
