namespace PatchSync.Common.Manifest;

public record PatchManifest
{
    public string Version { get; init; }

    public DateTime Date { get; init; }

    public string Channel { get; init; }

    public ManifestFileEntry[] Files { get; init; }
}
