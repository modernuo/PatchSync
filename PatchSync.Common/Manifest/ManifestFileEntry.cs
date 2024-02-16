namespace PatchSync.Common.Manifest;

public class ManifestFileEntry
{
    public ManifestFileEntry(ManifestFileCommand command, string filePath, long fileSize)
    {
        Command = command;
        FilePath = filePath;
        FileSize = fileSize;
    }

    public ManifestFileCommand Command { get; }
    public string FilePath { get; }

    public long FileSize { get; }

    // If it is not the standard chunk size
    public int ChunkSize { get; init; }

    // XxHash
    public string? FastHash { get; init; }

    // SHA256 - For file integrity checks
    public string? Hash { get; init; }

    public static int GetChunkSize(long fileSize) =>
        fileSize > 1024 * 1024 * 256 ? (int)Utilities.RoundUp((long)Math.Sqrt(fileSize)) : 1024;
}
