using System.Security.Cryptography;
using PatchSync.Common.LocalFiles;
using PatchSync.Common.Manifest;
using PatchSync.Common.Text;
using PatchSync.SDK.Threading;

namespace PatchSync.SDK;

public partial class PatchSyncClient
{
    public class FileChanges
    {
        public PatchSyncClient Client { get; }
        public PatchManifest Manifest { get; }
        public ManifestFileEntry[] Files { get; }

        internal FileChanges(PatchSyncClient client, PatchManifest manifest, ManifestFileEntry[] files)
        {
            Client = client;
            Manifest = manifest;
            Files = files;
        }
    }
}

public static class FileChangesExt
{
    public static async Task<PatchSyncClient.PatchFilesResult> PatchFiles(
        this Task<PatchSyncClient.FileChanges> changesTask, IProgress<FilePatchProgress>? progress = default
    )
    {
        var changes = await changesTask.ConfigureAwait(false);
        return await changes.PatchFiles(progress).ConfigureAwait(false);
    }

    public static async Task<PatchSyncClient.PatchFilesResult> PatchFiles(
        this PatchSyncClient.FileChanges changes, IProgress<FilePatchProgress>? progress = default
    )
    {
        var cancellationToken = changes.Client.CancellationToken;
        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The operation was cancelled before it could start.");
        }

        var installationPath = changes.Client.LocalInstallationPath;

        var fullHashBuffer = new byte[32];

        // (relativeFilePath, shouldDoFullUpdate)
        List<(string, ManifestFileEntry, bool)> filesToPatch = [];

        for (var i = 0; i < changes.Files.Length; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("The operation was cancelled.");
            }

            var command = changes.Files[i];
            var file = Path.Combine(installationPath, command.FilePath);

            switch (command.Command)
            {
                case ManifestFileCommand.Delete:
                    {
                        progress?.Report(new FilePatchProgress(command.FilePath, FileChange.Delete, 0));
                        File.Delete(file);
                        progress?.Report(new FilePatchProgress(command.FilePath, FileChange.Delete, 1));
                        break;
                    }
                case ManifestFileCommand.AlwaysFullUpdate:
                    {
                        progress?.Report(new FilePatchProgress(command.FilePath, FileChange.FullUpdate, 0));
                        File.Delete(file);
                        filesToPatch.Add((installationPath, command, true));
                        break;
                    }
                case ManifestFileCommand.UpdateIfFullHashMismatch:
                    {
                        progress?.Report(new FilePatchProgress(command.FilePath, FileChange.FullUpdate, 0));
                        var fi = new FileInfo(file);

                        var hasChanges = command.FileSize != fi.Length;
                        if (!hasChanges)
                        {
                            using var stream = File.Open(file, FileMode.Open, FileAccess.Read);
                            SHA256.HashData(stream, fullHashBuffer);
                            hasChanges = command.Hash != fullHashBuffer.ToHexString();
                        }

                        if (hasChanges)
                        {
                            File.Delete(file);

                            // TODO: Optimize by checking against existing local manifest file and determining if this file
                            // has a different full hash.
                            filesToPatch.Add((installationPath, command, true));
                        }
                        else
                        {
                            progress?.Report(new FilePatchProgress(command.FilePath, FileChange.FullUpdate, 1));
                        }

                        break;
                    }
                case ManifestFileCommand.DeltaUpdate:
                    {
                        progress?.Report(new FilePatchProgress(command.FilePath, FileChange.DeltaUpdate, 0));
                        // TODO: Optimize by checking against existing local manifest file and determining if this file
                        // has a different full hash.
                        filesToPatch.Add((installationPath, command, false));
                        break;
                    }
            }
        }

        ThreadWorker<(string LocalInstallationPath, ManifestFileEntry FileEntry, bool FullUpdate)>.MapParallel(
            filesToPatch,
            fileChangeTuple => DoPatchFiles(fileChangeTuple, progress, cancellationToken),
            cancellationToken
        );

        // TODO: Gather all files that changed and how they changed.
        return new PatchSyncClient.PatchFilesResult(changes.Client, changes.Manifest, changes.Files);
    }

    private static void DoPatchFiles(
        (string localInstallationpath, ManifestFileEntry fileEntry, bool fullUpdate) fileEntry,
        IProgress<FilePatchProgress> progress, CancellationToken cancellationToken
    )
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The operation was cancelled before it could start.");
        }

        var (localInstallationPath, command, fullUpdate) = fileEntry;
        var file = Path.Combine(localInstallationPath, command.FilePath);

        var tempFolder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempFolder);

        if (fullUpdate) // Full Update
        {
            progress?.Report(new FilePatchProgress(command.FilePath, FileChange.FullUpdate, 0));

            progress?.Report(new FilePatchProgress(command.FilePath, FileChange.FullUpdate, 1));
        }
        else // Delta Update
        {
            progress?.Report(new FilePatchProgress(command.FilePath, FileChange.DeltaUpdate, 0));
        }
    }
}
