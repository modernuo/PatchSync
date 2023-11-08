using PatchSync.CLI;
using PatchSync.CLI.Commands;

// Register commands
CommandHandler.Register(new BuildSignatures());
CommandHandler.Register(new UploadSignatures());
CommandHandler.Register(new QuitCLI());


var cliContext = new CLIContext();

do
{
  var command = CommandHandler.PromptCommands();
  command.ExecuteCommand(cliContext);
} while (!cliContext.TryGetProperty("exit", out bool exit) || !exit);