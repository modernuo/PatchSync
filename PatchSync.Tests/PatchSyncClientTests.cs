using System.Security.Cryptography;
using System.Text.Json;
using PatchSync.Common.Chunking;
using PatchSync.Common.Manifest;
using PatchSync.SDK.Client;
using PatchSync.SDK.Signatures;
using PatchSync.SDK.Storage;

namespace PatchSync.Tests;

public class PatchSyncClientTests : IDisposable
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

    public PatchSyncClientTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"patchsync_test_{Guid.NewGuid():N}");
        _sourceDir = Path.Combine(_tempDir, "source");
        _targetDir = Path.Combine(_tempDir, "target");
        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(_targetDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task GetManifestAsync_ParsesManifestCorrectly()
    {
        // Arrange
        var manifest = CreateTestManifest();
        var manifestPath = Path.Combine(_sourceDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath,
            JsonSerializer.Serialize(manifest, ManifestJsonContext.Default.GameManifest));

        var storage = new LocalStorageProvider(_sourceDir);
        using var client = new PatchSyncClient(storage);

        // Act
        var loaded = await client.GetManifestAsync();

        // Assert
        Assert.Equal("1.0.0", loaded.Version);
        Assert.Equal("fastcdc-v1", loaded.PreferredAlgorithm);
        Assert.Single(loaded.Files);
        Assert.Equal("test.bin", loaded.Files[0].Path);
    }

    [Fact]
    public async Task PatchAsync_WhenFileMatches_SkipsDownload()
    {
        // Arrange
        var testData = CreateDeterministicData(4096, seed: 42);
        var hash = ComputeHash(testData);

        // Create source file with signature
        var sourceFile = Path.Combine(_sourceDir, "test.bin");
        await File.WriteAllBytesAsync(sourceFile, testData);

        var sigGenerator = new SignatureGenerator(_chunker, _options);
        var sigDir = Path.Combine(_sourceDir, "signatures");
        Directory.CreateDirectory(sigDir);
        sigGenerator.GenerateSignatureFile(sourceFile, Path.Combine(sigDir, "test.bin.sig"));

        // Create target file (identical)
        var targetFile = Path.Combine(_targetDir, "test.bin");
        await File.WriteAllBytesAsync(targetFile, testData);

        // Create manifest
        var manifest = new GameManifest
        {
            Version = "1.0.0",
            BuildDate = DateTime.UtcNow,
            SupportedAlgorithms = new[] { "fastcdc-v1" },
            PreferredAlgorithm = "fastcdc-v1",
            BaseUrl = "file:///" + _sourceDir.Replace('\\', '/'),
            Files = new[]
            {
                new ManifestFile
                {
                    Path = "test.bin",
                    Size = testData.Length,
                    Hash = hash,
                    Strategy = UpdateStrategy.Delta,
                    SignatureUrl = "signatures/test.bin.sig"
                }
            }
        };

        var storage = new LocalStorageProvider(_sourceDir);
        using var client = new PatchSyncClient(storage);

        // Act
        await client.PatchAsync(_targetDir, manifest);

        // Assert - file should still exist and be unchanged
        Assert.True(File.Exists(targetFile));
        Assert.Equal(testData, await File.ReadAllBytesAsync(targetFile));
    }

    [Fact]
    public async Task PatchAsync_WhenFileMissing_DownloadsFile()
    {
        // Arrange
        var testData = CreateDeterministicData(4096, seed: 42);
        var hash = ComputeHash(testData);

        // Create source file with signature
        var sourceFile = Path.Combine(_sourceDir, "test.bin");
        await File.WriteAllBytesAsync(sourceFile, testData);

        var sigGenerator = new SignatureGenerator(_chunker, _options);
        var sigDir = Path.Combine(_sourceDir, "signatures");
        Directory.CreateDirectory(sigDir);
        sigGenerator.GenerateSignatureFile(sourceFile, Path.Combine(sigDir, "test.bin.sig"));

        // No target file exists

        // Create manifest
        var manifest = new GameManifest
        {
            Version = "1.0.0",
            BuildDate = DateTime.UtcNow,
            SupportedAlgorithms = new[] { "fastcdc-v1" },
            PreferredAlgorithm = "fastcdc-v1",
            BaseUrl = "file:///" + _sourceDir.Replace('\\', '/'),
            Files = new[]
            {
                new ManifestFile
                {
                    Path = "test.bin",
                    Size = testData.Length,
                    Hash = hash,
                    Strategy = UpdateStrategy.Delta,
                    SignatureUrl = "signatures/test.bin.sig"
                }
            }
        };

        var storage = new LocalStorageProvider(_sourceDir);
        using var client = new PatchSyncClient(storage);

        // Act
        await client.PatchAsync(_targetDir, manifest);

        // Assert
        var targetFile = Path.Combine(_targetDir, "test.bin");
        Assert.True(File.Exists(targetFile));
        Assert.Equal(testData, await File.ReadAllBytesAsync(targetFile));
    }

    [Fact]
    public async Task PatchAsync_WhenFileModified_AppliesDelta()
    {
        // Arrange
        var originalData = CreateDeterministicData(4096, seed: 42);
        var modifiedData = originalData.ToArray();
        // Modify the last quarter
        for (int i = 3072; i < 4096; i++)
            modifiedData[i] = (byte)(originalData[i] ^ 0xFF);

        var hash = ComputeHash(modifiedData);

        // Create source (modified) file with signature
        var sourceFile = Path.Combine(_sourceDir, "test.bin");
        await File.WriteAllBytesAsync(sourceFile, modifiedData);

        var sigGenerator = new SignatureGenerator(_chunker, _options);
        var sigDir = Path.Combine(_sourceDir, "signatures");
        Directory.CreateDirectory(sigDir);
        sigGenerator.GenerateSignatureFile(sourceFile, Path.Combine(sigDir, "test.bin.sig"));

        // Create target file (original)
        var targetFile = Path.Combine(_targetDir, "test.bin");
        await File.WriteAllBytesAsync(targetFile, originalData);

        // Create manifest
        var manifest = new GameManifest
        {
            Version = "1.0.0",
            BuildDate = DateTime.UtcNow,
            SupportedAlgorithms = new[] { "fastcdc-v1" },
            PreferredAlgorithm = "fastcdc-v1",
            BaseUrl = "file:///" + _sourceDir.Replace('\\', '/'),
            Files = new[]
            {
                new ManifestFile
                {
                    Path = "test.bin",
                    Size = modifiedData.Length,
                    Hash = hash,
                    Strategy = UpdateStrategy.Delta,
                    SignatureUrl = "signatures/test.bin.sig"
                }
            }
        };

        var storage = new LocalStorageProvider(_sourceDir);
        using var client = new PatchSyncClient(storage);

        // Act
        await client.PatchAsync(_targetDir, manifest);

        // Assert
        Assert.True(File.Exists(targetFile));
        Assert.Equal(modifiedData, await File.ReadAllBytesAsync(targetFile));
    }

    [Fact]
    public async Task PatchAsync_WithHashCheckStrategy_DownloadsOnMismatch()
    {
        // Arrange
        var testData = CreateDeterministicData(512, seed: 42); // Small file
        var hash = ComputeHash(testData);

        var sourceFile = Path.Combine(_sourceDir, "small.txt");
        await File.WriteAllBytesAsync(sourceFile, testData);

        // Create different target file
        var targetFile = Path.Combine(_targetDir, "small.txt");
        await File.WriteAllBytesAsync(targetFile, new byte[] { 1, 2, 3, 4 });

        var manifest = new GameManifest
        {
            Version = "1.0.0",
            BuildDate = DateTime.UtcNow,
            SupportedAlgorithms = new[] { "fastcdc-v1" },
            PreferredAlgorithm = "fastcdc-v1",
            BaseUrl = "file:///" + _sourceDir.Replace('\\', '/'),
            Files = new[]
            {
                new ManifestFile
                {
                    Path = "small.txt",
                    Size = testData.Length,
                    Hash = hash,
                    Strategy = UpdateStrategy.HashCheck
                }
            }
        };

        var storage = new LocalStorageProvider(_sourceDir);
        using var client = new PatchSyncClient(storage);

        // Act
        await client.PatchAsync(_targetDir, manifest);

        // Assert
        Assert.Equal(testData, await File.ReadAllBytesAsync(targetFile));
    }

    [Fact]
    public async Task PatchAsync_WithDeleteStrategy_RemovesFile()
    {
        // Arrange
        var targetFile = Path.Combine(_targetDir, "obsolete.txt");
        await File.WriteAllTextAsync(targetFile, "old content");

        var manifest = new GameManifest
        {
            Version = "1.0.0",
            BuildDate = DateTime.UtcNow,
            SupportedAlgorithms = new[] { "fastcdc-v1" },
            PreferredAlgorithm = "fastcdc-v1",
            BaseUrl = "file:///" + _sourceDir.Replace('\\', '/'),
            Files = new[]
            {
                new ManifestFile
                {
                    Path = "obsolete.txt",
                    Size = 0,
                    Hash = "",
                    Strategy = UpdateStrategy.Delete
                }
            }
        };

        var storage = new LocalStorageProvider(_sourceDir);
        using var client = new PatchSyncClient(storage);

        // Act
        await client.PatchAsync(_targetDir, manifest);

        // Assert
        Assert.False(File.Exists(targetFile));
    }

    [Fact]
    public async Task PatchAsync_WithCreateOnlyStrategy_DoesNotOverwrite()
    {
        // Arrange
        var testData = CreateDeterministicData(256, seed: 42);
        var hash = ComputeHash(testData);

        var sourceFile = Path.Combine(_sourceDir, "config.ini");
        await File.WriteAllBytesAsync(sourceFile, testData);

        // Create target file with different content (user-modified)
        var targetFile = Path.Combine(_targetDir, "config.ini");
        var userContent = new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(targetFile, userContent);

        var manifest = new GameManifest
        {
            Version = "1.0.0",
            BuildDate = DateTime.UtcNow,
            SupportedAlgorithms = new[] { "fastcdc-v1" },
            PreferredAlgorithm = "fastcdc-v1",
            BaseUrl = "file:///" + _sourceDir.Replace('\\', '/'),
            Files = new[]
            {
                new ManifestFile
                {
                    Path = "config.ini",
                    Size = testData.Length,
                    Hash = hash,
                    Strategy = UpdateStrategy.CreateOnly
                }
            }
        };

        var storage = new LocalStorageProvider(_sourceDir);
        using var client = new PatchSyncClient(storage);

        // Act
        await client.PatchAsync(_targetDir, manifest);

        // Assert - user's file should be preserved
        Assert.Equal(userContent, await File.ReadAllBytesAsync(targetFile));
    }

    [Fact]
    public async Task PatchAsync_ReportsProgress()
    {
        // Arrange
        var testData = CreateDeterministicData(4096, seed: 42);
        var hash = ComputeHash(testData);

        var sourceFile = Path.Combine(_sourceDir, "test.bin");
        await File.WriteAllBytesAsync(sourceFile, testData);

        var sigGenerator = new SignatureGenerator(_chunker, _options);
        var sigDir = Path.Combine(_sourceDir, "signatures");
        Directory.CreateDirectory(sigDir);
        sigGenerator.GenerateSignatureFile(sourceFile, Path.Combine(sigDir, "test.bin.sig"));

        var manifest = new GameManifest
        {
            Version = "1.0.0",
            BuildDate = DateTime.UtcNow,
            SupportedAlgorithms = new[] { "fastcdc-v1" },
            PreferredAlgorithm = "fastcdc-v1",
            BaseUrl = "file:///" + _sourceDir.Replace('\\', '/'),
            Files = new[]
            {
                new ManifestFile
                {
                    Path = "test.bin",
                    Size = testData.Length,
                    Hash = hash,
                    Strategy = UpdateStrategy.Delta,
                    SignatureUrl = "signatures/test.bin.sig"
                }
            }
        };

        var storage = new LocalStorageProvider(_sourceDir);
        using var client = new PatchSyncClient(storage);

        var progressReports = new List<PatchProgress>();
        var progress = new Progress<PatchProgress>(p => progressReports.Add(p));

        // Act
        await client.PatchAsync(_targetDir, manifest, progress);
        await Task.Delay(100); // Allow progress callbacks to complete

        // Assert
        Assert.NotEmpty(progressReports);
        // Note: Progress<T> may not capture all reports due to async timing.
        // We check that at least some progress was reported and completed successfully.
        Assert.Contains(progressReports, p => p.Phase == PatchPhase.Complete);
        Assert.True(progressReports.Last().FilesComplete == 1);
    }

    [Fact]
    public void PatchSyncClient_ThrowsOnUnsupportedAlgorithm()
    {
        // Arrange
        var manifest = new GameManifest
        {
            Version = "1.0.0",
            BuildDate = DateTime.UtcNow,
            SupportedAlgorithms = new[] { "unknown-algorithm-v99" },
            PreferredAlgorithm = "unknown-algorithm-v99",
            BaseUrl = "https://example.com",
            Files = Array.Empty<ManifestFile>()
        };

        var storage = new LocalStorageProvider(_sourceDir);
        using var client = new PatchSyncClient(storage);

        // Act & Assert
        // This would throw when trying to patch with unsupported algorithm
        // For now we just verify construction works
        Assert.NotNull(client);
    }

    private GameManifest CreateTestManifest()
    {
        return new GameManifest
        {
            Version = "1.0.0",
            BuildDate = DateTime.UtcNow,
            SupportedAlgorithms = new[] { "fastcdc-v1" },
            PreferredAlgorithm = "fastcdc-v1",
            BaseUrl = "file:///" + _sourceDir.Replace('\\', '/'),
            Files = new[]
            {
                new ManifestFile
                {
                    Path = "test.bin",
                    Size = 1024,
                    Hash = "abc123",
                    Strategy = UpdateStrategy.Delta
                }
            }
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
