namespace PatchSync.CLI.Commands;

public class QuitCLI : ICommand
{
    public string Name => "Quit";

    public void ExecuteCommand(CLIContext cliContext)
    {
        cliContext.SetProperty("exit", true);
    }
}
