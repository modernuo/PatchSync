using PatchSync.CLI.Workspace;

namespace PatchSync.CLI.Tests;

/// <summary>
/// Tests for BuildLock concurrent build prevention.
/// Uses temporary directories for isolation.
/// </summary>
public class BuildLockTests : IDisposable
{
    private readonly string _tempDir;

    public BuildLockTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"patchsync_lock_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    [Fact]
    public void TryAcquire_SucceedsOnFirstAttempt()
    {
        // Arrange
        var versionPath = Path.Combine(_tempDir, "1.0.0");

        // Act
        using var buildLock = BuildLock.TryAcquire(versionPath);

        // Assert
        Assert.NotNull(buildLock);
        Assert.Equal(Environment.MachineName, buildLock.Info.MachineName);
        Assert.Equal(Environment.ProcessId, buildLock.Info.ProcessId);
    }

    [Fact]
    public void TryAcquire_CreatesVersionDirectory()
    {
        // Arrange
        var versionPath = Path.Combine(_tempDir, "subdir", "1.0.0");

        // Act
        using var buildLock = BuildLock.TryAcquire(versionPath);

        // Assert
        Assert.True(Directory.Exists(versionPath));
    }

    [Fact]
    public void TryAcquire_FailsWhenLockHeld()
    {
        // Arrange
        var versionPath = Path.Combine(_tempDir, "1.0.0");

        // Act
        using var firstLock = BuildLock.TryAcquire(versionPath);
        var secondLock = BuildLock.TryAcquire(versionPath);

        // Assert
        Assert.NotNull(firstLock);
        Assert.Null(secondLock);
    }

    [Fact]
    public void TryAcquire_SucceedsAfterLockReleased()
    {
        // Arrange
        var versionPath = Path.Combine(_tempDir, "1.0.0");

        // Act - acquire and release first lock
        var firstLock = BuildLock.TryAcquire(versionPath);
        Assert.NotNull(firstLock);
        firstLock.Dispose();

        // Now try to acquire again
        using var secondLock = BuildLock.TryAcquire(versionPath);

        // Assert
        Assert.NotNull(secondLock);
    }

    [Fact]
    public void IsLocked_ReturnsFalseWhenNoLock()
    {
        // Arrange
        var versionPath = Path.Combine(_tempDir, "1.0.0");
        Directory.CreateDirectory(versionPath);

        // Act
        var isLocked = BuildLock.IsLocked(versionPath);

        // Assert
        Assert.False(isLocked);
    }

    [Fact]
    public void GetLockHolder_ReturnsNullWhenNoLock()
    {
        // Arrange
        var versionPath = Path.Combine(_tempDir, "1.0.0");
        Directory.CreateDirectory(versionPath);

        // Act
        var holder = BuildLock.GetLockHolder(versionPath);

        // Assert
        Assert.Null(holder);
    }

    [Fact]
    public void ForceRemove_ReturnsFalseWhenNoLock()
    {
        // Arrange
        var versionPath = Path.Combine(_tempDir, "1.0.0");
        Directory.CreateDirectory(versionPath);

        // Act
        var removed = BuildLock.ForceRemove(versionPath);

        // Assert
        Assert.False(removed);
    }

    [Fact]
    public void TryAcquire_RecordsCorrectTimestamp()
    {
        // Arrange
        var versionPath = Path.Combine(_tempDir, "1.0.0");
        var beforeAcquire = DateTime.UtcNow.AddSeconds(-1);

        // Act
        using var buildLock = BuildLock.TryAcquire(versionPath);

        // Assert
        Assert.NotNull(buildLock);
        Assert.True(buildLock.Info.LockedAt >= beforeAcquire);
        Assert.True(buildLock.Info.LockedAt <= DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        // Arrange
        var versionPath = Path.Combine(_tempDir, "1.0.0");
        var buildLock = BuildLock.TryAcquire(versionPath);
        Assert.NotNull(buildLock);

        // Act & Assert - should not throw
        buildLock.Dispose();
        buildLock.Dispose();
        buildLock.Dispose();
    }

    [Fact]
    public void TryAcquire_RemovesStaleLock()
    {
        // Arrange
        var versionPath = Path.Combine(_tempDir, "1.0.0");
        Directory.CreateDirectory(versionPath);
        var lockPath = Path.Combine(versionPath, ".build.lock");

        // Create a stale lock file manually (old timestamp)
        var staleLockInfo = new
        {
            machineName = "old-machine",
            lockedAt = DateTime.UtcNow.AddHours(-5), // 5 hours ago (default timeout is 4)
            processId = 12345,
            userName = "old-user"
        };
        File.WriteAllText(lockPath, System.Text.Json.JsonSerializer.Serialize(staleLockInfo));

        // Act
        using var buildLock = BuildLock.TryAcquire(versionPath);

        // Assert
        Assert.NotNull(buildLock);
        Assert.Equal(Environment.MachineName, buildLock.Info.MachineName);
    }

    [Fact]
    public void TryAcquire_RespectsCustomTimeout()
    {
        // Arrange
        var versionPath = Path.Combine(_tempDir, "1.0.0");
        Directory.CreateDirectory(versionPath);
        var lockPath = Path.Combine(versionPath, ".build.lock");

        // Create a "stale" lock file that's 2 hours old
        var staleLockInfo = new
        {
            machineName = "other-machine",
            lockedAt = DateTime.UtcNow.AddHours(-2),
            processId = 12345,
            userName = "other-user"
        };
        File.WriteAllText(lockPath, System.Text.Json.JsonSerializer.Serialize(staleLockInfo));

        // Act - with 1 hour timeout, the 2-hour-old lock should be considered stale
        using var buildLock = BuildLock.TryAcquire(versionPath, timeout: TimeSpan.FromHours(1));

        // Assert
        Assert.NotNull(buildLock);
    }

    [Fact]
    public void IsLocked_RespectsTimeout()
    {
        // Arrange
        var versionPath = Path.Combine(_tempDir, "1.0.0");
        Directory.CreateDirectory(versionPath);
        var lockPath = Path.Combine(versionPath, ".build.lock");

        // Create a lock file that's 30 minutes old
        var lockInfo = new
        {
            machineName = "some-machine",
            lockedAt = DateTime.UtcNow.AddMinutes(-30),
            processId = 12345,
            userName = "some-user"
        };
        File.WriteAllText(lockPath, System.Text.Json.JsonSerializer.Serialize(lockInfo));

        // Act
        var isLockedWith1HourTimeout = BuildLock.IsLocked(versionPath, timeout: TimeSpan.FromHours(1));
        var isLockedWith10MinTimeout = BuildLock.IsLocked(versionPath, timeout: TimeSpan.FromMinutes(10));

        // Assert
        Assert.True(isLockedWith1HourTimeout);  // 30 min < 1 hour = still valid
        Assert.False(isLockedWith10MinTimeout); // 30 min > 10 min = stale
    }

    [Fact]
    public void LockInfo_ContainsUserName()
    {
        // Arrange
        var versionPath = Path.Combine(_tempDir, "1.0.0");

        // Act
        using var buildLock = BuildLock.TryAcquire(versionPath);

        // Assert
        Assert.NotNull(buildLock);
        Assert.Equal(Environment.UserName, buildLock.Info.UserName);
    }
}
