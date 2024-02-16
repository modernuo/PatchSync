using PatchSync.Common.Manifest;

namespace PatchSync.SDK;

public partial class PatchSyncClient
{
    public class PatchFilesResult
    {
        public PatchSyncClient Client { get; }
        public PatchManifest Manifest { get; }
        public ManifestFileEntry[] Files { get; }

        internal PatchFilesResult(PatchSyncClient client, PatchManifest manifest, ManifestFileEntry[] files)
        {
            Client = client;
            Manifest = manifest;
            Files = files;
        }
    }
}
