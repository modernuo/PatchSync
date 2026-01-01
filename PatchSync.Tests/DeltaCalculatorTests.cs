using PatchSync.Common.Chunking;
using PatchSync.Common.Signatures;
using PatchSync.SDK.Delta;
using PatchSync.SDK.Sources;

namespace PatchSync.Tests;

public class DeltaCalculatorTests
{
    private readonly IChunker _chunker = new FastCDCChunker();
    private readonly ChunkingOptions _options = new()
    {
        MinSize = 256,
        AverageSize = 512,
        MaxSize = 1024
    };

    [Fact]
    public void Calculate_WhenAllChunksMatch_ReturnsAllCopyLocal()
    {
        // Arrange: Create a file and its signature
        var data = CreateDeterministicData(4096, seed: 42);
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, data);

            // Build signature from the same data
            using var stream = new MemoryStream(data);
            var chunks = _chunker.Chunk(stream, _options).ToList();
            var signature = new SignatureFile
            {
                AlgorithmId = "fastcdc-v1",
                MinSize = _options.MinSize,
                AverageSize = _options.AverageSize,
                MaxSize = _options.MaxSize,
                Chunks = chunks.Select(c => new SignatureChunk(c.Offset, c.Length, c.Hash)).ToList()
            };

            // Build local source from the file
            var localSource = LocalFileSource.Build(tempFile, _chunker, _options);
            var calculator = new DeltaCalculator();

            // Act
            var plan = calculator.Calculate(signature, localSource);

            // Assert: All chunks should be copy local
            Assert.Equal(0, plan.BytesToDownload);
            Assert.Equal(data.Length, plan.BytesToCopy);
            Assert.Equal(1.0, plan.LocalReuseRatio, precision: 2);
            Assert.All(plan.Actions, a => Assert.IsType<CopyLocal>(a));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Calculate_WhenNoLocalFile_ReturnsAllDownloadRemote()
    {
        // Arrange: Create signature without any local file
        var data = CreateDeterministicData(4096, seed: 123);
        using var stream = new MemoryStream(data);
        var chunks = _chunker.Chunk(stream, _options).ToList();
        var signature = new SignatureFile
        {
            AlgorithmId = "fastcdc-v1",
            MinSize = _options.MinSize,
            AverageSize = _options.AverageSize,
            MaxSize = _options.MaxSize,
            Chunks = chunks.Select(c => new SignatureChunk(c.Offset, c.Length, c.Hash)).ToList()
        };

        // Empty composite source
        var localSource = new CompositeChunkSource();
        var calculator = new DeltaCalculator();

        // Act
        var plan = calculator.Calculate(signature, localSource);

        // Assert: All chunks should be download
        Assert.Equal(data.Length, plan.BytesToDownload);
        Assert.Equal(0, plan.BytesToCopy);
        Assert.Equal(0.0, plan.LocalReuseRatio);
        Assert.All(plan.Actions, a => Assert.IsType<DownloadRemote>(a));
    }

    [Fact]
    public void Calculate_WhenPartialMatch_ReturnsMixedActions()
    {
        // Arrange: Create two files that share some chunks
        var data1 = CreateDeterministicData(4096, seed: 42);
        var data2 = CreateDeterministicData(4096, seed: 99);

        // Combine: first half from data1, second half from data2
        var combined = new byte[4096];
        Array.Copy(data1, 0, combined, 0, 2048);
        Array.Copy(data2, 2048, combined, 2048, 2048);

        var tempFile = Path.GetTempFileName();
        try
        {
            // Local file has only data1
            File.WriteAllBytes(tempFile, data1);

            // Remote signature is for combined file
            using var stream = new MemoryStream(combined);
            var chunks = _chunker.Chunk(stream, _options).ToList();
            var signature = new SignatureFile
            {
                AlgorithmId = "fastcdc-v1",
                MinSize = _options.MinSize,
                AverageSize = _options.AverageSize,
                MaxSize = _options.MaxSize,
                Chunks = chunks.Select(c => new SignatureChunk(c.Offset, c.Length, c.Hash)).ToList()
            };

            var localSource = LocalFileSource.Build(tempFile, _chunker, _options);
            var calculator = new DeltaCalculator();

            // Act
            var plan = calculator.Calculate(signature, localSource);

            // Assert: Should have a mix of copy and download
            Assert.True(plan.BytesToCopy > 0, "Should have some local chunks");
            Assert.True(plan.BytesToDownload > 0, "Should need to download some chunks");
            Assert.True(plan.LocalReuseRatio > 0 && plan.LocalReuseRatio < 1,
                "Should have partial reuse");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void GetDownloadRanges_CoalescesAdjacentRanges()
    {
        // Arrange: Create signature with specific chunks
        var chunks = new List<SignatureChunk>
        {
            new(0, 100, new byte[32]),
            new(100, 100, new byte[32]),  // Adjacent to first
            new(200, 100, new byte[32]),  // Adjacent to second
            new(1000, 100, new byte[32]), // Gap > maxGap
            new(1100, 100, new byte[32])  // Adjacent to previous
        };

        var signature = new SignatureFile
        {
            AlgorithmId = "fastcdc-v1",
            MinSize = 64,
            AverageSize = 128,
            MaxSize = 256,
            Chunks = chunks
        };

        // All chunks need downloading (empty local source)
        var calculator = new DeltaCalculator();
        var plan = calculator.Calculate(signature, new CompositeChunkSource());

        // Act
        var ranges = plan.GetDownloadRanges(maxGap: 100);

        // Assert: Should coalesce into 2 ranges
        Assert.Equal(2, ranges.Count);
        Assert.Equal(0, ranges[0].Offset);
        Assert.Equal(300, ranges[0].Length);
        Assert.Equal(1000, ranges[1].Offset);
        Assert.Equal(200, ranges[1].Length);
    }

    private static byte[] CreateDeterministicData(int size, int seed)
    {
        var rng = new Random(seed);
        var data = new byte[size];
        rng.NextBytes(data);
        return data;
    }
}
