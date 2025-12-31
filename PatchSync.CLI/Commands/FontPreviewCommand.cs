using System.Net.Http;
using Spectre.Console;

namespace PatchSync.CLI.Commands;

/// <summary>
/// Preview different Figlet fonts and title styles for the CLI.
/// Interactive navigation: Left/Right for fonts, Up/Down for styles.
/// </summary>
public static class FontPreviewCommand
{
    private static readonly string FontCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PatchSync", "fonts");

    /// <summary>
    /// Available Figlet fonts (name, filename, download URL)
    /// </summary>
    private static readonly (string Name, string File, string Url)[] AvailableFonts =
    [
        ("Standard", "standard.flf", "http://www.figlet.org/fonts/standard.flf"),
        ("Slant", "slant.flf", "http://www.figlet.org/fonts/slant.flf"),
        ("Small", "small.flf", "http://www.figlet.org/fonts/small.flf"),
        ("Big", "big.flf", "http://www.figlet.org/fonts/big.flf"),
        ("Banner", "banner.flf", "http://www.figlet.org/fonts/banner.flf"),
        ("Block", "block.flf", "http://www.figlet.org/fonts/block.flf"),
        ("Lean", "lean.flf", "http://www.figlet.org/fonts/lean.flf"),
        ("Mini", "mini.flf", "http://www.figlet.org/fonts/mini.flf"),
        ("Script", "script.flf", "http://www.figlet.org/fonts/script.flf"),
        ("Shadow", "shadow.flf", "http://www.figlet.org/fonts/shadow.flf"),
        ("Slant Small", "smshadow.flf", "http://www.figlet.org/fonts/smshadow.flf"),
        ("Speed", "speed.flf", "http://www.figlet.org/fonts/speed.flf"),
        ("Star Wars", "starwars.flf", "http://www.figlet.org/fonts/starwars.flf"),
        ("3D Diagonal", "3-d.flf", "http://www.figlet.org/fonts/3-d.flf"),
        ("Doom", "doom.flf", "http://www.figlet.org/fonts/doom.flf"),
        ("Epic", "epic.flf", "http://www.figlet.org/fonts/epic.flf"),
        ("Fender", "fender.flf", "http://www.figlet.org/fonts/fender.flf"),
        ("Larry 3D", "larry3d.flf", "http://www.figlet.org/fonts/larry3d.flf"),
        ("Ogre", "ogre.flf", "http://www.figlet.org/fonts/ogre.flf"),
        ("Pebbles", "pebbles.flf", "http://www.figlet.org/fonts/pebbles.flf"),
        ("Puffy", "puffy.flf", "http://www.figlet.org/fonts/puffy.flf"),
        ("Rectangles", "rectangles.flf", "http://www.figlet.org/fonts/rectangles.flf"),
        ("Stampatello", "stampatello.flf", "http://www.figlet.org/fonts/stampatello.flf"),
        ("Univers", "univers.flf", "http://www.figlet.org/fonts/univers.flf"),
    ];

    /// <summary>
    /// Color/style presets for the Figlet text
    /// </summary>
    private static readonly (string Name, Color Color, string? Decoration)[] ColorStyles =
    [
        ("Blue", Color.Blue, null),
        ("Cyan", Color.Cyan1, null),
        ("Green (Matrix)", Color.Green, null),
        ("Hot Pink", Color.DeepPink1, null),
        ("Purple", Color.MediumPurple1, null),
        ("Orange", Color.Orange1, null),
        ("Yellow", Color.Yellow, null),
        ("White", Color.White, null),
        ("Red", Color.Red, null),
        ("Grey", Color.Grey, null),
        ("Cyan + Line", Color.Cyan1, "line"),
        ("Green + Box", Color.Green, "box-double"),
        ("Purple + Box", Color.Fuchsia, "box-heavy"),
        ("Blue + Box", Color.Blue, "box-rounded"),
        ("Cyberpunk", Color.Yellow, "cyber"),
        ("Glitch", Color.Red, "glitch"),
        ("Retro Terminal", Color.Green, "terminal"),
        ("Hacker", Color.Green, "hacker"),
        ("Neon Sign", Color.Magenta1, "neon"),
        ("Rainbow", Color.Cyan1, "rainbow"),
    ];

    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly Dictionary<string, FigletFont?> _fontCache = new();

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
        var fontIndex = 0;
        var styleIndex = 0;
        var running = true;

