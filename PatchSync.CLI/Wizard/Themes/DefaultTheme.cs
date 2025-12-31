using Spectre.Console;

namespace PatchSync.CLI.Wizard.Themes;

/// <summary>
/// Simple, minimal theme with no frills.
/// Good for testing and basic usage.
/// </summary>
public sealed class DefaultTheme : IWizardTheme
{
    public string Name => "Default";

    public string BackMarker => ":left_arrow: Back";
    public string CancelMarker => ":cross_mark: Cancel";
    public string Separator => "───────────────────────────";
    public string NavigationHint => "[grey](type 'back' or 'cancel')[/]";

    public Style HighlightStyle => new(Color.Cyan1);
    public Style DimStyle => new(Color.Grey);
    public Style AccentStyle => new(Color.Blue);
    public Style PromptStyle => new(Color.Green);

    public int PageSize => 12;

    public void RenderHeader(string wizardTitle, int currentStep, int totalSteps, string stepName)
    {
        AnsiConsole.MarkupLine($"[blue]{Markup.Escape(wizardTitle)}[/]");
        AnsiConsole.MarkupLine($"[grey]Step {currentStep + 1} of {totalSteps}: {Markup.Escape(stepName)}[/]");
        AnsiConsole.WriteLine();
    }

    public void RenderFooter(bool canGoBack)
    {
        // No footer in default theme
    }

    public void ClearFrame()
    {
        AnsiConsole.Clear();
    }

    public string FormatPrompt(string prompt)
    {
        return $"[green]{Markup.Escape(prompt)}[/]";
    }

    public void ShowCancelled()
    {
        AnsiConsole.MarkupLine("[yellow]Wizard cancelled.[/]");
    }

    public void ShowCompleted(string message)
    {
        AnsiConsole.MarkupLine($"[green]:check_mark_button: {Markup.Escape(message)}[/]");
    }
}
