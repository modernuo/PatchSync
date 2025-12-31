using PatchSync.CLI.Commands;
using Spectre.Console;

// Register all commands
CommandRegistry.RegisterBuiltInCommands();

// If no arguments, show interactive menu
if (args.Length == 0)
{
    return await CommandRegistry.ShowInteractiveMenuAsync();
}

// Parse command from arguments
var commandName = args[0].ToLowerInvariant();

// Handle built-in options
if (commandName is "help" or "--help" or "-h")
{
    return ShowHelp();
}

if (commandName is "--version" or "-v")
{
    return ShowVersion();
}

// Find and execute command
var command = CommandRegistry.Find(commandName);
if (command == null)
{
    return ShowUnknownCommand(commandName);
}

return await command.Handler(args[1..]);

static int ShowHelp()
{
    AnsiConsole.MarkupLine("[bold blue]PatchSync CLI v2.0.0[/]");
    AnsiConsole.MarkupLine("Delta patching tool for game updates\n");
    AnsiConsole.MarkupLine("[yellow]Usage:[/]");
    AnsiConsole.MarkupLine("  patchsync <command> [[options]]");
    AnsiConsole.MarkupLine("  patchsync                        (interactive mode)\n");
    AnsiConsole.MarkupLine("[yellow]Commands:[/]");

    foreach (var cmd in CommandRegistry.Commands)
    {
        AnsiConsole.MarkupLine($"  [green]{cmd.Name,-10}[/] {cmd.Description}");
    }

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[yellow]Examples:[/]");
    AnsiConsole.MarkupLine("  patchsync build -i ./game -o ./output -v 1.0.0");
    AnsiConsole.MarkupLine("  patchsync patch -u https://cdn.example.com -p ./game");
    AnsiConsole.MarkupLine("  patchsync verify -u https://cdn.example.com -p ./game");
    AnsiConsole.MarkupLine("  patchsync upload -b ./output");
    AnsiConsole.MarkupLine("  patchsync info manifest.json\n");
    AnsiConsole.MarkupLine("Run [green]patchsync <command> --help[/] for command-specific options.");
    AnsiConsole.MarkupLine("Run [green]patchsync[/] without arguments for interactive mode.");
    return 0;
}

static int ShowVersion()
{
    AnsiConsole.WriteLine("patchsync 2.0.0");
    return 0;
}

static int ShowUnknownCommand(string cmd)
{
    AnsiConsole.MarkupLine($"[red]Error:[/] Unknown command '{cmd}'");
    AnsiConsole.MarkupLine("Run [green]patchsync --help[/] for usage information.");
    return 1;
}
