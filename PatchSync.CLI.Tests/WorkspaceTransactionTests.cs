using PatchSync.CLI.Workspace;

namespace PatchSync.CLI.Tests;

/// <summary>
/// Tests for WorkspaceTransaction atomic multi-file updates.
/// Uses temporary directories for isolation.
/// </summary>
public class WorkspaceTransactionTests : IDisposable
{
    private readonly string _tempDir;

    public WorkspaceTransactionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"patchsync_tx_test_{Guid.NewGuid():N}");
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
    public void Stage_IncrementsStagedCount()
    {
        // Arrange
        using var tx = new WorkspaceTransaction();
        var path = Path.Combine(_tempDir, "test.txt");

        // Act
        tx.Stage(path, "content");

        // Assert
        Assert.Equal(1, tx.StagedCount);
    }

    [Fact]
    public void Stage_CreatesTemporaryFile()
    {
        // Arrange
        using var tx = new WorkspaceTransaction();
        var path = Path.Combine(_tempDir, "test.txt");

        // Act
        tx.Stage(path, "content");

        // Assert - temp file should exist, but not the target
        Assert.False(File.Exists(path));
        var tempFiles = Directory.GetFiles(_tempDir, "*.tmp.*");
        Assert.Single(tempFiles);
    }

    [Fact]
    public void Commit_MovesFilesToFinalLocations()
    {
        // Arrange
        using var tx = new WorkspaceTransaction();
        var path1 = Path.Combine(_tempDir, "file1.txt");
        var path2 = Path.Combine(_tempDir, "file2.txt");

        tx.Stage(path1, "content1");
        tx.Stage(path2, "content2");

        // Act
        tx.Commit();

        // Assert
        Assert.True(File.Exists(path1));
        Assert.True(File.Exists(path2));
        Assert.Equal("content1", File.ReadAllText(path1));
        Assert.Equal("content2", File.ReadAllText(path2));

        // Temp files should be gone
        var tempFiles = Directory.GetFiles(_tempDir, "*.tmp.*");
        Assert.Empty(tempFiles);
    }

    [Fact]
    public void Commit_OverwritesExistingFiles()
    {
        // Arrange
        var path = Path.Combine(_tempDir, "existing.txt");
        File.WriteAllText(path, "old content");

        using var tx = new WorkspaceTransaction();
        tx.Stage(path, "new content");

        // Act
        tx.Commit();

        // Assert
        Assert.Equal("new content", File.ReadAllText(path));
    }

    [Fact]
    public void Rollback_DeletesTemporaryFiles()
    {
        // Arrange
        using var tx = new WorkspaceTransaction();
        var path = Path.Combine(_tempDir, "test.txt");
        tx.Stage(path, "content");

        // Act
        tx.Rollback();

        // Assert
        Assert.False(File.Exists(path));
        var tempFiles = Directory.GetFiles(_tempDir, "*.tmp.*");
        Assert.Empty(tempFiles);
        Assert.Equal(0, tx.StagedCount);
    }

    [Fact]
    public void Dispose_RollsBackUncommittedChanges()
    {
        // Arrange
        var path = Path.Combine(_tempDir, "test.txt");

        // Act
        using (var tx = new WorkspaceTransaction())
        {
            tx.Stage(path, "content");
        } // Dispose without commit

        // Assert
        Assert.False(File.Exists(path));
        var tempFiles = Directory.GetFiles(_tempDir, "*.tmp.*");
        Assert.Empty(tempFiles);
    }

    [Fact]
    public void Stage_CreatesNestedDirectories()
    {
        // Arrange
        using var tx = new WorkspaceTransaction();
        var path = Path.Combine(_tempDir, "sub1", "sub2", "test.txt");

        // Act
        tx.Stage(path, "content");
        tx.Commit();

        // Assert
        Assert.True(File.Exists(path));
        Assert.True(Directory.Exists(Path.Combine(_tempDir, "sub1", "sub2")));
    }

    [Fact]
    public void Rollback_RemovesEmptyCreatedDirectories()
    {
        // Arrange
        using var tx = new WorkspaceTransaction();
        var path = Path.Combine(_tempDir, "newdir", "test.txt");

        tx.Stage(path, "content");

        // Act
        tx.Rollback();

        // Assert
        Assert.False(Directory.Exists(Path.Combine(_tempDir, "newdir")));
    }

    [Fact]
    public void Stage_WithBytes_WritesToTempFile()
    {
        // Arrange
        using var tx = new WorkspaceTransaction();
        var path = Path.Combine(_tempDir, "binary.bin");
        var bytes = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        // Act
        tx.Stage(path, bytes);
        tx.Commit();

        // Assert
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task StageAsync_WritesTempFile()
    {
        // Arrange
        using var tx = new WorkspaceTransaction();
        var path = Path.Combine(_tempDir, "async.txt");

        // Act
        await tx.StageAsync(path, "async content");
        tx.Commit();

        // Assert
        Assert.Equal("async content", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void Commit_ThrowsAfterAlreadyCommitted()
    {
        // Arrange
        using var tx = new WorkspaceTransaction();
        var path = Path.Combine(_tempDir, "test.txt");
        tx.Stage(path, "content");
        tx.Commit();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => tx.Commit());
    }

    [Fact]
    public void Stage_ThrowsAfterCommitted()
    {
        // Arrange
        using var tx = new WorkspaceTransaction();
        var path1 = Path.Combine(_tempDir, "test1.txt");
        var path2 = Path.Combine(_tempDir, "test2.txt");
        tx.Stage(path1, "content");
        tx.Commit();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => tx.Stage(path2, "content"));
    }

    [Fact]
    public void Stage_ThrowsAfterDisposed()
    {
        // Arrange
        var tx = new WorkspaceTransaction();
        tx.Dispose();
        var path = Path.Combine(_tempDir, "test.txt");

        // Act & Assert
        Assert.Throws<ObjectDisposedException>(() => tx.Stage(path, "content"));
    }

    [Fact]
    public void Commit_ThrowsAfterDisposed()
    {
        // Arrange
        var tx = new WorkspaceTransaction();
        tx.Dispose();

        // Act & Assert
        Assert.Throws<ObjectDisposedException>(() => tx.Commit());
    }

    [Fact]
    public void Commit_ClearsStagedCount()
    {
        // Arrange
        using var tx = new WorkspaceTransaction();
        tx.Stage(Path.Combine(_tempDir, "test.txt"), "content");
        Assert.Equal(1, tx.StagedCount);

        // Act
        tx.Commit();

        // Assert
        Assert.Equal(0, tx.StagedCount);
    }

    [Fact]
    public void Rollback_AfterCommit_DoesNothing()
    {
        // Arrange
        using var tx = new WorkspaceTransaction();
        var path = Path.Combine(_tempDir, "test.txt");
        tx.Stage(path, "content");
        tx.Commit();

        // Act - should not throw or delete committed file
        tx.Rollback();

        // Assert
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        // Arrange
        var tx = new WorkspaceTransaction();
        tx.Stage(Path.Combine(_tempDir, "test.txt"), "content");

        // Act & Assert - should not throw
        tx.Dispose();
        tx.Dispose();
        tx.Dispose();
    }

    [Fact]
    public void MultipleFiles_CommittedAtomically()
    {
        // Arrange
        using var tx = new WorkspaceTransaction();
        var files = new Dictionary<string, string>
        {
            [Path.Combine(_tempDir, "config.json")] = "{\"key\": \"value\"}",
            [Path.Combine(_tempDir, "data", "file1.txt")] = "File 1 content",
            [Path.Combine(_tempDir, "data", "file2.txt")] = "File 2 content",
            [Path.Combine(_tempDir, "deep", "nested", "path", "file.txt")] = "Deep content"
        };

        foreach (var (path, content) in files)
        {
            tx.Stage(path, content);
        }

        Assert.Equal(4, tx.StagedCount);

        // Act
        tx.Commit();

        // Assert - all files exist with correct content
        foreach (var (path, expectedContent) in files)
        {
            Assert.True(File.Exists(path), $"File should exist: {path}");
            Assert.Equal(expectedContent, File.ReadAllText(path));
        }
    }

    [Fact]
    public void Rollback_DoesNotDeleteNonCreatedDirectories()
    {
        // Arrange - create a directory manually first
        var existingDir = Path.Combine(_tempDir, "existing");
        Directory.CreateDirectory(existingDir);

        using var tx = new WorkspaceTransaction();
        var path = Path.Combine(existingDir, "test.txt");
        tx.Stage(path, "content");

        // Act
        tx.Rollback();

        // Assert - existing directory should remain
        Assert.True(Directory.Exists(existingDir));
    }
}
