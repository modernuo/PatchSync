using System.Security.Cryptography;
using PatchSync.Common.Chunking;
using PatchSync.Common.Manifest;
using PatchSync.SDK.Client;
using PatchSync.SDK.Signatures;
using PatchSync.SDK.Storage;

namespace PatchSync.Tests;

/// <summary>
/// Tests for ScanAsync and VerifyAsync methods.
/// </summary>
public class ScanVerifyTests : IDisposable
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

    public ScanVerifyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scanverify_test_{Guid.NewGuid():N}");
        _sourceDir = Path.Combine(_tempDir, "source");
        _targetDir = Path.Combine(_tempDir, "target");
        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(_targetDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    #region ScanAsync Tests

    [Fact]
    public async Task ScanAsync_WhenAllUpToDate_ReportsUpToDate()
    {
        // Arrange
        var testData = CreateDeterministicData(2048, seed: 42);
        var hash = ComputeHash(testData);

        await SetupSourceFile("test.bin", testData);
        await File.WriteAllBytesAsync(Path.Combine(_targetDir, "test.bin"), testData);

        var manifest = CreateManifest(new[]
        {
            CreateManifestFile("test.bin", testData.Length, hash)
        });

        var storage = new LocalStorageProvider(_sourceDir);
        using var client = new PatchSyncClient(storage);

        // Act
        var result = await client.ScanAsync(_targetDir, manifest);

        // Assert
        Assert.True(result.IsUpToDate);
        Assert.Single(result.UpToDate);
        Assert.Empty(result.NeedsUpdate);
        Assert.Empty(result.Missing);
        Assert.Empty(result.ToDelete);
        Assert.Equal(0, result.BytesToDownload);
    }

    [Fact]
    public async Task ScanAsync_WhenFileMissing_ReportsMissing()
    {
        // Arrange
        var testData = CreateDeterministicData(2048, seed: 42);
        var hash = ComputeHash(testData);

        await SetupSourceFile("test.bin", testData);
        // Don't create target file

        var manifest = CreateManifest(new[]
        {
            CreateManifestFile("test.bin", testData.Length, hash)
        });

        var storage = new LocalStorageProvider(_sourceDir);
        using var client = new PatchSyncClient(storage);

        // Act
        var result = await client.ScanAsync(_targetDir, manifest);

        // Assert
        Assert.False(result.IsUpToDate);
        Assert.Empty(result.UpToDate);
        Assert.Empty(result.NeedsUpdate);
        Assert.Single(result.Missing);
        Assert.Equal("test.bin", result.Missing[0].Path);
        Assert.Equal(testData.Length, result.BytesToDownload);
    }

    [Fact]
    public async Task ScanAsync_WhenFileModified_ReportsNeedsUpdate()
    {
        // Arrange
        var sourceData = CreateDeterministicData(2048, seed: 42);
        var targetData = CreateDeterministicData(2048, seed: 99);
        var hash = ComputeHash(sourceData);

        await SetupSourceFile("test.bin", sourceData);
        await File.WriteAllBytesAsync(Path.Combine(_targetDir, "test.bin"), targetData);

        var manifest = CreateManifest(new[]
        {
            CreateManifestFile("test.bin", sourceData.Length, hash)
        });

        var storage = new LocalStorageProvider(_sourceDir);
        using var client = new PatchSyncClient(storage);

        // Act
        var result = await client.ScanAsync(_targetDir, manifest);

        // Assert
        Assert.False(result.IsUpToDate);
        Assert.Empty(result.UpToDate);
        Assert.Single(result.NeedsUpdate);
        Assert.Empty(result.Missing);
        Assert.Equal(FileStatusType.NeedsUpdate, result.NeedsUpdate[0].Status);
    }

    [Fact]
    public async Task ScanAsync_WhenFileMarkedForDelete_ReportsToDelete()
    {
        // Arrange
        await File.WriteAllTextAsync(Path.Combine(_targetDir, "obsolete.txt"), "delete me");

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
        using var client = new PatchSyncClient(storage);

        // Act
        var result = await client.ScanAsync(_targetDir, manifest);

        // Assert
        Assert.False(result.IsUpToDate);
        Assert.Single(result.ToDelete);
        Assert.Equal("obsolete.txt", result.ToDelete[0].Path);
    }

    [Fact]
    public async Task ScanAsync_MixedStatus_ReportsCorrectly()
    {
        // Arrange
        var upToDateData = CreateDeterministicData(1024, seed: 1);
        var needsUpdateSource = CreateDeterministicData(1024, seed: 2);
        var needsUpdateTarget = CreateDeterministicData(1024, seed: 3);
        var missingData = CreateDeterministicData(1024, seed: 4);

        await SetupSourceFile("uptodate.bin", upToDateData);
        await SetupSourceFile("needsupdate.bin", needsUpdateSource);
        await SetupSourceFile("missing.bin", missingData);

        await File.WriteAllBytesAsync(Path.Combine(_targetDir, "uptodate.bin"), upToDateData);
        await File.WriteAllBytesAsync(Path.Combine(_targetDir, "needsupdate.bin"), needsUpdateTarget);
        await File.WriteAllTextAsync(Path.Combine(_targetDir, "obsolete.txt"), "delete");
        // missing.bin is not created in target

        var manifest = CreateManifest(new[]
        {
            CreateManifestFile("uptodate.bin", upToDateData.Length, ComputeHash(upToDateData)),
            CreateManifestFile("needsupdate.bin", needsUpdateSource.Length, ComputeHash(needsUpdateSource)),
            CreateManifestFile("missing.bin", missingData.Length, ComputeHash(missingData)),
            new ManifestFile { Path = "obsolete.txt", Size = 0, Hash = "", Strategy = UpdateStrategy.Delete }
        });

        var storage = new LocalStorageProvider(_sourceDir);
        using var client = new PatchSyncClient(storage);

        // Act
        var result = await client.ScanAsync(_targetDir, manifest);

        // Assert
        Assert.False(result.IsUpToDate);
        Assert.Single(result.UpToDate);
        Assert.Single(result.NeedsUpdate);
        Assert.Single(result.Missing);
        Assert.Single(result.ToDelete);
        Assert.Equal(3, result.TotalFiles); // Excludes ToDelete from TotalFiles
    }

    [Fact]
    public async Task ScanAsync_ReportsProgress()
    {
        // Arrange
        for (int i = 0; i < 5; i++)
        {
            var data = CreateDeterministicData(512, seed: i);
            await SetupSourceFile($"file{i}.bin", data);
            await File.WriteAllBytesAsync(Path.Combine(_targetDir, $"file{i}.bin"), data);
        }

        var manifestFiles = Enumerable.Range(0, 5).Select(i =>
        {
            var data = CreateDeterministicData(512, seed: i);
            return CreateManifestFile($"file{i}.bin", data.Length, ComputeHash(data));
        }).ToArray();

        var manifest = CreateManifest(manifestFiles);

        var storage = new LocalStorageProvider(_sourceDir);
        using var client = new PatchSyncClient(storage);

        var progressReports = new List<ScanProgress>();
        var progress = new Progress<ScanProgress>(p => progressReports.Add(p));

        // Act
        await client.ScanAsync(_targetDir, manifest, progress);
        await Task.Delay(50);

        // Assert
        Assert.NotEmpty(progressReports);
        Assert.Contains(progressReports, p => p.FilesScanned == 5);
    }

    #endregion

    #region Strategy Breakdown Tests

    [Fact]
    public async Task ScanAsync_GetStrategyBreakdown_ReturnsCorrectEstimates()
    {
        // Arrange - Create files with different strategies
        var deltaData = CreateDeterministicData(100_000, seed: 1);
        var virtualDeltaData = CreateDeterministicData(200_000, seed: 2);
        var hashCheckData = CreateDeterministicData(50_000, seed: 3);
        var missingData = CreateDeterministicData(80_000, seed: 4);

        await SetupSourceFile("delta.bin", deltaData);
        await SetupSourceFile("virtual.uop", virtualDeltaData);
        await SetupSourceFile("small.txt", hashCheckData);
        await SetupSourceFile("missing.bin", missingData);

        // Create modified local files (not matching server)
        await File.WriteAllBytesAsync(Path.Combine(_targetDir, "delta.bin"),
            CreateDeterministicData(100_000, seed: 10)); // Modified
        await File.WriteAllBytesAsync(Path.Combine(_targetDir, "virtual.uop"),
            CreateDeterministicData(200_000, seed: 20)); // Modified
        await File.WriteAllBytesAsync(Path.Combine(_targetDir, "small.txt"),
            CreateDeterministicData(50_000, seed: 30)); // Modified
        // missing.bin is not created

        var manifest = CreateManifest(new[]
        {
            new ManifestFile
            {
                Path = "delta.bin",
                Size = deltaData.Length,
                Hash = ComputeHash(deltaData),
                Strategy = UpdateStrategy.Delta,
                SignatureUrl = "signatures/delta.bin.sig"
            },
            new ManifestFile
            {
                Path = "virtual.uop",
                Size = virtualDeltaData.Length,
                Hash = ComputeHash(virtualDeltaData),
                Strategy = UpdateStrategy.VirtualDelta,
                VirtualSignatureUrl = "signatures/virtual.uop.vsig",
                ContainerFormat = "uop-v1"
            },
            new ManifestFile
            {
                Path = "small.txt",
                Size = hashCheckData.Length,
                Hash = ComputeHash(hashCheckData),
                Strategy = UpdateStrategy.HashCheck
            },
            new ManifestFile
            {
                Path = "missing.bin",
                Size = missingData.Length,
                Hash = ComputeHash(missingData),
                Strategy = UpdateStrategy.Delta,
                SignatureUrl = "signatures/missing.bin.sig"
            }
        });

        var storage = new LocalStorageProvider(_sourceDir);
        using var client = new PatchSyncClient(storage);

        // Act
        var result = await client.ScanAsync(_targetDir, manifest);
        var breakdown = result.GetStrategyBreakdown();

        // Assert - Check counts
        Assert.Equal(2, breakdown.DeltaFileCount); // delta.bin (update) + missing.bin (missing)
        Assert.Equal(1, breakdown.VirtualDeltaFileCount); // virtual.uop (update)
        Assert.Equal(1, breakdown.FullDownloadFileCount); // small.txt (HashCheck)

        // Check worst case bytes
        Assert.Equal(100_000 + 80_000, breakdown.DeltaWorstCaseBytes); // delta.bin + missing.bin
        Assert.Equal(200_000, breakdown.VirtualDeltaWorstCaseBytes); // virtual.uop
        Assert.Equal(50_000, breakdown.FullDownloadBytes); // small.txt
        Assert.Equal(430_000, breakdown.TotalWorstCaseBytes);

        // Check estimated bytes:
        // - delta.bin (update): 100_000 * 0.20 = 20_000
        // - missing.bin (missing): 80_000 (full download)
        // - virtual.uop (update): 200_000 * 0.10 = 20_000
        // - small.txt (full): 50_000
        Assert.Equal(20_000 + 80_000, breakdown.DeltaEstimatedBytes);
        Assert.Equal(20_000, breakdown.VirtualDeltaEstimatedBytes);
        Assert.Equal(50_000, breakdown.FullDownloadBytes);
        Assert.Equal(170_000, breakdown.TotalEstimatedBytes);

        // Check savings
        Assert.Equal(260_000, breakdown.EstimatedSavings); // 430_000 - 170_000
        Assert.True(breakdown.SavingsPercentage > 0.5); // > 50% savings
    }

    [Fact]
    public async Task ScanAsync_GetStrategyBreakdown_WhenUpToDate_ReturnsZeroBytes()
    {
        // Arrange
        var testData = CreateDeterministicData(10_000, seed: 42);
        await SetupSourceFile("test.bin", testData);
        await File.WriteAllBytesAsync(Path.Combine(_targetDir, "test.bin"), testData);

        var manifest = CreateManifest(new[]
        {
            CreateManifestFile("test.bin", testData.Length, ComputeHash(testData))
        });

        var storage = new LocalStorageProvider(_sourceDir);
        using var client = new PatchSyncClient(storage);

        // Act
        var result = await client.ScanAsync(_targetDir, manifest);
        var breakdown = result.GetStrategyBreakdown();

        // Assert
        Assert.Equal(0, breakdown.TotalWorstCaseBytes);
        Assert.Equal(0, breakdown.TotalEstimatedBytes);
        Assert.Equal(0, breakdown.EstimatedSavings);
    }

    #endregion

    #region VerifyAsync Tests

    [Fact]
    public async Task VerifyAsync_WhenAllValid_ReportsSuccess()
    {
        // Arrange
        var testData = CreateDeterministicData(2048, seed: 42);
        var hash = ComputeHash(testData);

        await SetupSourceFile("test.bin", testData);
        await File.WriteAllBytesAsync(Path.Combine(_targetDir, "test.bin"), testData);

        var manifest = CreateManifest(new[]
        {
            CreateManifestFile("test.bin", testData.Length, hash)
        });

        var storage = new LocalStorageProvider(_sourceDir);
        using var client = new PatchSyncClient(storage);

        // Act
        var result = await client.VerifyAsync(_targetDir, manifest);

        // Assert
        Assert.True(result.Success);
        Assert.Single(result.Passed);
        Assert.Empty(result.Failed);
        Assert.Equal(1, result.TotalFiles);
    }

    [Fact]
    public async Task VerifyAsync_WhenFileMissing_ReportsFailed()
    {
        // Arrange
        var testData = CreateDeterministicData(2048, seed: 42);
        var hash = ComputeHash(testData);

        await SetupSourceFile("test.bin", testData);
        // Don't create target

        var manifest = CreateManifest(new[]
        {
            CreateManifestFile("test.bin", testData.Length, hash)
        });

        var storage = new LocalStorageProvider(_sourceDir);
        using var client = new PatchSyncClient(storage);

        // Act
        var result = await client.VerifyAsync(_targetDir, manifest);

        // Assert
        Assert.False(result.Success);
        Assert.Empty(result.Passed);
        Assert.Single(result.Failed);
        Assert.Equal(FileStatusType.Missing, result.Failed[0].Status);
    }

    [Fact]
    public async Task VerifyAsync_WhenHashMismatch_ReportsFailed()
    {
        // Arrange
        var sourceData = CreateDeterministicData(2048, seed: 42);
        var targetData = CreateDeterministicData(2048, seed: 99);
        var hash = ComputeHash(sourceData);

        await SetupSourceFile("test.bin", sourceData);
        await File.WriteAllBytesAsync(Path.Combine(_targetDir, "test.bin"), targetData);

        var manifest = CreateManifest(new[]
        {
            CreateManifestFile("test.bin", sourceData.Length, hash)
        });

        var storage = new LocalStorageProvider(_sourceDir);
        using var client = new PatchSyncClient(storage);

        // Act
        var result = await client.VerifyAsync(_targetDir, manifest);

        // Assert
        Assert.False(result.Success);
        Assert.Empty(result.Passed);
        Assert.Single(result.Failed);
        Assert.Equal(FileStatusType.NeedsUpdate, result.Failed[0].Status);
    }

    [Fact]
    public async Task VerifyAsync_SkipsDeletedFiles()
    {
        // Arrange
        var testData = CreateDeterministicData(1024, seed: 42);

        await SetupSourceFile("keep.bin", testData);
        await File.WriteAllBytesAsync(Path.Combine(_targetDir, "keep.bin"), testData);

        var manifest = CreateManifest(new[]
        {
            CreateManifestFile("keep.bin", testData.Length, ComputeHash(testData)),
            new ManifestFile { Path = "deleted.bin", Size = 0, Hash = "", Strategy = UpdateStrategy.Delete }
        });

        var storage = new LocalStorageProvider(_sourceDir);
        using var client = new PatchSyncClient(storage);

        // Act
        var result = await client.VerifyAsync(_targetDir, manifest);

        // Assert
        Assert.True(result.Success);
        Assert.Single(result.Passed);
        Assert.Equal(1, result.TotalFiles); // Only counts non-deleted files
    }

    [Fact]
    public async Task VerifyAsync_ReportsProgress()
    {
        // Arrange
        for (int i = 0; i < 5; i++)
        {
            var data = CreateDeterministicData(512, seed: i);
            await SetupSourceFile($"file{i}.bin", data);
            await File.WriteAllBytesAsync(Path.Combine(_targetDir, $"file{i}.bin"), data);
        }

        var manifestFiles = Enumerable.Range(0, 5).Select(i =>
        {
            var data = CreateDeterministicData(512, seed: i);
            return CreateManifestFile($"file{i}.bin", data.Length, ComputeHash(data));
        }).ToArray();

        var manifest = CreateManifest(manifestFiles);

        var storage = new LocalStorageProvider(_sourceDir);
        using var client = new PatchSyncClient(storage);

        var progressReports = new List<VerifyProgress>();
        var progress = new Progress<VerifyProgress>(p => progressReports.Add(p));

        // Act
        await client.VerifyAsync(_targetDir, manifest, progress);
        await Task.Delay(50);

        // Assert
        Assert.NotEmpty(progressReports);
        Assert.Contains(progressReports, p => p.FilesVerified == 5);
        Assert.Contains(progressReports, p => p.FilesPassed == 5);
    }

    [Fact]
    public async Task VerifyAsync_MixedResults_ReportsCorrectly()
    {
        // Arrange
        var validData = CreateDeterministicData(1024, seed: 1);
        var invalidSource = CreateDeterministicData(1024, seed: 2);
        var invalidTarget = CreateDeterministicData(1024, seed: 3);
        var missingData = CreateDeterministicData(1024, seed: 4);

        await SetupSourceFile("valid.bin", validData);
        await SetupSourceFile("invalid.bin", invalidSource);
        await SetupSourceFile("missing.bin", missingData);

        await File.WriteAllBytesAsync(Path.Combine(_targetDir, "valid.bin"), validData);
        await File.WriteAllBytesAsync(Path.Combine(_targetDir, "invalid.bin"), invalidTarget);
        // missing.bin not created

        var manifest = CreateManifest(new[]
        {
            CreateManifestFile("valid.bin", validData.Length, ComputeHash(validData)),
            CreateManifestFile("invalid.bin", invalidSource.Length, ComputeHash(invalidSource)),
            CreateManifestFile("missing.bin", missingData.Length, ComputeHash(missingData))
        });

        var storage = new LocalStorageProvider(_sourceDir);
        using var client = new PatchSyncClient(storage);

        // Act
        var result = await client.VerifyAsync(_targetDir, manifest);

        // Assert
        Assert.False(result.Success);
        Assert.Single(result.Passed);
        Assert.Equal(2, result.Failed.Count);
        Assert.Equal(3, result.TotalFiles);
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
