namespace PatchSync.CLI.Commands;

public interface ICommand
{
    string Name { get; }
    void ExecuteCommand(CLIContext cliContext);
}
