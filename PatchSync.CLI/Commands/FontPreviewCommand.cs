using Spectre.Console;

namespace PatchSync.CLI.Commands;

/// <summary>
/// Preview different Figlet fonts and title styles for the CLI.
/// This is a development/testing command.
/// </summary>
public static class FontPreviewCommand
{
    // Curated high-tech/modern Figlet fonts
    // These are embedded as they're small and avoid file dependencies
    private static readonly Dictionary<string, string> EmbeddedFonts = new()
    {
        ["ANSI Shadow"] = """
            flf2a$ 6 5 16 15 10
            ANSI Shadow by Wikipedia User Wikipedia, edited by Wikipedia User Wikipedia
            http://en.wikipedia.org/wiki/User:Wikipedia
            $
            $
            $
            $
            $
            $@@
            ██╗@
            ╚═╝@
            ██╗@
            ╚═╝@
            $@
            $@@
            """,
        ["Bloody"] = """
            flf2a$ 10 8 22 -1 4
             ▄▄▄▄   @
            ▓█████▄ @
            ▒██▀ ██▌@
            ░██   █▌@
            ░▓█▄   ▌@
             ░▒████▓ @
              ░ ▒▓  @
                ░▒  @
                 ▒  @
                 ░  @@
            """
    };

    /// <summary>
    /// High-tech title style presets
    /// </summary>
    private static readonly (string Name, Action<string> Renderer)[] TitleStyles =
    [
        ("Default Figlet (Blue)", text =>
        {
            AnsiConsole.Write(new FigletText(text).Color(Color.Blue));
        }),

        ("Cyan Gradient", text =>
        {
            AnsiConsole.Write(new FigletText(text).Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[grey]━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━[/]");
        }),

        ("Neon Green (Matrix)", text =>
        {
            AnsiConsole.Write(new FigletText(text).Color(Color.Green));
        }),

        ("Hot Pink (Synthwave)", text =>
        {
            AnsiConsole.Write(new FigletText(text).Color(Color.DeepPink1));
        }),

        ("Orange (Rust/Ember)", text =>
        {
            AnsiConsole.Write(new FigletText(text).Color(Color.Orange1));
        }),

        ("Purple Haze", text =>
        {
            AnsiConsole.Write(new FigletText(text).Color(Color.MediumPurple1));
        }),

        ("Panel - Rounded Blue", text =>
        {
            var panel = new Panel(new FigletText(text).Color(Color.Cyan1))
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Blue)
                .Padding(1, 0);
            AnsiConsole.Write(panel);
        }),

        ("Panel - Double Line Neon", text =>
        {
            var panel = new Panel(new FigletText(text).Color(Color.Green))
                .Border(BoxBorder.Double)
                .BorderColor(Color.Green)
                .Padding(1, 0);
            AnsiConsole.Write(panel);
        }),

        ("Panel - Heavy Purple", text =>
        {
            var panel = new Panel(new FigletText(text).Color(Color.Fuchsia))
                .Border(BoxBorder.Heavy)
                .BorderColor(Color.Purple)
                .Padding(1, 0);
            AnsiConsole.Write(panel);
        }),

        ("ASCII Box Art", text =>
        {
            var figlet = new FigletText(text).Color(Color.Cyan1);
            AnsiConsole.MarkupLine("[cyan]╔══════════════════════════════════════════════════════════════════╗[/]");
            AnsiConsole.MarkupLine("[cyan]║[/]                                                                  [cyan]║[/]");
            AnsiConsole.Write(figlet);
            AnsiConsole.MarkupLine("[cyan]║[/]                                                                  [cyan]║[/]");
            AnsiConsole.MarkupLine("[cyan]╚══════════════════════════════════════════════════════════════════╝[/]");
        }),

        ("Minimal Underline", text =>
        {
            AnsiConsole.Write(new FigletText(text).Color(Color.White));
            AnsiConsole.MarkupLine("[blue]════════════════════════════════════════════════════[/]");
        }),

        ("Tech Header + Tagline", text =>
        {
            AnsiConsole.Write(new FigletText(text).Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[grey]╭─────────────────────────────────────────────────────────╮[/]");
            AnsiConsole.MarkupLine("[grey]│[/] [blue]Delta Patching SDK[/] [grey]•[/] [green]Fast[/] [grey]•[/] [yellow]Efficient[/] [grey]•[/] [magenta]CDN-Ready[/] [grey]│[/]");
            AnsiConsole.MarkupLine("[grey]╰─────────────────────────────────────────────────────────╯[/]");
        }),

        ("Cyberpunk Style", text =>
        {
            AnsiConsole.MarkupLine("[fuchsia]▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓[/]");
            AnsiConsole.Write(new FigletText(text).Color(Color.Yellow));
            AnsiConsole.MarkupLine("[fuchsia]▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓[/]");
        }),

        ("Glitch Effect", text =>
        {
            AnsiConsole.MarkupLine("[red on black]█▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀█[/]");
            AnsiConsole.Write(new FigletText(text).Color(Color.Red));
            AnsiConsole.MarkupLine("[cyan]░▒▓█▓▒░[/][red]▒▓█▓▒░[/][green]▒▓█▓▒░[/][cyan]▒▓█▓▒░[/][red]▒▓█▓▒░[/][green]▒▓█▓▒░[/][cyan]▒▓█▓▒░[/]");
        }),

        ("Retro Terminal", text =>
        {
            AnsiConsole.MarkupLine("[green]┌──────────────────────────────────────────────────────────────┐[/]");
            AnsiConsole.MarkupLine("[green]│[/] [black on green] SYSTEM READY [/]                                             [green]│[/]");
            AnsiConsole.MarkupLine("[green]├──────────────────────────────────────────────────────────────┤[/]");
            AnsiConsole.Write(new FigletText(text).Color(Color.Green));
            AnsiConsole.MarkupLine("[green]└──────────────────────────────────────────────────────────────┘[/]");
        }),

        ("Modern Minimal", text =>
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new FigletText(text).Color(Color.Grey));
            AnsiConsole.MarkupLine("  [blue]●[/] [grey]Delta Patching Tool[/]");
            AnsiConsole.WriteLine();
        }),

