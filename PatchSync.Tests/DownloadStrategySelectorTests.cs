using PatchSync.Common.Signatures;
using PatchSync.SDK.Delta;
using PatchSync.SDK.Sources;

namespace PatchSync.Tests;

public class DownloadStrategySelectorTests
{
    [Fact]
    public void Select_WhenNoLocalData_ReturnsCompressedIfAvailable()
    {
        // Arrange
        var plan = CreatePlan(bytesToDownload: 1000, bytesToCopy: 0);
        var selector = new DownloadStrategySelector();

        // Act
        var decision = selector.Select(plan, compressedSize: 800, fullSize: 1000);

        // Assert
        Assert.Equal(DownloadMethod.Compressed, decision.Method);
        Assert.Equal(800, decision.EstimatedBytes);
    }

    [Fact]
    public void Select_WhenNoLocalDataAndNoCompressed_ReturnsFull()
    {
        // Arrange
        var plan = CreatePlan(bytesToDownload: 1000, bytesToCopy: 0);
        var selector = new DownloadStrategySelector();

        // Act
        var decision = selector.Select(plan, compressedSize: 0, fullSize: 1000);

        // Assert
        Assert.Equal(DownloadMethod.Full, decision.Method);
        Assert.Equal(1000, decision.EstimatedBytes);
    }

    [Fact]
    public void Select_WhenDeltaIsSignificantlyBetter_ReturnsDelta()
    {
        // Arrange: Delta is 200 bytes, compressed is 800 bytes
        var plan = CreatePlan(bytesToDownload: 200, bytesToCopy: 800);
        var selector = new DownloadStrategySelector(deltaThreshold: 0.8);

        // Act
        var decision = selector.Select(plan, compressedSize: 800, fullSize: 1000);

        // Assert: 200 < 800 * 0.8 (640), so delta wins
        Assert.Equal(DownloadMethod.Delta, decision.Method);
        Assert.Equal(200, decision.EstimatedBytes);
    }

    [Fact]
    public void Select_WhenDeltaIsNotMuchBetter_ReturnsCompressed()
    {
        // Arrange: Delta is 700 bytes, compressed is 800 bytes
        var plan = CreatePlan(bytesToDownload: 700, bytesToCopy: 300);
        var selector = new DownloadStrategySelector(deltaThreshold: 0.8);

        // Act
        var decision = selector.Select(plan, compressedSize: 800, fullSize: 1000);

        // Assert: 700 >= 800 * 0.8 (640), so compressed wins
        Assert.Equal(DownloadMethod.Compressed, decision.Method);
        Assert.Equal(800, decision.EstimatedBytes);
    }

    [Theory]
    [InlineData(false, 0, 1000, false)]           // No local file
    [InlineData(true, 100000, 100000, true)]      // Normal case (same size, large enough)
    [InlineData(true, 80000, 100000, true)]       // Normal case (similar sizes)
    [InlineData(true, 1000, 1000, false)]         // Too small (< 64KB)
    [InlineData(true, 100, 100000, false)]        // Size ratio too small (< 0.1)
    [InlineData(true, 100000, 1000, false)]       // Size ratio too large (> 10)
    [InlineData(true, 100, 50000, false)]         // Small file (< 64KB)
    public void ShouldAttemptDelta_ReturnsExpected(
        bool localExists, long localSize, long remoteSize, bool expected)
    {
        var result = DownloadStrategySelector.ShouldAttemptDelta(localExists, localSize, remoteSize);
        Assert.Equal(expected, result);
    }

    private static DeltaPlan CreatePlan(long bytesToDownload, long bytesToCopy)
    {
        var actions = new List<DeltaAction>();

        // Add download actions
        if (bytesToDownload > 0)
        {
            actions.Add(new DownloadRemote(0, (int)bytesToDownload, default));
        }

        // Add copy actions
        if (bytesToCopy > 0)
        {
            actions.Add(new CopyLocal(
                bytesToDownload,
                (int)bytesToCopy,
                new ChunkLocation("test.bin", 0, (int)bytesToCopy),
                default));
        }

        var signature = new SignatureFile
        {
            AlgorithmId = "fastcdc-v1",
            MinSize = 64,
            AverageSize = 128,
            MaxSize = 256,
            Chunks = new List<SignatureChunk>()
        };

        return new DeltaPlan(actions, signature);
    }
}
