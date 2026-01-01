using Spectre.Console;

namespace PatchSync.CLI.Wizard;

/// <summary>
/// Theme interface for wizard rendering.
/// Separates visual presentation from wizard logic.
/// </summary>
public interface IWizardTheme
{
    /// <summary>Theme display name.</summary>
    string Name { get; }

    /// <summary>
    /// Render the wizard header/frame before a step.
    /// </summary>
    void RenderHeader(string wizardTitle, int currentStep, int totalSteps, string stepName, string breadcrumb);

    /// <summary>
    /// Render navigation hint (Ctrl+C to go back/cancel).
    /// </summary>
    void RenderNavigationHint(bool isFirstStep);

    /// <summary>
    /// Clear the frame between steps.
    /// </summary>
    void ClearFrame();

    /// <summary>
    /// Style for highlighted/selected items.
    /// </summary>
    Style HighlightStyle { get; }

    /// <summary>
    /// Style for dimmed/secondary text.
    /// </summary>
    Style DimStyle { get; }

    /// <summary>
    /// Style for accent/brand color.
    /// </summary>
    Style AccentStyle { get; }

    /// <summary>
    /// Style for prompts.
    /// </summary>
    Style PromptStyle { get; }

    /// <summary>
    /// Page size for selection prompts.
    /// </summary>
    int PageSize { get; }

    /// <summary>
    /// Format a prompt title with theme styling.
    /// </summary>
    string FormatPrompt(string prompt);

    /// <summary>
    /// Show a cancellation message.
    /// </summary>
    void ShowCancelled();

    /// <summary>
    /// Show a completion message.
    /// </summary>
    void ShowCompleted(string message);
}

/// <summary>
/// Static accessor for the default theme.
/// </summary>
public static class WizardTheme
{
    private static IWizardTheme? _default;

    /// <summary>
    /// Default theme used when none is specified.
    /// </summary>
    public static IWizardTheme Default
    {
        get => _default ??= new Themes.DefaultTheme();
        set => _default = value;
    }
}
