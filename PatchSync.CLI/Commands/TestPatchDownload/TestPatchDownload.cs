using PatchSync.SDK.Client;

namespace PatchSync.CLI.Commands;

public partial class TestPatchDownload : ICommand
{
    public string Name => "Test Patch Download";

    public async void ExecuteCommand(CLIContext cliContext)
    {
        var pathSyncClient = new PatchSyncClient("https://testfile-org.mavenshotels.com");

        var index = 0;
        await foreach (var part in pathSyncClient.DownloadDeltaPatchFileAsync(
                           "TESTFILE.ORG/testfile.org-5GB.dat",
                           [(0, 131072), (1000000, 1131072), (1000000000, 1000131072)],
                           new Progress<IDownloadProgress>(
                               download =>
                               {
                                   Console.WriteLine($"Downloaded {download.TotalDownloaded} of {download.TotalSize} bytes.");
                                   Console.WriteLine($"Speed: {download.Speed} bytes/sec.");
                               })
                       ))
        {
            var partIndex = index++;
            Console.WriteLine($"Writing part {partIndex}..");
            await using var fileStream = File.Create("testfile.org-5GB.dat.1");
            await part.CopyToAsync(fileStream);
            Console.WriteLine($"Completed part {partIndex}.");
        }
    }
}
