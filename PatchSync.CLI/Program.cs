using System.Text;
using PatchSync.CLI.Commands;

Console.OutputEncoding = Encoding.UTF8;

return await CliBuilder.InvokeAsync(args);
