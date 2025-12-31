using PatchSync.Common.Chunking;
using PatchSync.Common.Signatures;
using PatchSync.SDK.Signatures;

namespace PatchSync.Tests;

public class SignatureGeneratorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IChunker _chunker = new FastCDCChunker();
    private readonly ChunkingOptions _options = new()
    {
        MinSize = 256,
        AverageSize = 512,
        MaxSize = 1024
    };

    public SignatureGeneratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"patchsync_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void GenerateSignature_CreatesValidSignature()
    {
        // Arrange
        var testData = CreateDeterministicData(4096, seed: 42);
        var filePath = Path.Combine(_tempDir, "test.bin");
        File.WriteAllBytes(filePath, testData);

        var generator = new SignatureGenerator(_chunker, _options);

        // Act
        var signature = generator.GenerateSignature(filePath);

        // Assert
        Assert.Equal("fastcdc-v1", signature.AlgorithmId);
        Assert.Equal(_options.MinSize, signature.MinSize);
        Assert.Equal(_options.AverageSize, signature.AverageSize);
        Assert.Equal(_options.MaxSize, signature.MaxSize);
        Assert.NotEmpty(signature.Chunks);
        Assert.Equal(testData.Length, signature.TotalSize);
    }

    [Fact]
    public void GenerateSignatureFile_CreatesFileOnDisk()
    {
        // Arrange
        var testData = CreateDeterministicData(4096, seed: 42);
        var filePath = Path.Combine(_tempDir, "test.bin");
        var sigPath = Path.Combine(_tempDir, "test.bin.sig");
        File.WriteAllBytes(filePath, testData);

        var generator = new SignatureGenerator(_chunker, _options);

        // Act
        generator.GenerateSignatureFile(filePath, sigPath);

        // Assert
        Assert.True(File.Exists(sigPath));

        // Verify it can be read back
        using var sigStream = File.OpenRead(sigPath);
        var signature = SignatureFile.Read(sigStream);
        Assert.Equal("fastcdc-v1", signature.AlgorithmId);
        Assert.NotEmpty(signature.Chunks);
    }

    [Fact]
    public void SignatureFile_RoundTrip_PreservesData()
    {
        // Arrange
        var testData = CreateDeterministicData(8192, seed: 99);
        var filePath = Path.Combine(_tempDir, "test.bin");
        File.WriteAllBytes(filePath, testData);

        var generator = new SignatureGenerator(_chunker, _options);
        var original = generator.GenerateSignature(filePath);

        // Act - Write and read back
        var sigPath = Path.Combine(_tempDir, "test.sig");
        using (var output = File.Create(sigPath))
        {
            original.Write(output);
        }

        SignatureFile loaded;
        using (var input = File.OpenRead(sigPath))
        {
            loaded = SignatureFile.Read(input);
        }

        // Assert
        Assert.Equal(original.AlgorithmId, loaded.AlgorithmId);
        Assert.Equal(original.MinSize, loaded.MinSize);
        Assert.Equal(original.AverageSize, loaded.AverageSize);
        Assert.Equal(original.MaxSize, loaded.MaxSize);
        Assert.Equal(original.Chunks.Count, loaded.Chunks.Count);

        for (int i = 0; i < original.Chunks.Count; i++)
        {
            Assert.Equal(original.Chunks[i].Offset, loaded.Chunks[i].Offset);
            Assert.Equal(original.Chunks[i].Length, loaded.Chunks[i].Length);
            Assert.Equal(original.Chunks[i].Hash, loaded.Chunks[i].Hash);
        }
    }

    [Fact]
    public void SignatureFile_Read_ThrowsOnInvalidMagic()
    {
        // Arrange - need at least 22 bytes (header size) with invalid magic
        var invalidData = new byte[22];
        invalidData[0] = 0x00; // Invalid magic (not PSI1)
        invalidData[1] = 0x01;
        invalidData[2] = 0x02;
        invalidData[3] = 0x03;
        var sigPath = Path.Combine(_tempDir, "invalid.sig");
        File.WriteAllBytes(sigPath, invalidData);

        // Act & Assert
        using var input = File.OpenRead(sigPath);
        var ex = Assert.Throws<InvalidDataException>(() => SignatureFile.Read(input));
        Assert.Contains("Invalid signature file magic", ex.Message);
    }

    [Fact]
    public void SignatureFile_Read_ThrowsOnTruncatedFile()
    {
        // Arrange
        var truncatedData = new byte[10]; // Too short for header
        var sigPath = Path.Combine(_tempDir, "truncated.sig");
        File.WriteAllBytes(sigPath, truncatedData);

        // Act & Assert
        using var input = File.OpenRead(sigPath);
        var ex = Assert.Throws<InvalidDataException>(() => SignatureFile.Read(input));
        Assert.Contains("too short", ex.Message);
    }

    [Fact]
    public void GenerateSignature_FromStream_WorksCorrectly()
    {
        // Arrange
        var testData = CreateDeterministicData(4096, seed: 42);
        var generator = new SignatureGenerator(_chunker, _options);

        // Act
        using var stream = new MemoryStream(testData);
        var signature = generator.GenerateSignature(stream);

        // Assert
        Assert.Equal("fastcdc-v1", signature.AlgorithmId);
        Assert.NotEmpty(signature.Chunks);
        Assert.Equal(testData.Length, signature.TotalSize);
    }

    private static byte[] CreateDeterministicData(int size, int seed)
    {
        var rng = new Random(seed);
        var data = new byte[size];
        rng.NextBytes(data);
        return data;
    }
}
