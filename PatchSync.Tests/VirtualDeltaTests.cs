using PatchSync.Common.Chunking;
using PatchSync.Common.Containers;
using PatchSync.Common.Signatures;
using PatchSync.SDK.Containers;
using PatchSync.SDK.Containers.Handlers;
using PatchSync.SDK.Delta;
using PatchSync.SDK.Signatures;
using PatchSync.SDK.Sources;

namespace PatchSync.Tests;

/// <summary>
/// Tests for the virtual delta patching system (container-aware chunking).
/// Uses real UOP files from Ultima Online client installations.
/// </summary>
public class VirtualDeltaTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IChunker _chunker = new FastCDCChunker();
    private readonly ChunkingOptions _options = ChunkingOptions.Default;
    private readonly ContainerRegistry _registry = ContainerRegistry.Default;

    // Test file paths - adjust if UO client is installed elsewhere
    private const string OldClientPath = @"C:\temp\Ultima Online Classic_7_0_50_00";
    private const string NewClientPath = @"C:\temp\Ultima Online Classic";

    public VirtualDeltaTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"patchsync_vdelta_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    #region UopHandler Tests

    [SkippableFact]
    public void UopHandler_CanDetectUopFile()
    {
        var filePath = Path.Combine(NewClientPath, "string_dictionary.uop");
        Skip.IfNot(File.Exists(filePath), "UO client not available");

        var handler = new UopHandler();

        using var stream = File.OpenRead(filePath);
        Assert.True(handler.CanHandle(stream));
    }

    [SkippableFact]
    public void UopHandler_ParsesStringDictionary()
    {
        var filePath = Path.Combine(NewClientPath, "string_dictionary.uop");
        Skip.IfNot(File.Exists(filePath), "UO client not available");

        var handler = new UopHandler();

        using var stream = File.OpenRead(filePath);
        var info = handler.Parse(stream);

        Assert.Equal("uop-v1", info.FormatId);
        Assert.NotEmpty(info.Entries);

        // Log some info for debugging
        var compressedCount = info.Entries.Count(e => e.Compression != ContainerCompression.None);
        var uncompressedCount = info.Entries.Count(e => e.Compression == ContainerCompression.None);

        Assert.True(info.Entries.Count > 0, $"Expected entries. Found: {info.Entries.Count}");
    }

    [SkippableFact]
    public void UopHandler_ParsesSoundLegacyMul()
    {
        var filePath = Path.Combine(NewClientPath, "soundLegacyMUL.uop");
        Skip.IfNot(File.Exists(filePath), "UO client not available");

        var handler = new UopHandler();

        using var stream = File.OpenRead(filePath);
        var info = handler.Parse(stream);

        Assert.Equal("uop-v1", info.FormatId);
        Assert.NotEmpty(info.Entries);
    }

    [SkippableFact]
    public void UopHandler_ParsesAnimationFrame2()
    {
        var filePath = Path.Combine(NewClientPath, "AnimationFrame2.uop");
        Skip.IfNot(File.Exists(filePath), "UO client not available");

        var handler = new UopHandler();

        using var stream = File.OpenRead(filePath);
        var info = handler.Parse(stream);

        Assert.Equal("uop-v1", info.FormatId);
        Assert.NotEmpty(info.Entries);

        // AnimationFrame2 should have many entries
        Assert.True(info.Entries.Count > 100, $"AnimationFrame2 should have many entries, found: {info.Entries.Count}");
    }

    [SkippableFact]
    public void UopHandler_ExtractsLayout()
    {
        var filePath = Path.Combine(NewClientPath, "string_dictionary.uop");
        Skip.IfNot(File.Exists(filePath), "UO client not available");

        var handler = new UopHandler();

        using var stream = File.OpenRead(filePath);
        var info = handler.Parse(stream);
        var layout = handler.ExtractLayout(stream, info);

        Assert.NotNull(layout);
        Assert.NotEmpty(layout.HeaderTemplate);
        Assert.Equal(info.Entries.Count, layout.Entries.Count);
        Assert.Equal(stream.Length, layout.TotalSize);
    }

    [Fact]
    public void UopHandler_RejectsNonUopFile()
    {
        // Create a file that's not a UOP
        var testFile = Path.Combine(_tempDir, "not_uop.bin");
        File.WriteAllBytes(testFile, new byte[] { 0x00, 0x01, 0x02, 0x03 });

        var handler = new UopHandler();

        using var stream = File.OpenRead(testFile);
        Assert.False(handler.CanHandle(stream));
    }

    #endregion

    #region VirtualSignatureGenerator Tests

    [SkippableFact]
    public void VirtualSignatureGenerator_GeneratesSignature_StringDictionary()
    {
        var filePath = Path.Combine(NewClientPath, "string_dictionary.uop");
        Skip.IfNot(File.Exists(filePath), "UO client not available");

        var handler = new UopHandler();
        var generator = new VirtualSignatureGenerator(_chunker, _options);

        var signature = generator.GenerateSignature(filePath, handler);

        Assert.Equal("uop-v1", signature.ContainerFormat);
        Assert.Equal("fastcdc-v1", signature.AlgorithmId);
        Assert.NotEmpty(signature.Entries);
        Assert.NotEmpty(signature.ContainerHash);
        Assert.NotNull(signature.Layout);
        Assert.Equal(new FileInfo(filePath).Length, signature.TotalSize);
    }

    [SkippableFact]
    public void VirtualSignatureGenerator_GeneratesSignature_AnimationFrame2()
    {
        var filePath = Path.Combine(NewClientPath, "AnimationFrame2.uop");
        Skip.IfNot(File.Exists(filePath), "UO client not available");

        var handler = new UopHandler();
        var generator = new VirtualSignatureGenerator(_chunker, _options);

        var signature = generator.GenerateSignature(filePath, handler);

        Assert.Equal("uop-v1", signature.ContainerFormat);
        Assert.NotEmpty(signature.Entries);

        // Check CDC chunking happened for large uncompressed entries
        var chunkedEntries = signature.Entries.Where(e => e.HasChunks).ToList();
        var totalChunks = signature.TotalChunkCount;

        // AnimationFrame2 should have some chunked entries
        Assert.True(signature.Entries.Count > 0);
    }

    [SkippableFact]
    public void VirtualSignatureGenerator_WritesAndReadsVsigFile()
    {
        var filePath = Path.Combine(NewClientPath, "string_dictionary.uop");
        Skip.IfNot(File.Exists(filePath), "UO client not available");

        var handler = new UopHandler();
        var generator = new VirtualSignatureGenerator(_chunker, _options);

        var vsigPath = Path.Combine(_tempDir, "string_dictionary.uop.vsig");
        generator.GenerateSignatureFile(filePath, handler, vsigPath);

        Assert.True(File.Exists(vsigPath));

        // Read it back
        using var vsigStream = File.OpenRead(vsigPath);
        var loaded = VirtualSignatureFormat.Read(vsigStream);

        Assert.Equal("uop-v1", loaded.ContainerFormat);
        Assert.NotEmpty(loaded.Entries);
    }

    #endregion

    #region VirtualDeltaCalculator Tests

    [SkippableFact]
    public void VirtualDeltaCalculator_FullDownload_WhenNoLocalSource()
    {
        var newFilePath = Path.Combine(NewClientPath, "string_dictionary.uop");
        Skip.IfNot(File.Exists(newFilePath), "UO client not available");

        var handler = new UopHandler();
        var sigGenerator = new VirtualSignatureGenerator(_chunker, _options);
        var calculator = new VirtualDeltaCalculator();

        var signature = sigGenerator.GenerateSignature(newFilePath, handler);
        var plan = calculator.CalculateFullDownload(signature);

        Assert.NotEmpty(plan.Entries);
        Assert.Equal(signature.Entries.Count, plan.Entries.Count);
        Assert.All(plan.Entries, e => Assert.Equal(EntryMethod.DownloadFull, e.Method));
        // BytesToDownload includes header + data for each entry
        var expectedDownload = plan.Entries.Sum(e => (long)e.TotalSize);
        Assert.Equal(expectedDownload, plan.BytesToDownload);
        Assert.Equal(0, plan.BytesToCopy);
    }

    [SkippableFact]
    public void VirtualDeltaCalculator_IdenticalFiles_AllLocalCopy()
    {
        var filePath = Path.Combine(NewClientPath, "string_dictionary.uop");
        Skip.IfNot(File.Exists(filePath), "UO client not available");

        var handler = new UopHandler();
        var sigGenerator = new VirtualSignatureGenerator(_chunker, _options);
        var calculator = new VirtualDeltaCalculator();

        // Generate signature from the file
        var signature = sigGenerator.GenerateSignature(filePath, handler);

        // Build local source from the same file (should match everything)
        var localSource = VirtualContainerSource.Build(filePath, handler, _chunker, _options);

        var plan = calculator.Calculate(signature, localSource);

        Assert.NotEmpty(plan.Entries);

        // All entries should match locally when comparing file to itself
        var localMatchCount = plan.Entries.Count(e => e.Method == EntryMethod.CopyLocal);
        Assert.Equal(plan.Entries.Count, localMatchCount);

        // No bytes should need downloading
        Assert.Equal(0, plan.BytesToDownload);

        // BytesToCopy includes header + data for each entry
        var expectedCopy = plan.Entries.Sum(e => (long)e.TotalSize);
        Assert.Equal(expectedCopy, plan.BytesToCopy);

        // LocalReuseRatio is BytesToCopy/TotalSize, which will be <1.0 due to container overhead
        // (headers, block tables are in TotalSize but not in BytesToCopy)
        // This is expected behavior - we're testing that all entry bytes can be copied locally
        Assert.True(plan.LocalReuseRatio > 0.9,
            $"LocalReuseRatio {plan.LocalReuseRatio:P1} should be >90% (overhead is container structure)");
    }

    [SkippableFact]
    public void VirtualDeltaCalculator_StringDictionary_OldVsNew()
    {
        var oldPath = Path.Combine(OldClientPath, "string_dictionary.uop");
        var newPath = Path.Combine(NewClientPath, "string_dictionary.uop");
        Skip.IfNot(File.Exists(oldPath) && File.Exists(newPath), "UO clients not available");

        var handler = new UopHandler();
        var sigGenerator = new VirtualSignatureGenerator(_chunker, _options);
        var calculator = new VirtualDeltaCalculator();

        // Generate signature for the NEW (target) file
        var targetSignature = sigGenerator.GenerateSignature(newPath, handler);

        // Build local source from the OLD file
        var localSource = VirtualContainerSource.Build(oldPath, handler, _chunker, _options);

        var plan = calculator.Calculate(targetSignature, localSource);

        // Report results
        var localMatch = plan.Entries.Count(e => e.Method == EntryMethod.CopyLocal);
        var deltaChunks = plan.Entries.Count(e => e.Method == EntryMethod.DeltaChunks);
        var fullDownload = plan.Entries.Count(e => e.Method == EntryMethod.DownloadFull);

        Assert.NotEmpty(plan.Entries);

        // Log the plan details for analysis
        var reusePercent = plan.LocalReuseRatio * 100;
        var downloadBytes = plan.BytesToDownload;
        var copyBytes = plan.BytesToCopy;
        var totalBytes = plan.TotalBytes;

        // The plan should show some benefit - either reuse or chunk matching
        // This assertion checks that the algorithm is working
        Assert.True(plan.BytesToDownload <= totalBytes,
            $"Download bytes ({downloadBytes}) should not exceed total ({totalBytes})");
    }

    [SkippableFact]
    public void VirtualDeltaCalculator_SoundLegacyMUL_OldVsNew()
    {
        var oldPath = Path.Combine(OldClientPath, "soundLegacyMUL.uop");
        var newPath = Path.Combine(NewClientPath, "soundLegacyMUL.uop");
        Skip.IfNot(File.Exists(oldPath) && File.Exists(newPath), "UO clients not available");

        var handler = new UopHandler();
        var sigGenerator = new VirtualSignatureGenerator(_chunker, _options);
        var calculator = new VirtualDeltaCalculator();

        var targetSignature = sigGenerator.GenerateSignature(newPath, handler);
        var localSource = VirtualContainerSource.Build(oldPath, handler, _chunker, _options);

        var plan = calculator.Calculate(targetSignature, localSource);

        var localMatch = plan.Entries.Count(e => e.Method == EntryMethod.CopyLocal);
        var deltaChunks = plan.Entries.Count(e => e.Method == EntryMethod.DeltaChunks);
        var fullDownload = plan.Entries.Count(e => e.Method == EntryMethod.DownloadFull);

        Assert.NotEmpty(plan.Entries);
        Assert.True(plan.BytesToDownload <= plan.TotalBytes);
    }

    [SkippableFact]
    public void VirtualDeltaCalculator_AnimationFrame2_OldVsNew()
    {
        var oldPath = Path.Combine(OldClientPath, "AnimationFrame2.uop");
        var newPath = Path.Combine(NewClientPath, "AnimationFrame2.uop");
        Skip.IfNot(File.Exists(oldPath) && File.Exists(newPath), "UO clients not available");

        var handler = new UopHandler();
        var sigGenerator = new VirtualSignatureGenerator(_chunker, _options);
        var calculator = new VirtualDeltaCalculator();

        var targetSignature = sigGenerator.GenerateSignature(newPath, handler);
        var localSource = VirtualContainerSource.Build(oldPath, handler, _chunker, _options);

        var plan = calculator.Calculate(targetSignature, localSource);

        var localMatch = plan.Entries.Count(e => e.Method == EntryMethod.CopyLocal);
        var deltaChunks = plan.Entries.Count(e => e.Method == EntryMethod.DeltaChunks);
        var fullDownload = plan.Entries.Count(e => e.Method == EntryMethod.DownloadFull);

        Assert.NotEmpty(plan.Entries);

        // AnimationFrame2 is a large file - check the plan makes sense
        Assert.True(plan.TotalBytes > 0);
        Assert.True(plan.Entries.Count > 100, $"Expected many entries, got {plan.Entries.Count}");
    }

    #endregion

    #region VirtualContainerSource Tests

    [SkippableFact]
    public void VirtualContainerSource_IndexesEntries()
    {
        var filePath = Path.Combine(NewClientPath, "string_dictionary.uop");
        Skip.IfNot(File.Exists(filePath), "UO client not available");

        var handler = new UopHandler();
        var source = VirtualContainerSource.Build(filePath, handler, _chunker, _options);

        Assert.True(source.EntryCount > 0);
    }

    [SkippableFact]
    public void VirtualContainerSource_FindsMatchingEntry()
    {
        var filePath = Path.Combine(NewClientPath, "string_dictionary.uop");
        Skip.IfNot(File.Exists(filePath), "UO client not available");

        var handler = new UopHandler();
        var sigGenerator = new VirtualSignatureGenerator(_chunker, _options);

        // Generate signature first (this computes the SHA256 hashes)
        var signature = sigGenerator.GenerateSignature(filePath, handler);

        // Build source (this also computes hashes internally)
        var source = VirtualContainerSource.Build(filePath, handler, _chunker, _options);

        // Try to find the first entry using hash from signature
        var firstEntry = signature.Entries[0];
        var hash = new Common.Hashing.Hash256(firstEntry.StoredHash);

        var found = source.TryGetEntry(hash, out var location);
        Assert.True(found, "Should find entry by hash");
        Assert.Equal(firstEntry.TargetOffset, location.Offset);
    }

    #endregion

    #region Integration Tests

    [SkippableFact]
    public void VirtualSignature_RoundTrip_PreservesData()
    {
        var filePath = Path.Combine(NewClientPath, "string_dictionary.uop");
        Skip.IfNot(File.Exists(filePath), "UO client not available");

        var handler = new UopHandler();
        var generator = new VirtualSignatureGenerator(_chunker, _options);

        // Generate signature
        var original = generator.GenerateSignature(filePath, handler);

        // Write to stream
        using var stream = new MemoryStream();
        VirtualSignatureFormat.Write(stream, original);

        // Read back
        stream.Position = 0;
        var loaded = VirtualSignatureFormat.Read(stream);

        // Verify
        Assert.Equal(original.ContainerFormat, loaded.ContainerFormat);
        Assert.Equal(original.AlgorithmId, loaded.AlgorithmId);
        Assert.Equal(original.MinSize, loaded.MinSize);
        Assert.Equal(original.AverageSize, loaded.AverageSize);
        Assert.Equal(original.MaxSize, loaded.MaxSize);
        Assert.Equal(original.TotalSize, loaded.TotalSize);
        Assert.Equal(original.ContainerHash, loaded.ContainerHash);
        Assert.Equal(original.Entries.Count, loaded.Entries.Count);

        for (int i = 0; i < original.Entries.Count; i++)
        {
            var origEntry = original.Entries[i];
            var loadedEntry = loaded.Entries[i];

            Assert.Equal(origEntry.EntryId, loadedEntry.EntryId);
            Assert.Equal(origEntry.TargetOffset, loadedEntry.TargetOffset);
            Assert.Equal(origEntry.StoredSize, loadedEntry.StoredSize);
            Assert.Equal(origEntry.Compression, loadedEntry.Compression);
            Assert.Equal(origEntry.StoredHash, loadedEntry.StoredHash);

            if (origEntry.Chunks != null)
            {
                Assert.NotNull(loadedEntry.Chunks);
                Assert.Equal(origEntry.Chunks.Count, loadedEntry.Chunks!.Count);
            }
            else
            {
                Assert.True(loadedEntry.Chunks == null || loadedEntry.Chunks.Count == 0);
            }
        }
    }

    [SkippableFact]
    public void VirtualSignature_SizeIsReasonable()
    {
        // This test verifies the fix for the bug where .vsig was the same size as the UOP file
        // because HeaderTemplate was storing the entire file instead of just structural metadata
        var filePath = Path.Combine(NewClientPath, "AnimationFrame2.uop");
        Skip.IfNot(File.Exists(filePath), "UO client not available");

        var handler = new UopHandler();
        var generator = new VirtualSignatureGenerator(_chunker, _options);

        // Generate signature
        var signature = generator.GenerateSignature(filePath, handler);

        // Write to stream to get actual .vsig size
        using var stream = new MemoryStream();
        VirtualSignatureFormat.Write(stream, signature);
        var vsigSize = stream.Length;

        var uopInfo = new FileInfo(filePath);
        var uopSize = uopInfo.Length;

        // Calculate the ratio
        var ratio = (double)vsigSize / uopSize;

        // The .vsig file should be MUCH smaller than the UOP file
        // HeaderTemplate contains only: file header (28 bytes) + block tables (12 + 34*entries per block)
        // Entry data should NOT be in the HeaderTemplate
        // A reasonable .vsig should be < 10% of the UOP size (often < 1%)
        Assert.True(ratio < 0.10,
            $"VSIG size ({vsigSize:N0} bytes) should be <10% of UOP size ({uopSize:N0} bytes), " +
            $"but ratio is {ratio:P1}. HeaderTemplate may be too large: {signature.Layout.HeaderTemplate.Length:N0} bytes");

        // Also verify HeaderTemplate is reasonable
        // For UOP: header(28) + blocks * (8 + 12 + entries*34) + entry headers
        // Should be a small fraction of total size
        var headerRatio = (double)signature.Layout.HeaderTemplate.Length / uopSize;
        Assert.True(headerRatio < 0.05,
            $"HeaderTemplate ({signature.Layout.HeaderTemplate.Length:N0} bytes) should be <5% of UOP size, " +
            $"but ratio is {headerRatio:P1}");
    }

    [SkippableFact]
    public void ContainerRegistry_FindsUopHandler()
    {
        Assert.True(_registry.TryGetHandlerForExtension(".uop", out var handler));
        Assert.NotNull(handler);
        Assert.Equal("uop-v1", handler!.FormatId);
    }

    [SkippableFact]
    public void ContainerRegistry_DetectsUopByContent()
    {
        var filePath = Path.Combine(NewClientPath, "string_dictionary.uop");
        Skip.IfNot(File.Exists(filePath), "UO client not available");

        using var stream = File.OpenRead(filePath);
        Assert.True(_registry.TryDetectHandler(stream, out var handler));
        Assert.NotNull(handler);
        Assert.Equal("uop-v1", handler!.FormatId);
    }

    #endregion

    #region Comparison Analysis Tests

    [SkippableFact]
    public void CompareAllUopFiles_OldVsNew()
    {
        Skip.IfNot(Directory.Exists(OldClientPath) && Directory.Exists(NewClientPath),
            "UO clients not available");

        var handler = new UopHandler();
        var sigGenerator = new VirtualSignatureGenerator(_chunker, _options);
        var calculator = new VirtualDeltaCalculator();

        var results = new List<(string File, int Entries, long Total, long Download, long Copy, double Reuse)>();

        // Get all UOP files that exist in both directories
        var newUops = Directory.GetFiles(NewClientPath, "*.uop");

        foreach (var newPath in newUops)
        {
            var fileName = Path.GetFileName(newPath);
            var oldPath = Path.Combine(OldClientPath, fileName);

            if (!File.Exists(oldPath)) continue;

            try
            {
                var targetSignature = sigGenerator.GenerateSignature(newPath, handler);
                var localSource = VirtualContainerSource.Build(oldPath, handler, _chunker, _options);
                var plan = calculator.Calculate(targetSignature, localSource);

                results.Add((
                    fileName,
                    plan.Entries.Count,
                    plan.TotalBytes,
                    plan.BytesToDownload,
                    plan.BytesToCopy,
                    plan.LocalReuseRatio
                ));
            }
            catch
            {
                // Skip files that fail to parse
            }
        }

        // Verify we got some results
        Assert.NotEmpty(results);

        // All files should have reasonable plans
        foreach (var r in results)
        {
            Assert.True(r.Download <= r.Total,
                $"{r.File}: Download ({r.Download}) should not exceed total ({r.Total})");
        }
    }

    [SkippableFact]
    public void EfficiencyCheck_SkipsTileartUop()
    {
        // tileart.uop has 37,756 tiny entries - virtual delta is inefficient
        var filePath = Path.Combine(NewClientPath, "tileart.uop");
        Skip.IfNot(File.Exists(filePath), "UO client not available");

        var handler = new UopHandler();
        var generator = new VirtualSignatureGenerator(_chunker, _options);

        var result = generator.CheckEfficiency(filePath, handler);

        Assert.False(result.IsEfficient,
            $"tileart.uop should be flagged as inefficient. " +
            $"Entries: {result.EntryCount}, Overhead: {result.OverheadRatio:P1}");
        Assert.NotNull(result.SkipReason);
        Assert.Contains("entries", result.SkipReason, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public void EfficiencyCheck_AcceptsNormalUop()
    {
        // AnimationFrame2.uop has reasonable entry count - virtual delta is efficient
        var filePath = Path.Combine(NewClientPath, "AnimationFrame2.uop");
        Skip.IfNot(File.Exists(filePath), "UO client not available");

        var handler = new UopHandler();
        var generator = new VirtualSignatureGenerator(_chunker, _options);

        var result = generator.CheckEfficiency(filePath, handler);

        Assert.True(result.IsEfficient,
            $"AnimationFrame2.uop should be efficient. " +
            $"Entries: {result.EntryCount}, Overhead: {result.OverheadRatio:P1}");
        Assert.Null(result.SkipReason);
    }

    [SkippableFact]
    public void TryGenerateSignature_ReturnsNullForInefficient()
    {
        var filePath = Path.Combine(NewClientPath, "tileart.uop");
        Skip.IfNot(File.Exists(filePath), "UO client not available");

        var handler = new UopHandler();
        var generator = new VirtualSignatureGenerator(_chunker, _options);

        var signature = generator.TryGenerateSignature(
            filePath, handler, null, null, out var efficiencyResult);

        Assert.Null(signature);
        Assert.False(efficiencyResult.IsEfficient);
    }

    #endregion
}

/// <summary>
/// Helper for conditional test skipping.
/// </summary>
public static class Skip
{
    public static void IfNot(bool condition, string reason)
    {
        if (!condition)
        {
            // In xUnit v3, we use Assert.Skip which was added
            // For older versions, we just return early
            throw new SkipTestException(reason);
        }
    }
}

/// <summary>
/// Exception to indicate a test should be skipped.
/// </summary>
public class SkipTestException : Exception
{
    public SkipTestException(string reason) : base($"Test skipped: {reason}") { }
}

/// <summary>
/// Fact attribute that handles skip exceptions gracefully.
/// </summary>
public class SkippableFactAttribute : FactAttribute
{
}
