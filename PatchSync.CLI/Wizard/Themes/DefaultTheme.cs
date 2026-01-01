using Spectre.Console;

namespace PatchSync.CLI.Wizard.Themes;

/// <summary>
/// Simple, minimal theme with no frills.
/// Good for testing and basic usage.
/// </summary>
public sealed class DefaultTheme : IWizardTheme
{
    public string Name => "Default";

    public Style HighlightStyle => new(Color.Cyan1);
    public Style DimStyle => new(Color.Grey);
    public Style AccentStyle => new(Color.Blue);
    public Style PromptStyle => new(Color.Green);

    public int PageSize => 12;

    public void RenderHeader(string wizardTitle, int currentStep, int totalSteps, string stepName, string breadcrumb)
    {
        AnsiConsole.MarkupLine($"[blue]{Markup.Escape(wizardTitle)}[/]");
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(breadcrumb)}[/]");
    }

    public void RenderNavigationHint(bool isFirstStep)
    {
        var hint = isFirstStep
            ? "[grey]Ctrl+C to cancel[/]"
            : "[grey]Ctrl+C to go back[/]";
        AnsiConsole.MarkupLine(hint);
        AnsiConsole.WriteLine();
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
