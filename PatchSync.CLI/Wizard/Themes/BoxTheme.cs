using Spectre.Console;

namespace PatchSync.CLI.Wizard.Themes;

/// <summary>
/// Clean bordered panel theme using Spectre.Console Panel.
/// Professional look with rounded corners.
/// </summary>
public sealed class BoxTheme : IWizardTheme
{
    public string Name => "Box";

    public string BackMarker => ":left_arrow: Back";
    public string CancelMarker => ":cross_mark: Cancel";
    public string Separator => ""; // Empty - separators can't be disabled in Spectre.Console
    public string NavigationHint => "[grey](type 'back' or 'cancel')[/]";

    public Style HighlightStyle => new(Color.Cyan1);
    public Style DimStyle => new(Color.Grey);
    public Style AccentStyle => new(Color.Blue);
    public Style PromptStyle => new(Color.Green);

    public int PageSize => 10;

    public void RenderHeader(string wizardTitle, int currentStep, int totalSteps, string stepName)
    {
        // Use Spectre.Console Panel for a clean, proper box
        var stepText = $"[grey]Step {currentStep + 1} of {totalSteps}[/]";
        var headerContent = $"[bold blue]{wizardTitle.ToUpperInvariant()}[/]  {stepText}\n[cyan]{Markup.Escape(stepName)}[/]";

        var panel = new Panel(headerContent)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Blue)
            .Padding(1, 0);

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    public void RenderFooter(bool canGoBack)
    {
        // Footer is not typically called - navigation is in the prompts themselves
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
        AnsiConsole.WriteLine();
        var panel = new Panel("[yellow]Wizard cancelled.[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Yellow)
            .Padding(1, 0);
        AnsiConsole.Write(panel);
    }

    public void ShowCompleted(string message)
    {
        AnsiConsole.WriteLine();
        var panel = new Panel($":check_mark_button: [green]{Markup.Escape(message)}[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Green)
            .Padding(1, 0);
        AnsiConsole.Write(panel);
    }
}
