using System.Security.Cryptography;
using PatchSync.Common.Chunking;
using PatchSync.Common.Hashing;
using PatchSync.Common.Signatures;
using PatchSync.SDK.Assembly;
using PatchSync.SDK.Delta;
using PatchSync.SDK.Sources;
using PatchSync.SDK.Storage;

namespace PatchSync.Tests;

public class FileAssemblerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IChunker _chunker = new FastCDCChunker();
    private readonly ChunkingOptions _options = new()
    {
        MinSize = 256,
        AverageSize = 512,
        MaxSize = 1024
    };

    public FileAssemblerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"patchsync_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task AssembleAsync_WhenAllChunksLocal_ReconstructsFile()
    {
        // Arrange: Create source file
        var originalData = CreateDeterministicData(4096, seed: 42);
        var sourcePath = Path.Combine(_tempDir, "source.bin");
        var targetPath = Path.Combine(_tempDir, "target.bin");
        var remotePath = Path.Combine(_tempDir, "remote.bin");

        await File.WriteAllBytesAsync(sourcePath, originalData);
        await File.WriteAllBytesAsync(remotePath, originalData); // Remote is identical

        // Create signature and delta plan
        var signature = CreateSignature(originalData);
        var localSource = await LocalFileSource.BuildAsync(sourcePath, _chunker, _options);
        var calculator = new DeltaCalculator();
        var plan = calculator.Calculate(signature, localSource);

        // All chunks should be local
        Assert.Equal(0, plan.BytesToDownload);
        Assert.Equal(originalData.Length, plan.BytesToCopy);

        // Create assembler with local storage
        var storage = new LocalStorageProvider(_tempDir);
        var assembler = new FileAssembler(storage);
        var expectedHash = ComputeHash(originalData);

        // Act
        await assembler.AssembleAsync(targetPath, "remote.bin", plan, expectedHash);

        // Assert
        Assert.True(File.Exists(targetPath));
        var resultData = await File.ReadAllBytesAsync(targetPath);
        Assert.Equal(originalData, resultData);
    }

    [Fact]
    public async Task AssembleAsync_WhenAllChunksRemote_DownloadsFile()
    {
        // Arrange: Remote file exists, no local file
        var originalData = CreateDeterministicData(4096, seed: 99);
        var targetPath = Path.Combine(_tempDir, "target.bin");
        var remotePath = Path.Combine(_tempDir, "remote.bin");

        await File.WriteAllBytesAsync(remotePath, originalData);

        // Create signature for remote file
        var signature = CreateSignature(originalData);

        // No local sources - everything must be downloaded
        var calculator = new DeltaCalculator();
        var plan = calculator.Calculate(signature, new CompositeChunkSource());

        Assert.Equal(originalData.Length, plan.BytesToDownload);
        Assert.Equal(0, plan.BytesToCopy);

        var storage = new LocalStorageProvider(_tempDir);
        var assembler = new FileAssembler(storage);
        var expectedHash = ComputeHash(originalData);

        // Act
        await assembler.AssembleAsync(targetPath, "remote.bin", plan, expectedHash);

        // Assert
        Assert.True(File.Exists(targetPath));
        var resultData = await File.ReadAllBytesAsync(targetPath);
        Assert.Equal(originalData, resultData);
    }

    [Fact]
    public async Task AssembleAsync_WhenMixedChunks_CombinesLocalAndRemote()
    {
        // Arrange: Local file has first half, remote has full file
        var data1 = CreateDeterministicData(2048, seed: 42);
        var data2 = CreateDeterministicData(2048, seed: 99);
        var fullData = data1.Concat(data2).ToArray();

        var sourcePath = Path.Combine(_tempDir, "source.bin");
        var targetPath = Path.Combine(_tempDir, "target.bin");
        var remotePath = Path.Combine(_tempDir, "remote.bin");

        await File.WriteAllBytesAsync(sourcePath, data1);    // Only first half
        await File.WriteAllBytesAsync(remotePath, fullData); // Full file

        var signature = CreateSignature(fullData);
        var localSource = await LocalFileSource.BuildAsync(sourcePath, _chunker, _options);
        var calculator = new DeltaCalculator();
        var plan = calculator.Calculate(signature, localSource);

        // Should have some local and some remote
        Assert.True(plan.BytesToCopy > 0);
        Assert.True(plan.BytesToDownload > 0);

        var storage = new LocalStorageProvider(_tempDir);
        var assembler = new FileAssembler(storage);
        var expectedHash = ComputeHash(fullData);

        // Act
        await assembler.AssembleAsync(targetPath, "remote.bin", plan, expectedHash);

        // Assert
        Assert.True(File.Exists(targetPath));
        var resultData = await File.ReadAllBytesAsync(targetPath);
        Assert.Equal(fullData, resultData);
    }

    [Fact]
    public async Task AssembleAsync_WhenHashMismatch_ThrowsException()
    {
        // Arrange
        var originalData = CreateDeterministicData(1024, seed: 42);
        var targetPath = Path.Combine(_tempDir, "target.bin");
        var remotePath = Path.Combine(_tempDir, "remote.bin");

        await File.WriteAllBytesAsync(remotePath, originalData);

        var signature = CreateSignature(originalData);
        var calculator = new DeltaCalculator();
        var plan = calculator.Calculate(signature, new CompositeChunkSource());

        var storage = new LocalStorageProvider(_tempDir);
        var assembler = new FileAssembler(storage);

        // Wrong hash
        var wrongHash = "0000000000000000000000000000000000000000000000000000000000000000";

        // Act & Assert
        await Assert.ThrowsAsync<InvalidDataException>(
            () => assembler.AssembleAsync(targetPath, "remote.bin", plan, wrongHash));
    }

    [Fact]
    public async Task AssembleAsync_ReportsProgress()
    {
        // Arrange
        var originalData = CreateDeterministicData(4096, seed: 42);
        var targetPath = Path.Combine(_tempDir, "target.bin");
        var remotePath = Path.Combine(_tempDir, "remote.bin");

        await File.WriteAllBytesAsync(remotePath, originalData);

        var signature = CreateSignature(originalData);
        var calculator = new DeltaCalculator();
        var plan = calculator.Calculate(signature, new CompositeChunkSource());

        var storage = new LocalStorageProvider(_tempDir);
        var assembler = new FileAssembler(storage);
        var expectedHash = ComputeHash(originalData);

        var progressReports = new List<AssemblyProgress>();
        var progress = new Progress<AssemblyProgress>(p => progressReports.Add(p));

        // Act
        await assembler.AssembleAsync(targetPath, "remote.bin", plan, expectedHash, progress);

        // Give progress reporter time to fire
        await Task.Delay(50);

        // Assert
        Assert.NotEmpty(progressReports);
        Assert.Contains(progressReports, p => p.Phase == AssemblyPhase.Complete);
        Assert.True(progressReports.Last().BytesComplete == originalData.Length);
    }

    private SignatureFile CreateSignature(byte[] data)
    {
        using var stream = new MemoryStream(data);
        var chunks = _chunker.Chunk(stream, _options).ToList();

        return new SignatureFile
        {
            AlgorithmId = "fastcdc-v1",
            MinSize = _options.MinSize,
            AverageSize = _options.AverageSize,
            MaxSize = _options.MaxSize,
            Chunks = chunks.Select(c => new SignatureChunk(c.Offset, c.Length, c.Hash)).ToList()
        };
    }

    private static string ComputeHash(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static byte[] CreateDeterministicData(int size, int seed)
    {
        var rng = new Random(seed);
        var data = new byte[size];
        rng.NextBytes(data);
        return data;
    }
}
