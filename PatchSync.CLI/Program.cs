using System.Text;
using PatchSync.CLI;
using PatchSync.CLI.Commands;

Console.OutputEncoding = Encoding.UTF8;

// Register commands
// CommandHandler.Register(new TestPatchDownload());
// CommandHandler.Register(new BuildSignatures());
// CommandHandler.Register(new PatchInstallation());
// CommandHandler.Register(new UploadSignatures());
// CommandHandler.Register(new QuitCLI());

var cliContext = new CLIContext();

new TestPatchDownload().ExecuteCommand(cliContext);

// do
// {
//     var command = CommandHandler.PromptCommands();
//     command.ExecuteCommand(cliContext);
// } while (!cliContext.TryGetProperty("exit", out bool exit) || !exit);
