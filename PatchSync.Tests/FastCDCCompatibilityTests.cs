using System.Text.Json;
using PatchSync.Common.Chunking;

namespace PatchSync.Tests;

/// <summary>
/// Tests that verify the C# FastCDC implementation produces identical
/// chunk boundaries to the reference fastcdc-go implementation.
/// </summary>
public class FastCDCCompatibilityTests
{
    private static readonly string TestVectorsPath =
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tools", "testvectors", "testvectors.json");

    private readonly TestVectors _vectors;

    public FastCDCCompatibilityTests()
    {
        var json = File.ReadAllText(TestVectorsPath);
        _vectors = JsonSerializer.Deserialize<TestVectors>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        })!;
    }

    [Theory]
    [InlineData("small_default")]
    [InlineData("medium_default")]
    [InlineData("tiny")]
    [InlineData("large_chunks")]
    [InlineData("boundary_aligned")]
    [InlineData("zeros")]
    public void Chunker_ProducesIdenticalBoundaries_ToFastCDCGo(string testCaseName)
    {
        var testCase = _vectors.TestCases.First(tc => tc.Name == testCaseName);

        // Generate the same deterministic data
        byte[] data = GenerateData(testCase);

        // Chunk with our implementation
        var chunker = new FastCDCChunker();
        var options = new ChunkingOptions
        {
            MinSize = testCase.MinSize,
            AverageSize = testCase.AverageSize,
            MaxSize = testCase.MaxSize
        };

        using var stream = new MemoryStream(data);
        var chunks = chunker.Chunk(stream, options).ToList();

        // Verify chunk count matches
        Assert.Equal(testCase.Chunks.Count, chunks.Count);

        // Verify each chunk matches
        for (int i = 0; i < chunks.Count; i++)
        {
            var expected = testCase.Chunks[i];
            var actual = chunks[i];

            Assert.Equal(expected.Offset, actual.Offset);
            Assert.Equal(expected.Length, actual.Length);
            Assert.Equal(expected.Hash.ToLowerInvariant(), Convert.ToHexString(actual.Hash).ToLowerInvariant());
        }
    }

    [Fact]
    public void Chunker_AlgorithmId_IsCorrect()
    {
        var chunker = new FastCDCChunker();
        Assert.Equal("fastcdc-v1", chunker.AlgorithmId);
    }

    [Fact]
    public void ChunkingOptions_Validate_RejectsInvalidOptions()
    {
        // MinSize too small
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkingOptions { MinSize = 32 }.Validate());

        // MaxSize too large
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkingOptions { MaxSize = int.MaxValue }.Validate());

        // MinSize >= MaxSize
        Assert.Throws<ArgumentException>(() => new ChunkingOptions { MinSize = 1024, MaxSize = 512 }.Validate());

        // AverageSize outside range
        Assert.Throws<ArgumentException>(() =>
            new ChunkingOptions { MinSize = 1024, AverageSize = 512, MaxSize = 4096 }.Validate());
    }

    [Fact]
    public void ChunkerRegistry_GetPreferred_ReturnsFirstSupportedAlgorithm()
    {
        var registry = new ChunkerRegistry();

        // Should return fastcdc-v1 when available
        var chunker = registry.GetPreferred(["unknown-v1", "fastcdc-v1", "another-v1"]);
        Assert.Equal("fastcdc-v1", chunker.AlgorithmId);
    }

    [Fact]
    public void ChunkerRegistry_GetPreferred_ThrowsWhenNoneSupported()
    {
        var registry = new ChunkerRegistry();

        Assert.Throws<NotSupportedException>(() =>
            registry.GetPreferred(["unknown-v1", "another-v1"]));
    }

    private static byte[] GenerateData(TestCase testCase)
    {
        // Use the actual test data from the Go test vectors (avoids PRNG differences)
        return Convert.FromBase64String(testCase.DataBase64);
    }
}

// DTOs for test vector JSON
public class TestVectors
{
    public string Version { get; set; } = "";
    public string Generator { get; set; } = "";
    public List<TestCase> TestCases { get; set; } = new();
}

public class TestCase
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public long Seed { get; set; }
    public int DataSize { get; set; }
    public string DataBase64 { get; set; } = ""; // Actual test data to avoid PRNG differences
    public int MinSize { get; set; }
    public int AverageSize { get; set; }
    public int MaxSize { get; set; }
    public List<ChunkVector> Chunks { get; set; } = new();
}

public class ChunkVector
{
    public long Offset { get; set; }
    public int Length { get; set; }
    public string Hash { get; set; } = "";
}
