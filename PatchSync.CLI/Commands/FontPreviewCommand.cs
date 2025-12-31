using Spectre.Console;

namespace PatchSync.CLI.Commands;

/// <summary>
/// Preview different Figlet fonts and title styles for the CLI.
/// Interactive arrow-key navigation to cycle through styles.
/// </summary>
public static class FontPreviewCommand
{
    /// <summary>
    /// High-tech title style presets
    /// </summary>
    private static readonly (string Name, string Category, Action<string> Renderer)[] TitleStyles =
    [
        // Clean/Professional
        ("Default Blue", "Clean", text =>
        {
            AnsiConsole.Write(new FigletText(text).Color(Color.Blue));
        }),

        ("Minimal Underline", "Clean", text =>
        {
            AnsiConsole.Write(new FigletText(text).Color(Color.White));
            AnsiConsole.MarkupLine("[blue]════════════════════════════════════════════════════[/]");
        }),

        ("Modern Minimal", "Clean", text =>
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new FigletText(text).Color(Color.Grey));
            AnsiConsole.MarkupLine("  [blue]●[/] [grey]Delta Patching Tool[/]");
            AnsiConsole.WriteLine();
        }),

        // Neon/Cyber
        ("Cyan Glow", "Neon", text =>
        {
            AnsiConsole.Write(new FigletText(text).Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[grey]━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━[/]");
        }),

        ("Neon Green (Matrix)", "Neon", text =>
        {
            AnsiConsole.Write(new FigletText(text).Color(Color.Green));
        }),

        ("Hot Pink (Synthwave)", "Neon", text =>
        {
            AnsiConsole.Write(new FigletText(text).Color(Color.DeepPink1));
        }),

        ("Purple Haze", "Neon", text =>
        {
            AnsiConsole.Write(new FigletText(text).Color(Color.MediumPurple1));
        }),

        ("Orange Ember", "Neon", text =>
        {
            AnsiConsole.Write(new FigletText(text).Color(Color.Orange1));
        }),

        // Panels/Boxed
        ("Panel Rounded", "Boxed", text =>
        {
            var panel = new Panel(new FigletText(text).Color(Color.Cyan1))
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Blue)
                .Padding(1, 0);
            AnsiConsole.Write(panel);
        }),

        ("Panel Double Neon", "Boxed", text =>
        {
            var panel = new Panel(new FigletText(text).Color(Color.Green))
                .Border(BoxBorder.Double)
                .BorderColor(Color.Green)
                .Padding(1, 0);
            AnsiConsole.Write(panel);
        }),

        ("Panel Heavy Purple", "Boxed", text =>
        {
            var panel = new Panel(new FigletText(text).Color(Color.Fuchsia))
                .Border(BoxBorder.Heavy)
                .BorderColor(Color.Purple)
                .Padding(1, 0);
            AnsiConsole.Write(panel);
        }),

        // Tech/Cyber
        ("Tech Header", "Tech", text =>
        {
            AnsiConsole.Write(new FigletText(text).Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[grey]╭─────────────────────────────────────────────────────────╮[/]");
            AnsiConsole.MarkupLine("[grey]│[/] [blue]Delta Patching SDK[/] [grey]•[/] [green]Fast[/] [grey]•[/] [yellow]Efficient[/] [grey]•[/] [magenta]CDN-Ready[/] [grey]│[/]");
            AnsiConsole.MarkupLine("[grey]╰─────────────────────────────────────────────────────────╯[/]");
        }),

        ("Cyberpunk", "Tech", text =>
        {
            AnsiConsole.MarkupLine("[fuchsia]▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓[/]");
            AnsiConsole.Write(new FigletText(text).Color(Color.Yellow));
            AnsiConsole.MarkupLine("[fuchsia]▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓[/]");
        }),

        ("Glitch", "Tech", text =>
        {
            AnsiConsole.MarkupLine("[red on black]█▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀█[/]");
            AnsiConsole.Write(new FigletText(text).Color(Color.Red));
            AnsiConsole.MarkupLine("[cyan]░▒▓█▓▒░[/][red]▒▓█▓▒░[/][green]▒▓█▓▒░[/][cyan]▒▓█▓▒░[/][red]▒▓█▓▒░[/][green]▒▓█▓▒░[/][cyan]▒▓█▓▒░[/]");
        }),

        ("Unicode Blocks", "Tech", text =>
        {
            AnsiConsole.MarkupLine("[blue]█▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀█[/]");
            AnsiConsole.Write(new FigletText(text).Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[blue]█▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄█[/]");
        }),

        // Retro/Terminal
        ("Retro Terminal", "Retro", text =>
        {
            AnsiConsole.MarkupLine("[green]┌──────────────────────────────────────────────────────────────┐[/]");
            AnsiConsole.MarkupLine("[green]│[/] [black on green] SYSTEM READY [/]                                             [green]│[/]");
            AnsiConsole.MarkupLine("[green]├──────────────────────────────────────────────────────────────┤[/]");
            AnsiConsole.Write(new FigletText(text).Color(Color.Green));
            AnsiConsole.MarkupLine("[green]└──────────────────────────────────────────────────────────────┘[/]");
        }),

        ("Hacker Boot", "Retro", text =>
        {
            AnsiConsole.MarkupLine("[green]> INITIALIZING...[/]");
            AnsiConsole.MarkupLine("[green]> LOADING SYSTEM...[/]");
            AnsiConsole.MarkupLine("[green]> ACCESS GRANTED[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new FigletText(text).Color(Color.Green));
            AnsiConsole.MarkupLine("[green]> _[/]");
        }),

        ("Amber CRT", "Retro", text =>
        {
            AnsiConsole.MarkupLine("[orange1]╔══════════════════════════════════════════════════════════════╗[/]");
            AnsiConsole.Write(new FigletText(text).Color(Color.Orange1));
            AnsiConsole.MarkupLine("[orange1]╚══════════════════════════════════════════════════════════════╝[/]");
        }),

        // Colorful
        ("Gradient Dots", "Colorful", text =>
        {
            AnsiConsole.MarkupLine("[blue]●[/][cyan]●[/][green]●[/][yellow]●[/][red]●[/][magenta]●[/] [grey]━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━[/]");
            AnsiConsole.Write(new FigletText(text).Color(Color.White));
            AnsiConsole.MarkupLine("[magenta]●[/][red]●[/][yellow]●[/][green]●[/][cyan]●[/][blue]●[/] [grey]━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━[/]");
        }),

        ("Rainbow Bar", "Colorful", text =>
        {
            AnsiConsole.MarkupLine("[red]█[/][orange1]█[/][yellow]█[/][green]█[/][cyan]█[/][blue]█[/][purple]█[/][red]█[/][orange1]█[/][yellow]█[/][green]█[/][cyan]█[/][blue]█[/][purple]█[/][red]█[/][orange1]█[/][yellow]█[/][green]█[/][cyan]█[/][blue]█[/][purple]█[/][red]█[/][orange1]█[/][yellow]█[/][green]█[/][cyan]█[/][blue]█[/][purple]█[/][red]█[/][orange1]█[/][yellow]█[/][green]█[/][cyan]█[/][blue]█[/][purple]█[/]");
            AnsiConsole.Write(new FigletText(text).Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[purple]█[/][blue]█[/][cyan]█[/][green]█[/][yellow]█[/][orange1]█[/][red]█[/][purple]█[/][blue]█[/][cyan]█[/][green]█[/][yellow]█[/][orange1]█[/][red]█[/][purple]█[/][blue]█[/][cyan]█[/][green]█[/][yellow]█[/][orange1]█[/][red]█[/][purple]█[/][blue]█[/][cyan]█[/][green]█[/][yellow]█[/][orange1]█[/][red]█[/][purple]█[/][blue]█[/][cyan]█[/][green]█[/][yellow]█[/][orange1]█[/][red]█[/]");
        }),

        ("Neon Sign", "Colorful", text =>
        {
            AnsiConsole.MarkupLine("[grey]     ┏━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┓[/]");
            AnsiConsole.Write(new FigletText(text).Color(Color.Magenta1));
            AnsiConsole.MarkupLine("[grey]     ┗━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┛[/]");
            AnsiConsole.MarkupLine("[magenta1]              ✧ ✦ ✧ ✦ ✧ ✦ ✧ ✦ ✧ ✦ ✧[/]");
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
        RunInteractivePreview(text);
        return Task.FromResult(0);
    }

    public static Task<int> RunWizardAsync()
    {
        var text = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]Text to preview[/] [grey](Enter for PatchSync)[/]:")
                .AllowEmpty());

        if (string.IsNullOrWhiteSpace(text))
            text = "PatchSync";

        RunInteractivePreview(text);
        return Task.FromResult(0);
    }

    private static void RunInteractivePreview(string text)
    {
        var currentIndex = 0;
        var running = true;

        // Hide cursor for cleaner display
        Console.CursorVisible = false;

        try
        {
            while (running)
            {
                RenderPreview(text, currentIndex);

                var key = Console.ReadKey(true);

                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                    case ConsoleKey.LeftArrow:
                    case ConsoleKey.K: // vim-style
                        currentIndex = (currentIndex - 1 + TitleStyles.Length) % TitleStyles.Length;
                        break;

                    case ConsoleKey.DownArrow:
                    case ConsoleKey.RightArrow:
                    case ConsoleKey.J: // vim-style
                        currentIndex = (currentIndex + 1) % TitleStyles.Length;
                        break;

                    case ConsoleKey.Home:
                        currentIndex = 0;
                        break;

                    case ConsoleKey.End:
                        currentIndex = TitleStyles.Length - 1;
                        break;

                    case ConsoleKey.PageUp:
                        currentIndex = Math.Max(0, currentIndex - 5);
                        break;

                    case ConsoleKey.PageDown:
                        currentIndex = Math.Min(TitleStyles.Length - 1, currentIndex + 5);
                        break;

                    case ConsoleKey.Enter:
                        running = false;
                        ShowSelectedStyle(text, currentIndex);
                        break;

                    case ConsoleKey.Escape:
                    case ConsoleKey.Q:
                        running = false;
                        AnsiConsole.Clear();
                        AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                        break;

                    // Number keys for quick jump
                    case ConsoleKey.D0 or ConsoleKey.NumPad0:
                        currentIndex = Math.Min(9, TitleStyles.Length - 1);
                        break;
                    case ConsoleKey.D1 or ConsoleKey.NumPad1:
                        currentIndex = 0;
                        break;
                    case ConsoleKey.D2 or ConsoleKey.NumPad2:
                        currentIndex = Math.Min(1, TitleStyles.Length - 1);
                        break;
                    case ConsoleKey.D3 or ConsoleKey.NumPad3:
                        currentIndex = Math.Min(2, TitleStyles.Length - 1);
                        break;
                    case ConsoleKey.D4 or ConsoleKey.NumPad4:
                        currentIndex = Math.Min(3, TitleStyles.Length - 1);
                        break;
                    case ConsoleKey.D5 or ConsoleKey.NumPad5:
                        currentIndex = Math.Min(4, TitleStyles.Length - 1);
                        break;
                    case ConsoleKey.D6 or ConsoleKey.NumPad6:
                        currentIndex = Math.Min(5, TitleStyles.Length - 1);
                        break;
                    case ConsoleKey.D7 or ConsoleKey.NumPad7:
                        currentIndex = Math.Min(6, TitleStyles.Length - 1);
                        break;
                    case ConsoleKey.D8 or ConsoleKey.NumPad8:
                        currentIndex = Math.Min(7, TitleStyles.Length - 1);
                        break;
                    case ConsoleKey.D9 or ConsoleKey.NumPad9:
                        currentIndex = Math.Min(8, TitleStyles.Length - 1);
                        break;
                }
            }
        }
        finally
        {
            Console.CursorVisible = true;
        }
    }

    private static void RenderPreview(string text, int currentIndex)
    {
        AnsiConsole.Clear();

        var (name, category, renderer) = TitleStyles[currentIndex];
        var termInfo = GetTerminalInfo();

        // Header
        AnsiConsole.MarkupLine("[grey]═══════════════════════════════════════════════════════════════════[/]");
        AnsiConsole.MarkupLine("[bold yellow]  PATCHSYNC TITLE STYLE PREVIEW[/]");
        AnsiConsole.MarkupLine("[grey]═══════════════════════════════════════════════════════════════════[/]");
        AnsiConsole.WriteLine();

        // Navigation info
        AnsiConsole.MarkupLine($"[grey]Terminal:[/] {termInfo}    [grey]Styles:[/] [cyan]{currentIndex + 1}[/][grey]/[/][cyan]{TitleStyles.Length}[/]");
        AnsiConsole.WriteLine();

        // Style info
        AnsiConsole.MarkupLine($"[yellow]Style:[/] [bold white]{name}[/]  [grey]│[/]  [yellow]Category:[/] [blue]{category}[/]");
        AnsiConsole.MarkupLine("[grey]───────────────────────────────────────────────────────────────────[/]");
        AnsiConsole.WriteLine();

        // Render the style
        try
        {
            renderer(text);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine();

        // Controls
        AnsiConsole.MarkupLine("[grey]───────────────────────────────────────────────────────────────────[/]");
        AnsiConsole.MarkupLine("[grey]  [/][white]↑/↓[/][grey] or [/][white]j/k[/][grey]  Cycle styles     [/][white]1-9[/][grey]  Jump to style[/]");
        AnsiConsole.MarkupLine("[grey]  [/][white]Enter[/][grey]       Select style      [/][white]Esc/q[/][grey]  Cancel[/]");
        AnsiConsole.MarkupLine("[grey]───────────────────────────────────────────────────────────────────[/]");

        // Style list preview
        AnsiConsole.WriteLine();
        var startIdx = Math.Max(0, currentIndex - 2);
        var endIdx = Math.Min(TitleStyles.Length - 1, startIdx + 5);
        startIdx = Math.Max(0, endIdx - 5); // Adjust if at the end

        for (int i = startIdx; i <= endIdx && i < TitleStyles.Length; i++)
        {
            var (n, c, _) = TitleStyles[i];
            if (i == currentIndex)
            {
                AnsiConsole.MarkupLine($"  [cyan]►[/] [bold white]{i + 1,2}. {n}[/] [grey]({c})[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"    [grey]{i + 1,2}. {n} ({c})[/]");
            }
        }
    }

    private static void ShowSelectedStyle(string text, int selectedIndex)
    {
        AnsiConsole.Clear();

        var (name, category, renderer) = TitleStyles[selectedIndex];

        AnsiConsole.MarkupLine($"[green]✓[/] Selected: [bold]{name}[/] [grey]({category})[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]───────────────────────────────────────────────────────────────────[/]");
        AnsiConsole.WriteLine();

        renderer(text);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]───────────────────────────────────────────────────────────────────[/]");
        AnsiConsole.WriteLine();

        // Show code hint
        AnsiConsole.MarkupLine("[yellow]To use this style, update WorkspaceMenu.cs ShowWorkspaceMenuAsync()[/]");
        AnsiConsole.MarkupLine($"[grey]Style index: {selectedIndex}[/]");
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
            return $"[cyan]xterm[/]";
        if (Environment.GetEnvironmentVariable("PSModulePath") != null)
            return "[cyan]PowerShell[/]";

        return $"[grey]{Markup.Escape(term)}[/]";
    }

    private static void ShowHelp()
    {
        AnsiConsole.MarkupLine("[yellow]Usage:[/] patchsync fonts [options]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Options:[/]");
        AnsiConsole.MarkupLine("  --text, -t <text>  Text to preview (default: PatchSync)");
        AnsiConsole.MarkupLine("  -h, --help         Show this help message");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Interactive Controls:[/]");
        AnsiConsole.MarkupLine("  ↑/↓ or j/k    Cycle through styles");
        AnsiConsole.MarkupLine("  1-9           Jump to style by number");
        AnsiConsole.MarkupLine("  Enter         Select current style");
        AnsiConsole.MarkupLine("  Esc/q         Cancel and exit");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Examples:[/]");
        AnsiConsole.MarkupLine("  patchsync fonts");
        AnsiConsole.MarkupLine("  patchsync fonts --text \"My App\"");
    }
}
