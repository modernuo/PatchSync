using PatchSync.Common.Manifest;

namespace PatchSync.Manifest;

public static class ManifestBuilder
{
  public static PatchManifest GenerateManifest(string channel, ManifestFileEntry[] fileEntries)
  {
    return new PatchManifest
    {
      Channel = channel,
      Date = DateTime.UtcNow,
      Files = fileEntries
    };
  }
}