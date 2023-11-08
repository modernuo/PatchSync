using PatchSync.Common.Manifest;

namespace PatchSync.Manifest;

public static class ManifestBuilder
{
    public static PatchManifest GenerateManifest(string channel, ManifestFileEntry[] fileEntries, DateTime date) =>
        new()
        {
            Channel = channel,
            Date = date,
            Files = fileEntries
        };
}
