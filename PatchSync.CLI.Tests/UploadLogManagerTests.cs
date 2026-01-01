using PatchSync.CLI.Workspace;

namespace PatchSync.CLI.Tests;

/// <summary>
/// Tests for UploadLogManager which tracks publish operations.
/// </summary>
public class UploadLogManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly UploadLogManager _logManager;

    public UploadLogManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"patchsync_uploadlog_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _logManager = new UploadLogManager(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    #region StartLogAsync Tests

    [Fact]
    public async Task StartLogAsync_CreatesNewLogEntry()
    {
        // Act
        var entry = await _logManager.StartLogAsync("prod", "1.0.0", "default");

        // Assert
        Assert.NotNull(entry);
        Assert.NotEmpty(entry.Id);
        Assert.Equal("prod", entry.Channel);
        Assert.Equal("1.0.0", entry.Version);
        Assert.Equal("default", entry.Profile);
        Assert.Equal(UploadStatus.InProgress, entry.Status);
    }

    [Fact]
    public async Task StartLogAsync_CreatesUploadsDirectory()
    {
        // Act
        await _logManager.StartLogAsync("prod", "1.0.0", "default");

        // Assert
        var uploadsDir = Path.Combine(_tempDir, ".patchsync", "uploads");
        Assert.True(Directory.Exists(uploadsDir));
    }

    [Fact]
    public async Task StartLogAsync_SavesLogFile()
    {
        // Act
        var entry = await _logManager.StartLogAsync("prod", "1.0.0", "default");

        // Assert
        var uploadsDir = Path.Combine(_tempDir, ".patchsync", "uploads");
        var files = Directory.GetFiles(uploadsDir, "*.json");
        Assert.Single(files);
    }

    #endregion

    #region UpdateLogAsync Tests

    [Fact]
    public async Task UpdateLogAsync_UpdatesExistingEntry()
    {
        // Arrange
        var entry = await _logManager.StartLogAsync("prod", "1.0.0", "default");
        entry.TotalFiles = 10;
        entry.UploadedFiles = 5;
        entry.TotalBytes = 1000;
        entry.UploadedBytes = 500;

        // Act
        await _logManager.UpdateLogAsync(entry);

        // Assert - Verify by retrieving logs
        var logs = await _logManager.GetLogsAsync();
        Assert.Single(logs);
        Assert.Equal(10, logs[0].TotalFiles);
        Assert.Equal(5, logs[0].UploadedFiles);
    }

    #endregion

    #region CompleteLogAsync Tests

    [Fact]
    public async Task CompleteLogAsync_SetsCompletedStatus_OnSuccess()
    {
        // Arrange
        var entry = await _logManager.StartLogAsync("prod", "1.0.0", "default");
        entry.TotalFiles = 5;
        entry.UploadedFiles = 5;

        // Act
        await _logManager.CompleteLogAsync(entry, success: true);

        // Assert
        Assert.Equal(UploadStatus.Completed, entry.Status);
        Assert.NotNull(entry.CompletedAt);
        Assert.True(entry.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task CompleteLogAsync_SetsPartialSuccessStatus_OnFailedFiles()
    {
        // Arrange
        var entry = await _logManager.StartLogAsync("prod", "1.0.0", "default");
        entry.TotalFiles = 5;
        entry.UploadedFiles = 3;
        entry.FailedFiles = 2;

        // Act
        await _logManager.CompleteLogAsync(entry, success: true);

        // Assert
        Assert.Equal(UploadStatus.PartialSuccess, entry.Status);
    }

    [Fact]
    public async Task CompleteLogAsync_SetsFailedStatus_OnFailure()
    {
        // Arrange
        var entry = await _logManager.StartLogAsync("prod", "1.0.0", "default");

        // Act
        await _logManager.CompleteLogAsync(entry, success: false, "Connection timed out");

        // Assert
        Assert.Equal(UploadStatus.Failed, entry.Status);
        Assert.Equal("Connection timed out", entry.ErrorMessage);
    }

    [Fact]
    public async Task CompleteLogAsync_CalculatesDuration()
    {
        // Arrange
        var entry = await _logManager.StartLogAsync("prod", "1.0.0", "default");
        await Task.Delay(50); // Small delay to ensure measurable duration

        // Act
        await _logManager.CompleteLogAsync(entry, success: true);

        // Assert
        Assert.True(entry.Duration.TotalMilliseconds >= 50);
    }

    #endregion

    #region GetLogAsync Tests

    [Fact]
    public async Task GetLogAsync_ReturnsSpecificLog()
    {
        // Arrange
        var entry = await _logManager.StartLogAsync("prod", "1.0.0", "default");
        var id = entry.Id;

        // Act
        var retrieved = await _logManager.GetLogAsync(id);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal(id, retrieved.Id);
        Assert.Equal("prod", retrieved.Channel);
        Assert.Equal("1.0.0", retrieved.Version);
    }

    [Fact]
    public async Task GetLogAsync_ReturnsNull_ForNonexistentId()
    {
        // Act
        var result = await _logManager.GetLogAsync("nonexistent123");

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region GetLogsAsync Tests

    [Fact]
    public async Task GetLogsAsync_ReturnsAllLogs()
    {
        // Arrange
        await _logManager.StartLogAsync("prod", "1.0.0", "default");
        await _logManager.StartLogAsync("prod", "1.0.1", "default");
        await _logManager.StartLogAsync("beta", "2.0.0", "default");

        // Act
        var logs = await _logManager.GetLogsAsync();

        // Assert
        Assert.Equal(3, logs.Count);
    }

    [Fact]
    public async Task GetLogsAsync_FiltersByChannel()
    {
        // Arrange
        await _logManager.StartLogAsync("prod", "1.0.0", "default");
        await _logManager.StartLogAsync("prod", "1.0.1", "default");
        await _logManager.StartLogAsync("beta", "2.0.0", "default");

        // Act
        var logs = await _logManager.GetLogsAsync("prod");

        // Assert
        Assert.Equal(2, logs.Count);
        Assert.All(logs, l => Assert.Equal("prod", l.Channel));
    }

    [Fact]
    public async Task GetLogsAsync_RespectsLimit()
    {
        // Arrange
        for (int i = 0; i < 10; i++)
        {
            await _logManager.StartLogAsync("prod", $"1.0.{i}", "default");
        }

        // Act
        var logs = await _logManager.GetLogsAsync(limit: 5);

        // Assert
        Assert.Equal(5, logs.Count);
    }

    [Fact]
    public async Task GetLogsAsync_ReturnsNewestFirst()
    {
        // Arrange
        await _logManager.StartLogAsync("prod", "1.0.0", "default");
        await Task.Delay(50);
        await _logManager.StartLogAsync("prod", "1.0.1", "default");

        // Act
        var logs = await _logManager.GetLogsAsync();

        // Assert
        Assert.Equal(2, logs.Count);
        // Newer log should be first based on filename ordering
        Assert.True(logs[0].StartedAt >= logs[1].StartedAt);
    }

    #endregion

    #region GetLastFailedAsync Tests

    [Fact]
    public async Task GetLastFailedAsync_ReturnsFailedUpload()
    {
        // Arrange
        var entry = await _logManager.StartLogAsync("prod", "1.0.0", "default");
        await _logManager.CompleteLogAsync(entry, success: false, "Error");

        // Act
        var lastFailed = await _logManager.GetLastFailedAsync("prod", "1.0.0");

        // Assert
        Assert.NotNull(lastFailed);
        Assert.Equal("1.0.0", lastFailed.Version);
        Assert.Equal(UploadStatus.Failed, lastFailed.Status);
    }

    [Fact]
    public async Task GetLastFailedAsync_ReturnsPartialSuccess()
    {
        // Arrange
        var entry = await _logManager.StartLogAsync("prod", "1.0.0", "default");
        entry.FailedFiles = 1;
        await _logManager.CompleteLogAsync(entry, success: true);

        // Act
        var lastFailed = await _logManager.GetLastFailedAsync("prod", "1.0.0");

        // Assert
        Assert.NotNull(lastFailed);
        Assert.Equal(UploadStatus.PartialSuccess, lastFailed.Status);
    }

    [Fact]
    public async Task GetLastFailedAsync_ReturnsNull_WhenNoFailures()
    {
        // Arrange
        var entry = await _logManager.StartLogAsync("prod", "1.0.0", "default");
        await _logManager.CompleteLogAsync(entry, success: true);

        // Act
        var lastFailed = await _logManager.GetLastFailedAsync("prod", "1.0.0");

        // Assert
        Assert.Null(lastFailed);
    }

    [Fact]
    public async Task GetLastFailedAsync_FiltersCorrectly()
    {
        // Arrange - Failed upload for different version
        var entry1 = await _logManager.StartLogAsync("prod", "1.0.0", "default");
        await _logManager.CompleteLogAsync(entry1, success: false, "Error");

        // Act - Look for failures on different version
        var lastFailed = await _logManager.GetLastFailedAsync("prod", "2.0.0");

        // Assert
        Assert.Null(lastFailed);
    }

    #endregion

    #region GetFailedFiles Tests

    [Fact]
    public void GetFailedFiles_ReturnsOnlyFailedFiles()
    {
        // Arrange
        var entry = new UploadLogEntry
        {
            Id = "test123",
            Channel = "prod",
            Version = "1.0.0",
            Profile = "default",
            Files =
            [
                new UploadedFileLog { LocalPath = "file1.txt", RemoteKey = "key1", Success = true },
                new UploadedFileLog { LocalPath = "file2.txt", RemoteKey = "key2", Success = false, Error = "Timeout" },
                new UploadedFileLog { LocalPath = "file3.txt", RemoteKey = "key3", Success = true },
                new UploadedFileLog { LocalPath = "file4.txt", RemoteKey = "key4", Success = false, Error = "Access denied" }
            ]
        };

        // Act
        var failedFiles = _logManager.GetFailedFiles(entry);

        // Assert
        Assert.Equal(2, failedFiles.Count);
        Assert.Contains(failedFiles, f => f.LocalPath == "file2.txt");
        Assert.Contains(failedFiles, f => f.LocalPath == "file4.txt");
    }

    #endregion

    #region CleanupOldLogs Tests

    [Fact]
    public async Task CleanupOldLogs_KeepsRecentLogs()
    {
        // Arrange
        for (int i = 0; i < 5; i++)
        {
            await _logManager.StartLogAsync("prod", $"1.0.{i}", "default");
            await Task.Delay(10); // Ensure different timestamps
        }

        // Act
        _logManager.CleanupOldLogs(keepCount: 3);

        // Assert
        var uploadsDir = Path.Combine(_tempDir, ".patchsync", "uploads");
        var files = Directory.GetFiles(uploadsDir, "*.json");
        Assert.Equal(3, files.Length);
    }

    [Fact]
    public async Task CleanupOldLogs_DoesNothingWhenBelowLimit()
    {
        // Arrange
        await _logManager.StartLogAsync("prod", "1.0.0", "default");
        await _logManager.StartLogAsync("prod", "1.0.1", "default");

        // Act
        _logManager.CleanupOldLogs(keepCount: 100);

        // Assert
        var uploadsDir = Path.Combine(_tempDir, ".patchsync", "uploads");
        var files = Directory.GetFiles(uploadsDir, "*.json");
        Assert.Equal(2, files.Length);
    }

    #endregion

    #region FileLog Details Tests

    [Fact]
    public async Task UploadLogEntry_TracksFileDetails()
    {
        // Arrange
        var entry = await _logManager.StartLogAsync("prod", "1.0.0", "default");

        entry.Files.Add(new UploadedFileLog
        {
            LocalPath = "manifest.json",
            RemoteKey = "prod/1.0.0/manifest.json",
            Size = 1024,
            UploadedAt = DateTime.UtcNow,
            ETag = "\"abc123\"",
            Success = true,
            BytesPerSecond = 500_000
        });

        entry.TotalFiles = 1;
        entry.UploadedFiles = 1;
        entry.TotalBytes = 1024;
        entry.UploadedBytes = 1024;

        await _logManager.UpdateLogAsync(entry);

        // Act
        var retrieved = await _logManager.GetLogAsync(entry.Id);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Single(retrieved.Files);
        var file = retrieved.Files[0];
        Assert.Equal("manifest.json", file.LocalPath);
        Assert.Equal(1024, file.Size);
        Assert.True(file.Success);
        Assert.Equal(500_000, file.BytesPerSecond);
    }

    #endregion
}
