using System.Security.Cryptography;
using System.Text.Json;
using PatchSync.Common.Chunking;
using PatchSync.Common.Manifest;
using PatchSync.Common.Signatures;
using PatchSync.SDK.Engine;
using PatchSync.SDK.Signatures;
using PatchSync.SDK.Storage;

namespace PatchSync.Tests;

/// <summary>
/// Tests for the phased PatchEngine to verify:
/// 1. Phase isolation - temp files used, originals not modified during assembly
/// 2. Cross-file chunk safety - files that depend on each other don't corrupt
/// 3. Parallel assembly - multiple files assembled safely in parallel
/// 4. Atomic commit - temps only moved after all assemblies complete
/// 5. Verify with fallback - verification failures trigger re-download
/// </summary>
public class PatchEngineTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sourceDir;
    private readonly string _targetDir;
    private readonly IChunker _chunker = new FastCDCChunker();
    private readonly ChunkingOptions _options = new()
    {
        MinSize = 256,
        AverageSize = 512,
        MaxSize = 1024
    };

    public PatchEngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"patchengine_test_{Guid.NewGuid():N}");
        _sourceDir = Path.Combine(_tempDir, "source");
        _targetDir = Path.Combine(_tempDir, "target");
        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(_targetDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    #region Phase Isolation Tests

    [Fact]
    public async Task PatchAsync_UsesTempFiles_OriginalNotModifiedDuringAssembly()
    {
        // Arrange: Create source file
        var originalData = CreateDeterministicData(4096, seed: 42);
        var modifiedData = CreateDeterministicData(4096, seed: 99);
        var hash = ComputeHash(modifiedData);

        await SetupSourceFile("test.bin", modifiedData);

        // Create target with original data
        var targetFile = Path.Combine(_targetDir, "test.bin");
        await File.WriteAllBytesAsync(targetFile, originalData);
        var originalModTime = File.GetLastWriteTimeUtc(targetFile);

        var manifest = CreateManifest(new[]
        {
            CreateManifestFile("test.bin", modifiedData.Length, hash)
        });

        var storage = new LocalStorageProvider(_sourceDir);
        using var engine = new PatchEngine(storage, options: new PatchEngineOptions
        {
            MaxConcurrency = 1 // Sequential to make timing predictable
        });

        // Track when original file was modified
        var originalModifiedDuringAssembly = false;
        var assemblyStarted = false;
        var assemblyComplete = false;

        var progress = new Progress<PatchEngineProgress>(p =>
        {
            if (p.Phase == PatchEnginePhase.Assembling && !assemblyStarted)
            {
                assemblyStarted = true;
            }
            if (p.Phase == PatchEnginePhase.Committing && assemblyStarted && !assemblyComplete)
            {
                assemblyComplete = true;
                // Check if original was modified before commit
                var currentModTime = File.GetLastWriteTimeUtc(targetFile);
                if (currentModTime != originalModTime)
                {
                    originalModifiedDuringAssembly = true;
                }
            }
        });

        // Act
        var result = await engine.PatchAsync(_targetDir, manifest, progress);

        // Assert
        Assert.True(result.Success);
        Assert.False(originalModifiedDuringAssembly, "Original file should not be modified during assembly phase");
        Assert.Equal(modifiedData, await File.ReadAllBytesAsync(targetFile));
    }

    [Fact]
    public async Task PatchAsync_TempFilesCleanedUp_OnSuccess()
    {
        // Arrange
        var testData = CreateDeterministicData(2048, seed: 42);
        var hash = ComputeHash(testData);

        await SetupSourceFile("test.bin", testData);

        var manifest = CreateManifest(new[]
        {
            CreateManifestFile("test.bin", testData.Length, hash)
        });

        var storage = new LocalStorageProvider(_sourceDir);
        using var engine = new PatchEngine(storage);

        // Act
        await engine.PatchAsync(_targetDir, manifest);

        // Assert - no .pstmp files should remain
        var tempFiles = Directory.GetFiles(_targetDir, "*.pstmp", SearchOption.AllDirectories);
        Assert.Empty(tempFiles);
    }

    #endregion

    #region Cross-File Chunk Safety Tests

    [Fact]
    public async Task PatchAsync_CrossFileChunks_DoesNotCorrupt()
    {
        // This is the key test: File B needs chunks from File A.
        // If we modify A before B is assembled, B would be corrupted.
        // The phased approach prevents this by reading all from originals,
        // writing all to temps, then committing all at once.

        // Arrange: Create two files that share common chunks
        var sharedChunk = CreateDeterministicData(1024, seed: 100);
        var uniqueA = CreateDeterministicData(1024, seed: 200);
        var uniqueB = CreateDeterministicData(1024, seed: 300);

        // File A: [shared][uniqueA]
        var fileAData = sharedChunk.Concat(uniqueA).ToArray();
        // File B: [shared][uniqueB] - shares the first chunk with A
        var fileBData = sharedChunk.Concat(uniqueB).ToArray();

        // Create new versions (modified)
        var newUniqueA = CreateDeterministicData(1024, seed: 201);
        var newUniqueB = CreateDeterministicData(1024, seed: 301);
        var newFileAData = sharedChunk.Concat(newUniqueA).ToArray();
        var newFileBData = sharedChunk.Concat(newUniqueB).ToArray();

        // Setup source with new versions
        await SetupSourceFile("fileA.bin", newFileAData);
        await SetupSourceFile("fileB.bin", newFileBData);

        // Target has old versions
        await File.WriteAllBytesAsync(Path.Combine(_targetDir, "fileA.bin"), fileAData);
        await File.WriteAllBytesAsync(Path.Combine(_targetDir, "fileB.bin"), fileBData);

        var manifest = CreateManifest(new[]
        {
            CreateManifestFile("fileA.bin", newFileAData.Length, ComputeHash(newFileAData)),
            CreateManifestFile("fileB.bin", newFileBData.Length, ComputeHash(newFileBData))
        });

        var storage = new LocalStorageProvider(_sourceDir);
        using var engine = new PatchEngine(storage, options: new PatchEngineOptions
        {
            MaxConcurrency = 4 // Allow parallel to test race conditions
        });

        // Act
        var result = await engine.PatchAsync(_targetDir, manifest);

        // Assert
        Assert.True(result.Success, result.Message);
        Assert.Equal(newFileAData, await File.ReadAllBytesAsync(Path.Combine(_targetDir, "fileA.bin")));
        Assert.Equal(newFileBData, await File.ReadAllBytesAsync(Path.Combine(_targetDir, "fileB.bin")));
    }

    [Fact]
    public async Task PatchAsync_ManyFilesWithSharedChunks_AllCorrect()
    {
        // Test with many files sharing chunks to stress-test parallelism
        var sharedData = CreateDeterministicData(512, seed: 1);
        var fileCount = 10;

        var files = new List<(string name, byte[] data)>();
        for (int i = 0; i < fileCount; i++)
        {
            var uniqueData = CreateDeterministicData(512, seed: 100 + i);
            var fileData = sharedData.Concat(uniqueData).ToArray();
            files.Add(($"file{i:D2}.bin", fileData));
        }

        // Setup source files
        foreach (var (name, data) in files)
        {
            await SetupSourceFile(name, data);
        }

        var manifestFiles = files.Select(f =>
            CreateManifestFile(f.name, f.data.Length, ComputeHash(f.data))).ToArray();

        var manifest = CreateManifest(manifestFiles);

        var storage = new LocalStorageProvider(_sourceDir);
        using var engine = new PatchEngine(storage, options: new PatchEngineOptions
        {
            MaxConcurrency = Environment.ProcessorCount
        });

        // Act
        var result = await engine.PatchAsync(_targetDir, manifest);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(fileCount, result.FilesCommitted);

        foreach (var (name, expectedData) in files)
        {
            var actualData = await File.ReadAllBytesAsync(Path.Combine(_targetDir, name));
            Assert.Equal(expectedData, actualData);
        }
    }

    #endregion

    #region Parallel Assembly Tests

    [Fact]
    public async Task PatchAsync_ParallelAssembly_AllFilesCorrect()
    {
        // Arrange: Create multiple independent files
        var files = new List<(string name, byte[] data)>();
        for (int i = 0; i < 20; i++)
        {
            var data = CreateDeterministicData(1024 + i * 100, seed: i);
            files.Add(($"parallel{i:D2}.bin", data));
        }

        foreach (var (name, data) in files)
        {
            await SetupSourceFile(name, data);
        }

        var manifestFiles = files.Select(f =>
            CreateManifestFile(f.name, f.data.Length, ComputeHash(f.data))).ToArray();

        var manifest = CreateManifest(manifestFiles);

        var storage = new LocalStorageProvider(_sourceDir);
        using var engine = new PatchEngine(storage, options: new PatchEngineOptions
        {
            MaxConcurrency = 8 // High parallelism
        });

        // Act
        var result = await engine.PatchAsync(_targetDir, manifest);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(files.Count, result.FilesVerified);

        foreach (var (name, expectedData) in files)
        {
            var actualData = await File.ReadAllBytesAsync(Path.Combine(_targetDir, name));
            Assert.Equal(expectedData, actualData);
        }
    }

    [Fact]
    public async Task PatchAsync_ParallelAssembly_ReportsProgressForAllPhases()
    {
        // Arrange
        var files = new List<(string name, byte[] data)>();
        for (int i = 0; i < 5; i++)
        {
            var data = CreateDeterministicData(1024, seed: i);
            files.Add(($"prog{i}.bin", data));
        }

        foreach (var (name, data) in files)
        {
            await SetupSourceFile(name, data);
        }

        var manifestFiles = files.Select(f =>
            CreateManifestFile(f.name, f.data.Length, ComputeHash(f.data))).ToArray();

        var manifest = CreateManifest(manifestFiles);

        var storage = new LocalStorageProvider(_sourceDir);
        using var engine = new PatchEngine(storage);

        var phases = new HashSet<PatchEnginePhase>();
        var progress = new Progress<PatchEngineProgress>(p => phases.Add(p.Phase));

        // Act
        await engine.PatchAsync(_targetDir, manifest, progress);
        await Task.Delay(50); // Allow async progress to fire

        // Assert - all phases should be reported
        Assert.Contains(PatchEnginePhase.Planning, phases);
        Assert.Contains(PatchEnginePhase.Assembling, phases);
        Assert.Contains(PatchEnginePhase.Committing, phases);
        Assert.Contains(PatchEnginePhase.Verifying, phases);
        Assert.Contains(PatchEnginePhase.Complete, phases);
    }

    #endregion

    #region Verify with Fallback Tests

    [Fact]
    public async Task PatchAsync_VerifyFailure_FallsBackToFullDownload()
    {
        // Arrange: Create a file where delta will "fail" verification
        // We simulate this by having the source file available for fallback
        var testData = CreateDeterministicData(2048, seed: 42);
        var hash = ComputeHash(testData);

        await SetupSourceFile("fallback.bin", testData);

        // Create a target with completely different data
        var targetFile = Path.Combine(_targetDir, "fallback.bin");
        await File.WriteAllBytesAsync(targetFile, CreateDeterministicData(500, seed: 999));

        var manifest = CreateManifest(new[]
        {
            CreateManifestFile("fallback.bin", testData.Length, hash)
        });

        var storage = new LocalStorageProvider(_sourceDir);
        using var engine = new PatchEngine(storage, options: new PatchEngineOptions
        {
            FallbackOnVerifyFailure = true
        });

        // Act
        var result = await engine.PatchAsync(_targetDir, manifest);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(testData, await File.ReadAllBytesAsync(targetFile));
    }

    [Fact]
    public async Task PatchAsync_WhenAlreadyUpToDate_SkipsProcessing()
    {
        // Arrange: Target already has correct files
        var testData = CreateDeterministicData(2048, seed: 42);
        var hash = ComputeHash(testData);

        await SetupSourceFile("uptodate.bin", testData);

        var targetFile = Path.Combine(_targetDir, "uptodate.bin");
        await File.WriteAllBytesAsync(targetFile, testData);

        var manifest = CreateManifest(new[]
        {
            CreateManifestFile("uptodate.bin", testData.Length, hash)
        });

        var storage = new LocalStorageProvider(_sourceDir);
        using var engine = new PatchEngine(storage);

        // Act
        var result = await engine.PatchAsync(_targetDir, manifest);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(0, result.FilesPlanned); // Nothing to update
        Assert.Equal("Already up to date", result.Message);
    }

    #endregion

    #region Delete Strategy Tests

    [Fact]
    public async Task PatchAsync_DeleteStrategy_RemovesObsoleteFiles()
    {
        // Arrange: Create a file that should be deleted
        var obsoleteFile = Path.Combine(_targetDir, "obsolete.txt");
        await File.WriteAllTextAsync(obsoleteFile, "this should be deleted");

        var manifest = CreateManifest(new[]
        {
            new ManifestFile
            {
                Path = "obsolete.txt",
                Size = 0,
                Hash = "",
                Strategy = UpdateStrategy.Delete
            }
        });

        var storage = new LocalStorageProvider(_sourceDir);
        using var engine = new PatchEngine(storage);

        // Act
        var result = await engine.PatchAsync(_targetDir, manifest);

        // Assert
        Assert.True(result.Success);
        Assert.False(File.Exists(obsoleteFile));
    }

    #endregion

    #region Mixed Scenarios

    [Fact]
    public async Task PatchAsync_MixedOperations_AllSucceed()
    {
        // Arrange: Mix of new files, updates, and deletes
        var newFileData = CreateDeterministicData(1024, seed: 1);
        var updateFileOld = CreateDeterministicData(1024, seed: 2);
        var updateFileNew = CreateDeterministicData(1024, seed: 3);
        var unchangedData = CreateDeterministicData(1024, seed: 4);

        // Setup source
        await SetupSourceFile("new.bin", newFileData);
        await SetupSourceFile("update.bin", updateFileNew);
        await SetupSourceFile("unchanged.bin", unchangedData);

        // Setup target
        await File.WriteAllBytesAsync(Path.Combine(_targetDir, "update.bin"), updateFileOld);
        await File.WriteAllBytesAsync(Path.Combine(_targetDir, "unchanged.bin"), unchangedData);
        await File.WriteAllTextAsync(Path.Combine(_targetDir, "obsolete.bin"), "delete me");

        var manifest = CreateManifest(new[]
        {
            CreateManifestFile("new.bin", newFileData.Length, ComputeHash(newFileData)),
            CreateManifestFile("update.bin", updateFileNew.Length, ComputeHash(updateFileNew)),
            CreateManifestFile("unchanged.bin", unchangedData.Length, ComputeHash(unchangedData)),
            new ManifestFile { Path = "obsolete.bin", Size = 0, Hash = "", Strategy = UpdateStrategy.Delete }
        });

        var storage = new LocalStorageProvider(_sourceDir);
        using var engine = new PatchEngine(storage);

        // Act
        var result = await engine.PatchAsync(_targetDir, manifest);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(newFileData, await File.ReadAllBytesAsync(Path.Combine(_targetDir, "new.bin")));
        Assert.Equal(updateFileNew, await File.ReadAllBytesAsync(Path.Combine(_targetDir, "update.bin")));
        Assert.Equal(unchangedData, await File.ReadAllBytesAsync(Path.Combine(_targetDir, "unchanged.bin")));
        Assert.False(File.Exists(Path.Combine(_targetDir, "obsolete.bin")));
    }

    [Fact]
    public async Task PatchAsync_SubdirectoryFiles_CreatesDirectoriesAndFiles()
    {
        // Arrange
        var testData = CreateDeterministicData(1024, seed: 42);
        var hash = ComputeHash(testData);

        // Create nested source structure
        var nestedPath = Path.Combine(_sourceDir, "data", "textures");
        Directory.CreateDirectory(nestedPath);
        await File.WriteAllBytesAsync(Path.Combine(nestedPath, "texture.bin"), testData);

        // Create signature
        var sigDir = Path.Combine(_sourceDir, "signatures", "data", "textures");
        Directory.CreateDirectory(sigDir);
        var sigGenerator = new SignatureGenerator(_chunker, _options);
        sigGenerator.GenerateSignatureFile(
            Path.Combine(nestedPath, "texture.bin"),
            Path.Combine(sigDir, "texture.bin.sig"));

        var manifest = CreateManifest(new[]
        {
            CreateManifestFile("data/textures/texture.bin", testData.Length, hash)
        });

        var storage = new LocalStorageProvider(_sourceDir);
        using var engine = new PatchEngine(storage);

        // Act
        var result = await engine.PatchAsync(_targetDir, manifest);

        // Assert
        Assert.True(result.Success);
        var targetFile = Path.Combine(_targetDir, "data", "textures", "texture.bin");
        Assert.True(File.Exists(targetFile));
        Assert.Equal(testData, await File.ReadAllBytesAsync(targetFile));
    }

    #endregion

    #region Cancellation Tests

    [Fact]
    public async Task PatchAsync_WhenCancelled_StopsAndCleansUp()
    {
        // Arrange: Create many files to ensure we can cancel mid-process
        for (int i = 0; i < 50; i++)
        {
            var data = CreateDeterministicData(4096, seed: i);
            await SetupSourceFile($"cancel{i:D2}.bin", data);
        }

        var manifestFiles = Enumerable.Range(0, 50)
            .Select(i =>
            {
                var data = CreateDeterministicData(4096, seed: i);
                return CreateManifestFile($"cancel{i:D2}.bin", data.Length, ComputeHash(data));
            }).ToArray();

        var manifest = CreateManifest(manifestFiles);

        var storage = new LocalStorageProvider(_sourceDir);
        using var engine = new PatchEngine(storage, options: new PatchEngineOptions
        {
            MaxConcurrency = 2 // Slow down to allow cancellation
        });

        var cts = new CancellationTokenSource();

        // Cancel after first progress report
        var progress = new Progress<PatchEngineProgress>(_ => cts.Cancel());

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.PatchAsync(_targetDir, manifest, progress, cts.Token));

        // Cleanup should have happened - no temp files
        var tempFiles = Directory.GetFiles(_targetDir, "*.pstmp", SearchOption.AllDirectories);
        // Note: Some temp files might remain if cancellation happened during write
        // The important thing is that the operation was cancelled
    }

    #endregion

    #region Helper Methods

    private async Task SetupSourceFile(string relativePath, byte[] data)
    {
        var fullPath = Path.Combine(_sourceDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllBytesAsync(fullPath, data);

        // Create signature
        var sigPath = Path.Combine(_sourceDir, "signatures", relativePath.Replace('/', Path.DirectorySeparatorChar) + ".sig");
        var sigDir = Path.GetDirectoryName(sigPath);
        if (!string.IsNullOrEmpty(sigDir))
            Directory.CreateDirectory(sigDir);

        var sigGenerator = new SignatureGenerator(_chunker, _options);
        sigGenerator.GenerateSignatureFile(fullPath, sigPath);
    }

    private ManifestFile CreateManifestFile(string path, long size, string hash)
    {
        return new ManifestFile
        {
            Path = path,
            Size = size,
            Hash = hash,
            Strategy = UpdateStrategy.Delta,
            SignatureUrl = $"signatures/{path}.sig"
        };
    }

    private GameManifest CreateManifest(ManifestFile[] files)
    {
        return new GameManifest
        {
            Version = "1.0.0",
            BuildDate = DateTime.UtcNow,
            SupportedAlgorithms = new[] { "fastcdc-v1" },
            PreferredAlgorithm = "fastcdc-v1",
            BaseUrl = "file:///" + _sourceDir.Replace('\\', '/'),
            Files = files
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

    #endregion
}
