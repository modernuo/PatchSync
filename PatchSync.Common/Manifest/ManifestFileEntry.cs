namespace PatchSync.Common.Manifest;

public record ManifestFileEntry(ManifestFileCommand Command, string FilePath, uint FileSize, string Hash)
{
  public ManifestFileCommand Command { get; } = Command;
  public string FilePath { get; } = FilePath;
  public uint FileSize { get; } = FileSize;
  public string Hash { get; } = Hash;
}