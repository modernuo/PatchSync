using Spectre.Console;

namespace PatchSync.CLI.Commands;

/// <summary>
/// Registered command information for the CLI.
/// </summary>
public sealed class CommandInfo
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required Func<string[], Task<int>> Handler { get; init; }
    public required Func<Task<int>> WizardHandler { get; init; }
    public string? Icon { get; init; }
}

/// <summary>
/// Registry of all available CLI commands.
/// AOT-compatible - uses static registration, no reflection.
/// </summary>
public static class CommandRegistry
{
    private static readonly List<CommandInfo> _commands = new();

    /// <summary>
    /// All registered commands.
    /// </summary>
    public static IReadOnlyList<CommandInfo> Commands => _commands;

    /// <summary>
    /// Registers all built-in commands.
    /// Call this once at startup.
    /// </summary>
    public static void RegisterBuiltInCommands()
    {
        Register(new CommandInfo
        {
            Name = "build",
            Description = "Generate signatures and manifest for a directory",
            Icon = ":hammer:",
            Handler = BuildCommand.RunAsync,
            WizardHandler = BuildCommand.RunWizardAsync
        });

        Register(new CommandInfo
        {
            Name = "patch",
            Description = "Apply delta patches to update local files",
            Icon = ":inbox_tray:",
            Handler = PatchCommand.RunAsync,
            WizardHandler = PatchCommand.RunWizardAsync
        });

        Register(new CommandInfo
        {
            Name = "verify",
            Description = "Verify local files against manifest",
            Icon = ":check_mark_button:",
            Handler = VerifyCommand.RunAsync,
            WizardHandler = VerifyCommand.RunWizardAsync
        });

        Register(new CommandInfo
        {
            Name = "upload",
            Description = "Upload build artifacts to S3-compatible storage",
            Icon = "📤",
            Handler = UploadCommand.RunAsync,
            WizardHandler = UploadCommand.RunWizardAsync
        });

        Register(new CommandInfo
        {
            Name = "info",
            Description = "Display information about manifest or signature files",
            Icon = ":red_question_mark:",
            Handler = InfoCommand.RunAsync,
            WizardHandler = InfoCommand.RunWizardAsync
        });
    }

    /// <summary>
    /// Registers a command.
    /// </summary>
    public static void Register(CommandInfo command)
    {
        _commands.Add(command);
    }

    /// <summary>
    /// Finds a command by name.
    /// </summary>
    public static CommandInfo? Find(string name)
    {
        return _commands.FirstOrDefault(c =>
            c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Shows interactive menu to select a command.
    /// </summary>
    public static async Task<int> ShowInteractiveMenuAsync()
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(
            new FigletText("PatchSync")
                .Color(Color.Blue));

        AnsiConsole.MarkupLine("[grey]Delta patching tool for game updates[/]\n");

        var choices = _commands
            .Select(c => $"{c.Icon ?? ">"} {c.Name,-10} - {c.Description}")
            .Append(":cross_mark: Exit")
            .ToList();

        var selection = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[yellow]Select a command:[/]")
                .PageSize(10)
                .HighlightStyle(new Style(Color.Green))
                .AddChoices(choices));

        if (selection.Contains("Exit"))
        {
            return 0;
        }

        // Extract command name from selection
        var commandName = selection.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1) // Skip icon
            .FirstOrDefault()?
            .Trim();

        var command = Find(commandName ?? "");
        if (command == null)
        {
            AnsiConsole.MarkupLine("[red]Command not found[/]");
            return 1;
        }

        AnsiConsole.Clear();
        AnsiConsole.MarkupLine($"[bold blue]{command.Icon} {command.Name.ToUpperInvariant()}[/]\n");

        return await command.WizardHandler();
    }
}
