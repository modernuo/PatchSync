using System.Text;
using Spectre.Console;

namespace PatchSync.CLI.Wizard.Themes;

/// <summary>
/// Modern minimal theme with progress bar.
/// Clean lines with visual progress indicator.
/// </summary>
public sealed class ModernTheme : IWizardTheme
{
    public string Name => "Modern";

    public Style HighlightStyle => new(Color.Cyan1);
    public Style DimStyle => new(Color.Grey);
    public Style AccentStyle => new(Color.Blue);
    public Style PromptStyle => new(Color.White);

    public int PageSize => 10;

    public void RenderHeader(string wizardTitle, int currentStep, int totalSteps, string stepName, string breadcrumb)
    {
        var width = Math.Min(Console.WindowWidth - 2, 70);

        // Top bar
        AnsiConsole.MarkupLine($"[grey]{new string('━', width)}[/]");

        // Title
        AnsiConsole.MarkupLine($"  [bold white]{Markup.Escape(wizardTitle)}[/]");

        // Progress bar
        var progress = (double)(currentStep) / totalSteps;
        var barWidth = width - 25;
        var filled = (int)(progress * barWidth);
        var empty = barWidth - filled;

        var bar = new StringBuilder();
        bar.Append("[cyan]");
        bar.Append('█', filled);
        bar.Append("[/][grey]");
        bar.Append('░', empty);
        bar.Append("[/]");

        AnsiConsole.MarkupLine($"  {bar} [grey]{Markup.Escape(breadcrumb)}[/]");

        // Bottom bar
        AnsiConsole.MarkupLine($"[grey]{new string('━', width)}[/]");
    }

    public void RenderNavigationHint(bool isFirstStep)
    {
        var hint = isFirstStep
            ? "  [grey]Ctrl+C to cancel[/]"
            : "  [grey]Ctrl+C to go back[/]";
        AnsiConsole.MarkupLine(hint);
        AnsiConsole.WriteLine();
    }

    public void ClearFrame()
    {
        AnsiConsole.Clear();
    }

    public string FormatPrompt(string prompt)
    {
        return $"  [white]{Markup.Escape(prompt)}[/]";
    }

    public void ShowCancelled()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [yellow]━━ Wizard cancelled ━━[/]");
    }

    public void ShowCompleted(string message)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"  [green]━━ :check_mark_button: {Markup.Escape(message)} ━━[/]");
    }
}
