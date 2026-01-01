using System.Text;
using PatchSync.CLI.Commands;
using PatchSync.CLI.Wizard;

Console.OutputEncoding = Encoding.UTF8;

// Check if running in interactive mode (no arguments or only --workspace)
if (args.Length == 0 || (args.Length == 2 && (args[0] == "-w" || args[0] == "--workspace")))
{
    // Interactive mode: Handle Ctrl+C by signaling our cancellation token
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true; // Prevent process termination
        InteractiveCancellation.Instance.Cancel(); // Signal cancellation to prompts
    };

    // Extract workspace path if provided
    string? workspacePath = null;
    if (args.Length == 2)
    {
        workspacePath = args[1];
    }

    return await InteractiveMode.RunAsync(workspacePath);
}

// CLI mode: Use System.CommandLine with its default Ctrl+C handling
return await CliBuilder.InvokeAsync(args);
