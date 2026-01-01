using System.Security.Cryptography;
using PatchSync.Common.Chunking;
using PatchSync.Common.Hashing;
using PatchSync.Common.Signatures;
using PatchSync.SDK.Delta;
using PatchSync.SDK.Sources;

namespace PatchSync.TestClient;

/// <summary>
/// Diagnostic tool to analyze delta between two files.
/// </summary>
public static class DeltaAnalyzer
{
    public static async Task AnalyzeAsync(string oldFile, string newFile, ChunkingOptions? options = null)
    {
        if (!File.Exists(oldFile))
        {
            Console.WriteLine($"Error: Old file not found: {oldFile}");
            return;
        }
        if (!File.Exists(newFile))
        {
            Console.WriteLine($"Error: New file not found: {newFile}");
            return;
        }

        var oldInfo = new FileInfo(oldFile);
        var newInfo = new FileInfo(newFile);

        Console.WriteLine("=== DELTA ANALYSIS ===");
        Console.WriteLine();
        Console.WriteLine($"Old file: {oldFile}");
        Console.WriteLine($"  Size: {FormatSize(oldInfo.Length)}");
        Console.WriteLine();
        Console.WriteLine($"New file: {newFile}");
        Console.WriteLine($"  Size: {FormatSize(newInfo.Length)}");
        Console.WriteLine();
        Console.WriteLine($"Size difference: {FormatSize(Math.Abs(newInfo.Length - oldInfo.Length))} ({(newInfo.Length > oldInfo.Length ? "larger" : "smaller")})");
        Console.WriteLine();

        // Use default chunking options if not provided
        options ??= new ChunkingOptions
        {
            MinSize = 16 * 1024,      // 16 KB
            AverageSize = 64 * 1024,  // 64 KB
            MaxSize = 256 * 1024      // 256 KB
        };

        Console.WriteLine($"Chunking options:");
        Console.WriteLine($"  Min: {FormatSize(options.MinSize)}, Avg: {FormatSize(options.AverageSize)}, Max: {FormatSize(options.MaxSize)}");
        Console.WriteLine();

        var chunker = new FastCDCChunker();

        // Chunk both files
        Console.Write("Chunking old file... ");
        var oldChunks = await ChunkFileAsync(oldFile, chunker, options);
        Console.WriteLine($"{oldChunks.Count} chunks");

        Console.Write("Chunking new file... ");
        var newChunks = await ChunkFileAsync(newFile, chunker, options);
        Console.WriteLine($"{newChunks.Count} chunks");
        Console.WriteLine();

        // Build signature for new file (what we're trying to reconstruct)
        var newSignature = new SignatureFile
        {
            AlgorithmId = "fastcdc-v1",
            MinSize = options.MinSize,
            AverageSize = options.AverageSize,
            MaxSize = options.MaxSize,
            Chunks = newChunks.Select(c => new SignatureChunk(c.Offset, c.Length, c.Hash)).ToList()
        };

        // Build local source from old file
        var localSource = LocalFileSource.Build(oldFile, chunker, options);

        // Calculate delta
        var calculator = new DeltaCalculator();
        var plan = calculator.Calculate(newSignature, localSource);

        // Analyze results
        Console.WriteLine("=== DELTA PLAN ===");
        Console.WriteLine();
        Console.WriteLine($"Total chunks in new file: {newChunks.Count}");
        Console.WriteLine($"Chunks found locally:     {plan.ChunksToCopy} ({plan.LocalReuseRatio:P1})");
        Console.WriteLine($"Chunks to download:       {plan.ChunksToDownload} ({1 - plan.LocalReuseRatio:P1})");
        Console.WriteLine();
        Console.WriteLine($"Bytes to copy locally:    {FormatSize(plan.BytesToCopy)}");
        Console.WriteLine($"Bytes to download:        {FormatSize(plan.BytesToDownload)}");
        Console.WriteLine($"Total bytes:              {FormatSize(plan.TotalBytes)}");
        Console.WriteLine();

        // Show chunk size distribution
        Console.WriteLine("=== CHUNK SIZE DISTRIBUTION (New File) ===");
        var sizeGroups = newChunks
            .GroupBy(c => GetSizeBucket(c.Length))
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var group in sizeGroups)
        {
            var count = group.Count();
            var totalSize = group.Sum(c => (long)c.Length);
            Console.WriteLine($"  {group.Key}: {count} chunks, {FormatSize(totalSize)}");
        }
        Console.WriteLine();

        // Show matching analysis
        Console.WriteLine("=== MATCHING ANALYSIS ===");