        ("Hacker Style", text =>
        {
            AnsiConsole.MarkupLine("[green]> INITIALIZING...[/]");
            AnsiConsole.MarkupLine("[green]> LOADING SYSTEM...[/]");
            AnsiConsole.MarkupLine("[green]> ACCESS GRANTED[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new FigletText(text).Color(Color.Green));
            AnsiConsole.MarkupLine("[green]> _[/]");
        }),

        ("Unicode Blocks Header", text =>
        {
            AnsiConsole.MarkupLine("[blue]█▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀█[/]");
            AnsiConsole.Write(new FigletText(text).Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[blue]█▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄█[/]");
        }),

        ("Gradient Dots", text =>
        {
            AnsiConsole.MarkupLine("[blue]●[/][cyan]●[/][green]●[/][yellow]●[/][red]●[/][magenta]●[/] [grey]━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━[/]");
            AnsiConsole.Write(new FigletText(text).Color(Color.White));
            AnsiConsole.MarkupLine("[magenta]●[/][red]●[/][yellow]●[/][green]●[/][cyan]●[/][blue]●[/] [grey]━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━[/]");
        }),
    ];

    public static Task<int> RunAsync(string[] args)
    {
        var parser = new ArgParser(args);

        if (parser.HasHelp)
        {
            ShowHelp();
            return Task.FromResult(0);
        }

        var text = parser.Get("text") ?? "PatchSync";
        var styleIndex = parser.Get("style");

        if (styleIndex != null && int.TryParse(styleIndex, out var idx) && idx >= 0 && idx < TitleStyles.Length)
        {
            // Show specific style
            AnsiConsole.Clear();
            var (name, renderer) = TitleStyles[idx];
            AnsiConsole.MarkupLine($"[yellow]Style {idx}:[/] {name}\n");
            renderer(text);
            return Task.FromResult(0);
        }

        // Show all styles
        ShowAllStyles(text);
        return Task.FromResult(0);
    }

    public static Task<int> RunWizardAsync()
    {
        var text = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]Text to preview[/] [[PatchSync]]:")
                .AllowEmpty());

        if (string.IsNullOrWhiteSpace(text))
            text = "PatchSync";

        ShowAllStyles(text);

        // Let user pick a favorite
        var choices = TitleStyles.Select((s, i) => $"{i}: {s.Name}").ToList();
        choices.Add("Exit");

