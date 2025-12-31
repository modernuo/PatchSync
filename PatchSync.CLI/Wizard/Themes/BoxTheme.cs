using Spectre.Console;

namespace PatchSync.CLI.Wizard.Themes;

/// <summary>
/// Clean bordered panel theme.
/// Professional look with rounded corners.
/// </summary>
public sealed class BoxTheme : IWizardTheme
{
    public string Name => "Box";

    public string BackMarker => ":left_arrow: Back";
    public string CancelMarker => ":cross_mark: Cancel";
    public string Separator => "────────────────────";
    public string NavigationHint => "[grey](type 'back' or 'cancel')[/]";

    public Style HighlightStyle => new(Color.Cyan1);
    public Style DimStyle => new(Color.Grey);
    public Style AccentStyle => new(Color.Blue);
    public Style PromptStyle => new(Color.Green);

    public int PageSize => 10;

    public void RenderHeader(string wizardTitle, int currentStep, int totalSteps, string stepName)
    {
        var width = Math.Min(Console.WindowWidth - 4, 70);
        var titleText = wizardTitle.ToUpperInvariant();
        var stepText = $"Step {currentStep + 1} of {totalSteps}";
        var padding = width - titleText.Length - stepText.Length - 4;

        AnsiConsole.MarkupLine($"[blue]╭{new string('─', width)}╮[/]");
        AnsiConsole.MarkupLine($"[blue]│[/]  [bold blue]{titleText}[/]{new string(' ', Math.Max(1, padding))}[grey]{stepText}[/]  [blue]│[/]");
        AnsiConsole.MarkupLine($"[blue]│[/]  [cyan]{Markup.Escape(stepName)}[/]{new string(' ', width - stepName.Length - 2)}[blue]│[/]");
        AnsiConsole.MarkupLine($"[blue]├{new string('─', width)}┤[/]");
        AnsiConsole.MarkupLine($"[blue]│[/]{new string(' ', width)}[blue]│[/]");
    }

    public void RenderFooter(bool canGoBack)
    {
        var width = Math.Min(Console.WindowWidth - 4, 70);
        AnsiConsole.MarkupLine($"[blue]│[/]{new string(' ', width)}[blue]│[/]");
        AnsiConsole.MarkupLine($"[blue]╰{new string('─', width)}╯[/]");

        if (canGoBack)
        {
            AnsiConsole.MarkupLine("[grey]  ← Back                                              Cancel ✕[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[grey]                                                      Cancel ✕[/]");
        }
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
        AnsiConsole.MarkupLine("[yellow]╭───────────────────────────╮[/]");
        AnsiConsole.MarkupLine("[yellow]│[/]   Wizard cancelled.      [yellow]│[/]");
        AnsiConsole.MarkupLine("[yellow]╰───────────────────────────╯[/]");
    }

    public void ShowCompleted(string message)
    {
        AnsiConsole.WriteLine();
        var width = Math.Max(message.Length + 6, 30);
        AnsiConsole.MarkupLine($"[green]╭{new string('─', width)}╮[/]");
        AnsiConsole.MarkupLine($"[green]│[/] :check_mark_button: {Markup.Escape(message)}{new string(' ', width - message.Length - 4)}[green]│[/]");
        AnsiConsole.MarkupLine($"[green]╰{new string('─', width)}╯[/]");
    }
}
