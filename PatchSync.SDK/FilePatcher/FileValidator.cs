using System.Collections.Concurrent;
using System.IO.Hashing;
using System.IO.MemoryMappedFiles;
using PatchSync.Common.Manifest;

namespace PatchSync.SDK;

public static class FileValidator
{
    public static ManifestFileEntry[] ValidateFiles(
        PatchManifest manifest, string baseFolder, Func<ValidationResult>? progressCallback = null
    )
    {
        if (manifest.Files.Length == 0)
        {
            return Array.Empty<ManifestFileEntry>();
        }

        var queue = new ConcurrentQueue<ManifestFileEntry>();
        var concurrency = Math.Max(Environment.ProcessorCount, 1);

        Parallel.ForEach(
            manifest.Files,
            new ParallelOptions { MaxDegreeOfParallelism = concurrency },
            file => ValidateManifestFileEntry(baseFolder, file)
        );

        return queue.ToArray();
    }

    public static bool ValidateFastHash(Stream inputStream, string fastHash)
    {
        var xxHash3 = new XxHash3();
        xxHash3.Append(inputStream);

        // Reset stream
        inputStream.Seek(0, SeekOrigin.Begin);

        var hash = xxHash3.GetCurrentHashAsUInt64();
        return hash.ToString() == fastHash;
    }

    private static void ValidateManifestFileEntry(string baseFolder, ManifestFileEntry file)
    {
        var xxHash3 = new XxHash3();
        using var mmf = MemoryMappedFile.CreateFromFile(file.FilePath, FileMode.Open);
        using var mmStream = mmf.CreateViewStream();

        // if (xxHash3.)
    }
}
