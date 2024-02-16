using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text.Json;
using PatchSync.Common;
using PatchSync.Common.LocalFiles;
using PatchSync.Common.Manifest;
using PatchSync.Common.Text;

namespace PatchSync.SDK;

public partial class PatchSyncClient
{
    private PatchManifest _manifest;

    public class ManifestDownloadResult
    {
        public PatchSyncClient Client { get; }
        public PatchManifest Manifest { get; }

        internal ManifestDownloadResult(PatchSyncClient client, PatchManifest manifest)
        {
            Client = client;
            Manifest = manifest;
        }
    }

    public async Task<ManifestDownloadResult> DownloadManifest(IProgress<DownloadProgress>? progress = default)
    {
        if (CancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The operation was cancelled before it could start.");
        }

        var url = GetFileUri("manifest.json");

        using (var memoryStream = new MemoryStream())
        {
            await Downloader.DownloadFileAsync("manifest.json", url, memoryStream, progress, CancellationToken);

            // Reset the memory stream so we can read.
            memoryStream.Seek(0, SeekOrigin.Begin);
            _manifest = await JsonSerializer.DeserializeAsync<PatchManifest>(
                memoryStream,
                JsonSerializerOptions.Default,
                CancellationToken
            ) ?? throw new InvalidOperationException("Could not properly download the manifest.");
        }

        // TODO: Do this at the end
        // Replace the local manifest with the new one
        // using (var stream = File.OpenWrite(_localManifestPath))
        // {
        //     await JsonSerializer.SerializeAsync(stream, manifest, JsonSerializerOptions.Default, _cancellationToken);
        // }

        return new ManifestDownloadResult(this, _manifest);
    }
}

public static class ManifestDownloadResultExt
{
    public static async Task<PatchSyncClient.FileChanges> GetFileChanges(
        this Task<PatchSyncClient.ManifestDownloadResult> resultTask, IProgress<FileChangeProgress>? progress = default
    )
    {
        var result = await resultTask.ConfigureAwait(false);
        return await result.GetFileChanges(progress).ConfigureAwait(false);
    }

    public static async Task<PatchSyncClient.FileChanges> GetFileChanges(
        this PatchSyncClient.ManifestDownloadResult result, IProgress<FileChangeProgress>? progress = default
    )
    {
        var cancellationToken = result.Client.CancellationToken;

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The operation was cancelled before it could start.");
        }

        var hasher = new XxHash3();
        var manifest = result.Manifest;
        var installationPath = result.Client.LocalInstallationPath;

        var filesToChange = manifest.Files.Where(
            entry =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("The operation was cancelled.");
                }

                var fullPath = Path.Combine(installationPath, entry.FilePath);

                if (!File.Exists(fullPath))
                {
                    if (entry.Command is not ManifestFileCommand.Delete)
                    {
                        progress?.Report(new FileChangeProgress(entry.FilePath, FileChange.Create));
                        return true;
                    }

                    return false;
                }

                if (entry.Command is ManifestFileCommand.UpdateIfMissing)
                {
                    return false;
                }

                if (entry.Command is ManifestFileCommand.AlwaysFullUpdate || string.IsNullOrWhiteSpace(entry.Hash))
                {
                    progress?.Report(new FileChangeProgress(entry.FilePath, FileChange.FullUpdate));
                    return true;
                }

                if (entry.Command is ManifestFileCommand.DeltaUpdate)
                {
                    using var file = File.Open(fullPath, FileMode.Open, FileAccess.Read);
                    hasher.Append(file);
                    var existingHash = hasher.GetCurrentHashAsUInt64().ToString();
                    hasher.Reset();

                    if (existingHash != entry.FastHash)
                    {
                        progress?.Report(new FileChangeProgress(entry.FilePath, FileChange.DeltaUpdate));
                        return true;
                    }
                }
                else if (entry.Command is ManifestFileCommand.Delete)
                {
                    progress?.Report(new FileChangeProgress(entry.FilePath, FileChange.Delete));
                    return true;
                }

                if (entry.Command is ManifestFileCommand.UpdateIfFullHashMismatch)
                {
                    using var stream = File.Open(fullPath, FileMode.Open, FileAccess.Read);
                    Span<byte> fullHashBuffer;
#if NET6_0_OR_GREATER
                    fullHashBuffer = stackalloc byte[32];
                    SHA256.HashData(stream, fullHashBuffer);
#else
                    using (var sha256 = SHA256.Create())
                    {
                        fullHashBuffer = sha256.ComputeHash(stream);
                    }
#endif

                    // TODO: Use binary comparison instead of string comparison
                    if (fullHashBuffer.ToHexString() != entry.Hash)
                    {
                        progress?.Report(new FileChangeProgress(entry.FilePath, FileChange.FullUpdate));
                        return true;
                    }
                }

                return false;
            }
        ).ToArray();

        return new PatchSyncClient.FileChanges(result.Client, manifest, filesToChange);
    }
}