        while (true)
        {
            AnsiConsole.WriteLine();
            var selection = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[yellow]Select a style to preview again, or Exit:[/]")
                    .PageSize(20)
                    .AddChoices(choices));

            if (selection == "Exit")
                break;

            var idx = int.Parse(selection.Split(':')[0]);
            AnsiConsole.Clear();
            var (name, renderer) = TitleStyles[idx];
            AnsiConsole.MarkupLine($"[yellow]Style {idx}:[/] [bold]{name}[/]\n");
            renderer(text);
        }

        return Task.FromResult(0);
    }

    private static void ShowAllStyles(string text)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[bold yellow]╔══════════════════════════════════════════════════════════════════╗[/]");
        AnsiConsole.MarkupLine("[bold yellow]║[/]              [bold cyan]PATCHSYNC TITLE STYLE PREVIEW[/]                      [bold yellow]║[/]");
        AnsiConsole.MarkupLine("[bold yellow]╚══════════════════════════════════════════════════════════════════╝[/]");
        AnsiConsole.WriteLine();

        // Terminal info
        var termInfo = GetTerminalInfo();
        AnsiConsole.MarkupLine($"[grey]Terminal:[/] {termInfo}");
        AnsiConsole.MarkupLine($"[grey]Unicode:[/] {(Console.OutputEncoding.WebName.Contains("utf") ? "[green]Supported[/]" : "[yellow]Limited[/]")}");
        AnsiConsole.MarkupLine($"[grey]Colors:[/] {(AnsiConsole.Profile.Capabilities.ColorSystem)}");
        AnsiConsole.WriteLine();

        for (int i = 0; i < TitleStyles.Length; i++)
        {
            var (name, renderer) = TitleStyles[i];

            AnsiConsole.MarkupLine($"[grey]─────────────────────────────────────────────────────────────────[/]");
            AnsiConsole.MarkupLine($"[yellow]Style {i}:[/] [bold]{name}[/]");
            AnsiConsole.MarkupLine($"[grey]─────────────────────────────────────────────────────────────────[/]");
            AnsiConsole.WriteLine();

            try
            {
                renderer(text);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error rendering: {ex.Message}[/]");
            }

            AnsiConsole.WriteLine();
            AnsiConsole.WriteLine();
        }
    }

    private static string GetTerminalInfo()
    {
        var term = Environment.GetEnvironmentVariable("TERM") ?? "unknown";
        var termProgram = Environment.GetEnvironmentVariable("TERM_PROGRAM") ?? "";
        var wtSession = Environment.GetEnvironmentVariable("WT_SESSION");
        var conEmu = Environment.GetEnvironmentVariable("ConEmuANSI");

        if (!string.IsNullOrEmpty(wtSession))
            return "[cyan]Windows Terminal[/]";
        if (!string.IsNullOrEmpty(conEmu))
            return "[cyan]ConEmu[/]";
        if (termProgram.Contains("iTerm", StringComparison.OrdinalIgnoreCase))
            return "[cyan]iTerm2[/]";
        if (termProgram.Contains("ghostty", StringComparison.OrdinalIgnoreCase))
            return "[cyan]Ghostty[/]";
        if (termProgram.Contains("vscode", StringComparison.OrdinalIgnoreCase))
            return "[cyan]VS Code Terminal[/]";
        if (term.Contains("xterm"))
            return $"[cyan]xterm ({term})[/]";
        if (Environment.GetEnvironmentVariable("PSModulePath") != null)
            return "[cyan]PowerShell[/]";

        return $"[grey]{term}[/]";
    }

    private static void ShowHelp()
    {
        AnsiConsole.MarkupLine("[yellow]Usage:[/] patchsync fonts [options]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Options:[/]");
        AnsiConsole.MarkupLine("  --text <text>    Text to preview (default: PatchSync)");
        AnsiConsole.MarkupLine("  --style <n>      Show specific style by number");
        AnsiConsole.MarkupLine("  -h, --help       Show this help message");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Examples:[/]");
        AnsiConsole.MarkupLine("  patchsync fonts");
        AnsiConsole.MarkupLine("  patchsync fonts --text \"My App\"");
        AnsiConsole.MarkupLine("  patchsync fonts --style 5");
    }
}
