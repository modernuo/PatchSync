namespace PatchSync.CLI.Workspace;

/// <summary>
/// Manages atomic multi-file updates using a write-ahead pattern.
/// Writes all changes to temporary files first, then commits atomically.
/// </summary>
public sealed class WorkspaceTransaction : IDisposable
{
    private readonly List<StagedFile> _stagedFiles = new();
    private readonly List<string> _createdDirectories = new();
    private bool _committed;
    private bool _disposed;

    /// <summary>
    /// Number of files staged for commit
    /// </summary>
    public int StagedCount => _stagedFiles.Count;

    /// <summary>
    /// Stages a file write. The content is written to a temp file immediately.
    /// </summary>
    /// <param name="targetPath">Final path for the file</param>
    /// <param name="content">File content</param>
    public void Stage(string targetPath, string content)
    {
        if (_committed)
            throw new InvalidOperationException("Transaction already committed");
        if (_disposed)
            throw new ObjectDisposedException(nameof(WorkspaceTransaction));

        // Ensure directory exists
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            _createdDirectories.Add(directory);
        }

        // Write to temp file
        var tempPath = targetPath + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(tempPath, content);

        _stagedFiles.Add(new StagedFile(targetPath, tempPath));
    }

    /// <summary>
    /// Stages a file write asynchronously.
    /// </summary>
    public async Task StageAsync(string targetPath, string content, CancellationToken ct = default)
    {
        if (_committed)
            throw new InvalidOperationException("Transaction already committed");
        if (_disposed)
            throw new ObjectDisposedException(nameof(WorkspaceTransaction));

        // Ensure directory exists
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            _createdDirectories.Add(directory);
        }

        // Write to temp file
        var tempPath = targetPath + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        await File.WriteAllTextAsync(tempPath, content, ct);

        _stagedFiles.Add(new StagedFile(targetPath, tempPath));
    }

    /// <summary>
    /// Stages a file write with bytes.
    /// </summary>
    public void Stage(string targetPath, byte[] content)
    {
        if (_committed)
            throw new InvalidOperationException("Transaction already committed");
        if (_disposed)
            throw new ObjectDisposedException(nameof(WorkspaceTransaction));

        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            _createdDirectories.Add(directory);
        }

        var tempPath = targetPath + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllBytes(tempPath, content);

        _stagedFiles.Add(new StagedFile(targetPath, tempPath));
    }

    /// <summary>
    /// Commits all staged files atomically.
    /// After commit, no further staging is allowed.
    /// </summary>
    public void Commit()
    {
        if (_committed)
            throw new InvalidOperationException("Transaction already committed");
        if (_disposed)
            throw new ObjectDisposedException(nameof(WorkspaceTransaction));

        // Validate all temp files exist before committing
        foreach (var staged in _stagedFiles)
        {
            if (!File.Exists(staged.TempPath))
                throw new WorkspaceException($"Staged file missing: {staged.TempPath}");
        }

        // Move all temp files to final locations
        foreach (var staged in _stagedFiles)
        {
            File.Move(staged.TempPath, staged.TargetPath, overwrite: true);
        }

        _committed = true;
        _stagedFiles.Clear();
    }

    /// <summary>
    /// Rolls back all staged changes by deleting temp files.
    /// </summary>
    public void Rollback()
    {
        if (_committed)
            return; // Nothing to rollback

        foreach (var staged in _stagedFiles)
        {
            try
            {
                if (File.Exists(staged.TempPath))
                    File.Delete(staged.TempPath);
            }
            catch
            {
                // Best effort cleanup
            }
        }

        // Remove empty directories we created (in reverse order)
        for (int i = _createdDirectories.Count - 1; i >= 0; i--)
        {
            try
            {
                var dir = _createdDirectories[i];
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch
            {
                // Best effort cleanup
            }
        }

        _stagedFiles.Clear();
        _createdDirectories.Clear();
    }

    /// <summary>
    /// Disposes the transaction, rolling back any uncommitted changes.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (!_committed)
        {
            Rollback();
        }
    }

    private readonly record struct StagedFile(string TargetPath, string TempPath);
}