        // Build hash set of old chunks for quick lookup
        var oldChunkHashes = new HashSet<string>(oldChunks.Select(c => Convert.ToHexString(c.Hash)));
        var newChunkHashes = newChunks.Select(c => Convert.ToHexString(c.Hash)).ToList();

        var matchingHashes = newChunkHashes.Where(h => oldChunkHashes.Contains(h)).ToList();
        var uniqueToNew = newChunkHashes.Where(h => !oldChunkHashes.Contains(h)).ToList();

        Console.WriteLine($"Unique hashes in old file: {oldChunkHashes.Count}");
        Console.WriteLine($"Unique hashes in new file: {new HashSet<string>(newChunkHashes).Count}");
        Console.WriteLine($"Matching hashes:           {matchingHashes.Count}");
        Console.WriteLine($"New hashes (to download):  {uniqueToNew.Count}");
        Console.WriteLine();

        // Show where the downloads are concentrated
        Console.WriteLine("=== DOWNLOAD DISTRIBUTION ===");
        var downloads = plan.Actions.OfType<DownloadRemote>().ToList();
        var copies = plan.Actions.OfType<CopyLocal>().ToList();

        if (downloads.Count > 0)
        {
            var ranges = plan.GetDownloadRanges(maxGap: 0);
            Console.WriteLine($"Download ranges (no coalescing): {ranges.Count}");

            // Show first 10 download ranges
            Console.WriteLine("First 10 download ranges:");
            foreach (var range in ranges.Take(10))
            {
                var pct = (double)range.Offset / newInfo.Length * 100;
                Console.WriteLine($"  Offset {range.Offset:N0} ({pct:F1}%) - Length {FormatSize(range.Length)}");
            }
            if (ranges.Count > 10)
            {
                Console.WriteLine($"  ... and {ranges.Count - 10} more ranges");
            }
            Console.WriteLine();

            // Show largest download ranges
            Console.WriteLine("Top 10 largest download ranges:");
            foreach (var range in ranges.OrderByDescending(r => r.Length).Take(10))
            {
                var pct = (double)range.Offset / newInfo.Length * 100;
                Console.WriteLine($"  Offset {range.Offset:N0} ({pct:F1}%) - Length {FormatSize(range.Length)}");
            }
        }
        else
        {
            Console.WriteLine("No downloads needed - 100% local match!");
        }
        Console.WriteLine();

