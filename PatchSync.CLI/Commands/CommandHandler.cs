using Spectre.Console;

namespace PatchSync.CLI.Commands;

public static class CommandHandler
{
    private static readonly List<ICommand> _commands = new();

    public static void Register(ICommand command)
    {
        _commands.Add(command);
    }

    public static ICommand PromptCommands()
    {
        return AnsiConsole.Prompt(
            new SelectionPrompt<ICommand>()
                .Title("What would you like to do?")
                .AddChoices(_commands)
                .UseConverter(command => command.Name)
        );
    }
}