        // Ensure cache directory exists
        Directory.CreateDirectory(FontCacheDir);

        // Hide cursor for cleaner display
        Console.CursorVisible = false;

        try
        {
            while (running)
            {
                RenderPreview(text, fontIndex, styleIndex);

                var key = Console.ReadKey(true);

                switch (key.Key)
                {
                    // Font navigation (Left/Right)
                    case ConsoleKey.LeftArrow:
                    case ConsoleKey.H: // vim-style
                        fontIndex = (fontIndex - 1 + AvailableFonts.Length) % AvailableFonts.Length;
                        break;

                    case ConsoleKey.RightArrow:
                    case ConsoleKey.L: // vim-style
                        fontIndex = (fontIndex + 1) % AvailableFonts.Length;
                        break;

                    // Style navigation (Up/Down)
                    case ConsoleKey.UpArrow:
                    case ConsoleKey.K: // vim-style
                        styleIndex = (styleIndex - 1 + ColorStyles.Length) % ColorStyles.Length;
                        break;

                    case ConsoleKey.DownArrow:
                    case ConsoleKey.J: // vim-style
                        styleIndex = (styleIndex + 1) % ColorStyles.Length;
                        break;

                    case ConsoleKey.Home:
                        fontIndex = 0;
                        styleIndex = 0;
                        break;

                    case ConsoleKey.End:
                        fontIndex = AvailableFonts.Length - 1;
                        styleIndex = ColorStyles.Length - 1;
                        break;

                    case ConsoleKey.PageUp:
                        styleIndex = Math.Max(0, styleIndex - 5);
                        break;

                    case ConsoleKey.PageDown:
                        styleIndex = Math.Min(ColorStyles.Length - 1, styleIndex + 5);
                        break;

                    case ConsoleKey.Enter:
                        running = false;
                        ShowSelectedStyle(text, fontIndex, styleIndex);
                        break;

                    case ConsoleKey.Escape:
                    case ConsoleKey.Q:
                        running = false;
                        AnsiConsole.Clear();
                        AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                        break;

                    // Tab to cycle through fonts quickly
                    case ConsoleKey.Tab:
                        if (key.Modifiers.HasFlag(ConsoleModifiers.Shift))
                            fontIndex = (fontIndex - 1 + AvailableFonts.Length) % AvailableFonts.Length;
                        else
                            fontIndex = (fontIndex + 1) % AvailableFonts.Length;
                        break;
                }
            }
        }
        finally
        {
            Console.CursorVisible = true;
        }
    }

    private static void RenderPreview(string text, int fontIndex, int styleIndex)
    {
        AnsiConsole.Clear();

        var (fontName, fontFile, _) = AvailableFonts[fontIndex];
        var (styleName, color, decoration) = ColorStyles[styleIndex];
        var termInfo = GetTerminalInfo();

        // Header
        AnsiConsole.MarkupLine("[grey]═══════════════════════════════════════════════════════════════════════════[/]");
        AnsiConsole.MarkupLine("[bold yellow]  PATCHSYNC FONT & STYLE PREVIEW[/]");
        AnsiConsole.MarkupLine("[grey]═══════════════════════════════════════════════════════════════════════════[/]");
        AnsiConsole.WriteLine();

        // Navigation info
        AnsiConsole.MarkupLine($"[grey]Terminal:[/] {termInfo}");
        AnsiConsole.MarkupLine($"[grey]Font:[/] [cyan]{fontIndex + 1}[/][grey]/[/][cyan]{AvailableFonts.Length}[/]    [grey]Style:[/] [cyan]{styleIndex + 1}[/][grey]/[/][cyan]{ColorStyles.Length}[/]");
        AnsiConsole.WriteLine();

        // Current selection
        AnsiConsole.MarkupLine($"[yellow]Font:[/] [bold white]{fontName}[/]  [grey]│[/]  [yellow]Style:[/] [bold white]{styleName}[/]");
        AnsiConsole.MarkupLine("[grey]───────────────────────────────────────────────────────────────────────────[/]");
        AnsiConsole.WriteLine();

        // Render the preview
        try
        {
            var font = GetOrDownloadFont(fontIndex);
            RenderWithStyle(text, font, color, decoration);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
        }

        AnsiConsole.WriteLine();

        // Controls
        AnsiConsole.MarkupLine("[grey]───────────────────────────────────────────────────────────────────────────[/]");
        AnsiConsole.MarkupLine("[grey]  [/][white]←/→[/][grey] or [/][white]h/l[/][grey]  Change font       [/][white]↑/↓[/][grey] or [/][white]j/k[/][grey]  Change style[/]");
        AnsiConsole.MarkupLine("[grey]  [/][white]Tab[/][grey]          Next font          [/][white]Enter[/][grey]        Select[/]");
        AnsiConsole.MarkupLine("[grey]  [/][white]Esc/q[/][grey]        Cancel[/]");
        AnsiConsole.MarkupLine("[grey]───────────────────────────────────────────────────────────────────────────[/]");

        // Font and style lists side by side
        AnsiConsole.WriteLine();
        RenderLists(fontIndex, styleIndex);
    }

    private static void RenderLists(int fontIndex, int styleIndex)
    {
        // Show 5 fonts and 5 styles side by side
        var fontStart = Math.Max(0, fontIndex - 2);
        var fontEnd = Math.Min(AvailableFonts.Length - 1, fontStart + 4);
        fontStart = Math.Max(0, fontEnd - 4);

        var styleStart = Math.Max(0, styleIndex - 2);
        var styleEnd = Math.Min(ColorStyles.Length - 1, styleStart + 4);
        styleStart = Math.Max(0, styleEnd - 4);

        var lines = Math.Max(fontEnd - fontStart + 1, styleEnd - styleStart + 1);

        AnsiConsole.MarkupLine("[grey]  Fonts                              Styles[/]");
        AnsiConsole.MarkupLine("[grey]  ─────                              ──────[/]");

        for (int i = 0; i < lines; i++)
        {
            var fontLine = "";
            var styleLine = "";

            var fi = fontStart + i;
            if (fi <= fontEnd && fi < AvailableFonts.Length)
            {
                var (name, _, _) = AvailableFonts[fi];
                var displayName = name.Length > 18 ? name[..15] + "..." : name;
                if (fi == fontIndex)
                    fontLine = $"[cyan]►[/] [bold white]{displayName,-18}[/]";
                else
                    fontLine = $"  [grey]{displayName,-18}[/]";
            }
            else
            {
                fontLine = new string(' ', 20);
            }

            var si = styleStart + i;
            if (si <= styleEnd && si < ColorStyles.Length)
            {
                var (name, _, _) = ColorStyles[si];
                var displayName = name.Length > 18 ? name[..15] + "..." : name;
                if (si == styleIndex)
                    styleLine = $"[cyan]►[/] [bold white]{displayName}[/]";
                else
                    styleLine = $"  [grey]{displayName}[/]";
            }

            AnsiConsole.MarkupLine($"  {fontLine}         {styleLine}");
        }
    }

    private static FigletFont? GetOrDownloadFont(int fontIndex)
    {
        var (name, file, url) = AvailableFonts[fontIndex];
        var cachePath = Path.Combine(FontCacheDir, file);

        // Check memory cache
        if (_fontCache.TryGetValue(file, out var cachedFont))
            return cachedFont;

        // Check disk cache
        if (File.Exists(cachePath))
        {
            try
            {
                var font = FigletFont.Load(cachePath);
                _fontCache[file] = font;
                return font;
            }
            catch
            {
                // Corrupted file, re-download
                File.Delete(cachePath);
            }
        }

        // Download font
        try
        {
            AnsiConsole.MarkupLine($"[grey]Downloading font: {name}...[/]");
            var content = _httpClient.GetStringAsync(url).GetAwaiter().GetResult();
            File.WriteAllText(cachePath, content);
            var font = FigletFont.Load(cachePath);
            _fontCache[file] = font;
            return font;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Could not download font: {Markup.Escape(ex.Message)}[/]");
            _fontCache[file] = null;
            return null;
        }
    }

    private static void RenderWithStyle(string text, FigletFont? font, Color color, string? decoration)
    {
        var figlet = font != null
            ? new FigletText(font, text).Color(color)
            : new FigletText(text).Color(color);

        switch (decoration)
        {
            case "line":
                AnsiConsole.Write(figlet);
                AnsiConsole.MarkupLine($"[{color.ToMarkup()}]━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━[/]");
                break;

            case "box-double":
                var panelDouble = new Panel(figlet)
                    .Border(BoxBorder.Double)
                    .BorderColor(color)
                    .Padding(1, 0);
                AnsiConsole.Write(panelDouble);
                break;

            case "box-heavy":
                var panelHeavy = new Panel(figlet)
                    .Border(BoxBorder.Heavy)
                    .BorderColor(color)
                    .Padding(1, 0);
                AnsiConsole.Write(panelHeavy);
                break;

            case "box-rounded":
                var panelRounded = new Panel(figlet)
                    .Border(BoxBorder.Rounded)
                    .BorderColor(color)
                    .Padding(1, 0);
                AnsiConsole.Write(panelRounded);
                break;

            case "cyber":
                AnsiConsole.MarkupLine("[fuchsia]▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓[/]");
                AnsiConsole.Write(figlet);
                AnsiConsole.MarkupLine("[fuchsia]▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓[/]");
                break;

            case "glitch":
                AnsiConsole.MarkupLine("[red on black]█▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀█[/]");
                AnsiConsole.Write(figlet);
                AnsiConsole.MarkupLine("[cyan]░▒▓█▓▒░[/][red]▒▓█▓▒░[/][green]▒▓█▓▒░[/][cyan]▒▓█▓▒░[/][red]▒▓█▓▒░[/][green]▒▓█▓▒░[/][cyan]▒▓█▓▒░[/][red]▒▓█▓▒░[/][green]▒▓█▓▒░[/]");
                break;

            case "terminal":
                AnsiConsole.MarkupLine("[green]┌─────────────────────────────────────────────────────────────────────────┐[/]");
                AnsiConsole.MarkupLine("[green]│[/] [black on green] SYSTEM READY [/]                                                       [green]│[/]");
                AnsiConsole.MarkupLine("[green]├─────────────────────────────────────────────────────────────────────────┤[/]");
                AnsiConsole.Write(figlet);
                AnsiConsole.MarkupLine("[green]└─────────────────────────────────────────────────────────────────────────┘[/]");
                break;

            case "hacker":
                AnsiConsole.MarkupLine("[green]> INITIALIZING...[/]");
                AnsiConsole.MarkupLine("[green]> LOADING SYSTEM...[/]");
                AnsiConsole.MarkupLine("[green]> ACCESS GRANTED[/]");
                AnsiConsole.WriteLine();
                AnsiConsole.Write(figlet);
                AnsiConsole.MarkupLine("[green]> _[/]");
                break;

            case "neon":
                AnsiConsole.MarkupLine("[grey]     ┏━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┓[/]");
                AnsiConsole.Write(figlet);
                AnsiConsole.MarkupLine("[grey]     ┗━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┛[/]");
                AnsiConsole.MarkupLine("[magenta1]                    ✧ ✦ ✧ ✦ ✧ ✦ ✧ ✦ ✧ ✦ ✧[/]");
                break;

            case "rainbow":
                AnsiConsole.MarkupLine("[red]█[/][orange1]█[/][yellow]█[/][green]█[/][cyan]█[/][blue]█[/][purple]█[/][red]█[/][orange1]█[/][yellow]█[/][green]█[/][cyan]█[/][blue]█[/][purple]█[/][red]█[/][orange1]█[/][yellow]█[/][green]█[/][cyan]█[/][blue]█[/][purple]█[/][red]█[/][orange1]█[/][yellow]█[/][green]█[/][cyan]█[/][blue]█[/][purple]█[/][red]█[/][orange1]█[/][yellow]█[/][green]█[/][cyan]█[/][blue]█[/][purple]█[/]");
                AnsiConsole.Write(figlet);
                AnsiConsole.MarkupLine("[purple]█[/][blue]█[/][cyan]█[/][green]█[/][yellow]█[/][orange1]█[/][red]█[/][purple]█[/][blue]█[/][cyan]█[/][green]█[/][yellow]█[/][orange1]█[/][red]█[/][purple]█[/][blue]█[/][cyan]█[/][green]█[/][yellow]█[/][orange1]█[/][red]█[/][purple]█[/][blue]█[/][cyan]█[/][green]█[/][yellow]█[/][orange1]█[/][red]█[/][purple]█[/][blue]█[/][cyan]█[/][green]█[/][yellow]█[/][orange1]█[/][red]█[/]");
                break;

            default:
                AnsiConsole.Write(figlet);
                break;
        }
    }

    private static void ShowSelectedStyle(string text, int fontIndex, int styleIndex)
    {
        AnsiConsole.Clear();

        var (fontName, fontFile, _) = AvailableFonts[fontIndex];
        var (styleName, color, decoration) = ColorStyles[styleIndex];

        AnsiConsole.MarkupLine($"[green]✓[/] Selected: [bold]{fontName}[/] + [bold]{styleName}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]───────────────────────────────────────────────────────────────────────────[/]");
        AnsiConsole.WriteLine();

        try
        {
            var font = GetOrDownloadFont(fontIndex);
            RenderWithStyle(text, font, color, decoration);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]───────────────────────────────────────────────────────────────────────────[/]");
        AnsiConsole.WriteLine();

        // Show configuration details
        AnsiConsole.MarkupLine("[yellow]Configuration:[/]");
        AnsiConsole.MarkupLine($"  [grey]Font file:[/] {fontFile}");
        AnsiConsole.MarkupLine($"  [grey]Color:[/] {color}");
        AnsiConsole.MarkupLine($"  [grey]Decoration:[/] {decoration ?? "none"}");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[grey]Fonts cached in:[/] {FontCacheDir}");
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
            return "[cyan]xterm[/]";
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
        AnsiConsole.MarkupLine("  ←/→ or h/l    Cycle through fonts");
        AnsiConsole.MarkupLine("  ↑/↓ or j/k    Cycle through styles");
        AnsiConsole.MarkupLine("  Tab           Next font");
        AnsiConsole.MarkupLine("  Enter         Select current combination");
        AnsiConsole.MarkupLine("  Esc/q         Cancel and exit");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Features:[/]");
        AnsiConsole.MarkupLine($"  {AvailableFonts.Length} Figlet fonts (downloaded on demand)");
        AnsiConsole.MarkupLine($"  {ColorStyles.Length} color/style presets");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Examples:[/]");
        AnsiConsole.MarkupLine("  patchsync fonts");
        AnsiConsole.MarkupLine("  patchsync fonts --text \"My App\"");
    }
}