        // File header analysis (first 1KB)
        Console.WriteLine("=== FILE HEADER COMPARISON ===");
        await CompareHeadersAsync(oldFile, newFile, 1024);
    }

    private static async Task<List<ChunkBoundary>> ChunkFileAsync(
        string filePath,
        IChunker chunker,
        ChunkingOptions options)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.SequentialScan);

        return chunker.Chunk(stream, options).ToList();
    }

    private static async Task CompareHeadersAsync(string oldFile, string newFile, int bytes)
    {
        var oldHeader = new byte[bytes];
        var newHeader = new byte[bytes];

        await using (var fs = File.OpenRead(oldFile))
        {
            await fs.ReadAsync(oldHeader);
        }
        await using (var fs = File.OpenRead(newFile))
        {
            await fs.ReadAsync(newHeader);
        }

        var differences = 0;
        var firstDiff = -1;
        for (int i = 0; i < bytes; i++)
        {
            if (oldHeader[i] != newHeader[i])
            {
                differences++;
                if (firstDiff < 0) firstDiff = i;
            }
        }

        Console.WriteLine($"First {bytes} bytes comparison:");
        Console.WriteLine($"  Differences: {differences} bytes");
        if (firstDiff >= 0)
        {
            Console.WriteLine($"  First difference at offset: {firstDiff}");
        }

        // Show first 64 bytes as hex
        Console.WriteLine();
        Console.WriteLine("Old file header (first 64 bytes):");
        Console.WriteLine($"  {Convert.ToHexString(oldHeader.Take(64).ToArray())}");
        Console.WriteLine("New file header (first 64 bytes):");
        Console.WriteLine($"  {Convert.ToHexString(newHeader.Take(64).ToArray())}");

        // Check for common UOP signature
        if (oldHeader[0] == 'M' && oldHeader[1] == 'Y' && oldHeader[2] == 'P')
        {
            Console.WriteLine();
            Console.WriteLine("Detected UOP file format (MYP signature)");
            // UOP files have a header structure that might contain offsets/counts
            // that change frequently
        }
    }

    private static string GetSizeBucket(int size)
    {
        return size switch
        {
            < 1024 => "< 1 KB",
            < 4096 => "1-4 KB",
            < 16384 => "4-16 KB",
            < 65536 => "16-64 KB",
            < 262144 => "64-256 KB",
            _ => "> 256 KB"
        };
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    /// <summary>
    /// Creates test TAR files to verify CDC behavior with sequential archives.
    /// </summary>
    public static async Task TestTarBehaviorAsync(string outputDir)
    {
        Console.WriteLine("=== TAR FORMAT CDC TEST ===");
        Console.WriteLine();
        Console.WriteLine("Creating test TAR files to verify CDC handles sequential archives...");
        Console.WriteLine();

        Directory.CreateDirectory(outputDir);

        // Create deterministic test data
        var file1Data = CreateTestData(100 * 1024, seed: 1);  // 100KB
        var file2Data = CreateTestData(200 * 1024, seed: 2);  // 200KB
        var file3Data = CreateTestData(150 * 1024, seed: 3);  // 150KB

        var file1Modified = CreateTestData(120 * 1024, seed: 10); // Different data, different size

        // Create TAR 1: file1, file2, file3
        var tar1Path = Path.Combine(outputDir, "test1.tar");
        await CreateSimpleTarAsync(tar1Path, new[]
        {
            ("file1.bin", file1Data),
            ("file2.bin", file2Data),
            ("file3.bin", file3Data)
        });

        // Create TAR 2: file1 (modified), file2 (same), file3 (same)
        var tar2Path = Path.Combine(outputDir, "test2.tar");
        await CreateSimpleTarAsync(tar2Path, new[]
        {
            ("file1.bin", file1Modified),
            ("file2.bin", file2Data),  // Same content!
            ("file3.bin", file3Data)   // Same content!
        });

        Console.WriteLine($"TAR 1: {tar1Path} ({FormatSize(new FileInfo(tar1Path).Length)})");
        Console.WriteLine($"TAR 2: {tar2Path} ({FormatSize(new FileInfo(tar2Path).Length)})");
        Console.WriteLine();
        Console.WriteLine("Hypothesis: file2 and file3 chunks should match despite being at different offsets.");
        Console.WriteLine();

        // Now analyze
        await AnalyzeAsync(tar1Path, tar2Path);
    }

    private static async Task CreateSimpleTarAsync(string path, (string name, byte[] data)[] files)
    {
        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);

        foreach (var (name, data) in files)
        {
            // Write TAR header (512 bytes)
            var header = new byte[512];

            // File name (100 bytes)
            var nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
            Array.Copy(nameBytes, header, Math.Min(nameBytes.Length, 100));

            // File mode (8 bytes at offset 100) - "0000644\0"
            System.Text.Encoding.ASCII.GetBytes("0000644\0").CopyTo(header, 100);

            // UID (8 bytes at offset 108) - "0000000\0"
            System.Text.Encoding.ASCII.GetBytes("0000000\0").CopyTo(header, 108);

            // GID (8 bytes at offset 116) - "0000000\0"
            System.Text.Encoding.ASCII.GetBytes("0000000\0").CopyTo(header, 116);

            // File size in octal (12 bytes at offset 124)
            var sizeOctal = Convert.ToString(data.Length, 8).PadLeft(11, '0') + "\0";
            System.Text.Encoding.ASCII.GetBytes(sizeOctal).CopyTo(header, 124);

            // Mtime (12 bytes at offset 136) - "00000000000\0"
            System.Text.Encoding.ASCII.GetBytes("00000000000\0").CopyTo(header, 136);

            // Checksum placeholder (8 bytes at offset 148) - spaces
            for (int i = 148; i < 156; i++) header[i] = (byte)' ';

            // Type flag (1 byte at offset 156) - '0' for regular file
            header[156] = (byte)'0';

            // Calculate checksum
            int checksum = 0;
            for (int i = 0; i < 512; i++) checksum += header[i];
            var checksumOctal = Convert.ToString(checksum, 8).PadLeft(6, '0') + "\0 ";
            System.Text.Encoding.ASCII.GetBytes(checksumOctal).CopyTo(header, 148);

            await fs.WriteAsync(header);

            // Write file data
            await fs.WriteAsync(data);

            // Pad to 512-byte boundary
            var padding = (512 - (data.Length % 512)) % 512;
            if (padding > 0)
            {
                await fs.WriteAsync(new byte[padding]);
            }
        }

        // Write two empty blocks to mark end of archive
        await fs.WriteAsync(new byte[1024]);
    }

    private static byte[] CreateTestData(int size, int seed)
    {
        var rng = new Random(seed);
        var data = new byte[size];
        rng.NextBytes(data);
        return data;
    }
}
