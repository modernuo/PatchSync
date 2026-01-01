using System.Text.Json;

namespace PatchSync.CLI.Workspace;

/// <summary>
/// Manages upload log entries for tracking publish operations.
/// Logs are stored in .patchsync/uploads/ directory.
/// </summary>
public sealed class UploadLogManager
{
    private readonly string _uploadsDir;

    public UploadLogManager(string workspacePath)
    {
        _uploadsDir = Path.Combine(workspacePath, ".patchsync", "uploads");
    }

    /// <summary>
    /// Starts a new upload log entry.
    /// </summary>
    public async Task<UploadLogEntry> StartLogAsync(
        string channel,
        string version,
        string profile,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_uploadsDir);

        var entry = new UploadLogEntry
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Channel = channel,
            Version = version,
            Profile = profile,
            StartedAt = DateTime.UtcNow,
            Status = UploadStatus.InProgress
        };

        await SaveLogAsync(entry, cancellationToken);
        return entry;
    }

    /// <summary>
    /// Updates an existing upload log entry.
    /// </summary>
    public async Task UpdateLogAsync(UploadLogEntry entry, CancellationToken cancellationToken = default)
    {
        await SaveLogAsync(entry, cancellationToken);
    }

    /// <summary>
    /// Marks an upload as completed.
    /// </summary>
    public async Task CompleteLogAsync(
        UploadLogEntry entry,
        bool success,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        entry.CompletedAt = DateTime.UtcNow;
        entry.Duration = entry.CompletedAt.Value - entry.StartedAt;

        if (success)
        {
            entry.Status = entry.FailedFiles > 0 ? UploadStatus.PartialSuccess : UploadStatus.Completed;
        }
        else
        {
            entry.Status = UploadStatus.Failed;
            entry.ErrorMessage = errorMessage;
        }

        await SaveLogAsync(entry, cancellationToken);
    }

    /// <summary>
    /// Gets a specific upload log entry by ID.
    /// </summary>
    public async Task<UploadLogEntry?> GetLogAsync(string id, CancellationToken cancellationToken = default)
    {
        var files = GetLogFiles();
        foreach (var file in files)
        {
            try
            {
                var content = await File.ReadAllTextAsync(file, cancellationToken);
                var entry = JsonSerializer.Deserialize(content, UploadLogJsonContext.Default.UploadLogEntry);
                if (entry?.Id == id)
                    return entry;
            }
            catch
            {
                // Skip malformed files
            }
        }
        return null;
    }

    /// <summary>
    /// Gets recent upload logs, optionally filtered by channel.
    /// </summary>
    public async Task<List<UploadLogEntry>> GetLogsAsync(
        string? channel = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var results = new List<UploadLogEntry>();
        var files = GetLogFiles().Take(limit * 2); // Get more files in case some are filtered

        foreach (var file in files)
        {
            if (results.Count >= limit)
                break;

            try
            {
                var content = await File.ReadAllTextAsync(file, cancellationToken);
                var entry = JsonSerializer.Deserialize(content, UploadLogJsonContext.Default.UploadLogEntry);

                if (entry != null && (channel == null || entry.Channel == channel))
                    results.Add(entry);
            }
            catch
            {
                // Skip malformed files
            }
        }

        return results;
    }

    /// <summary>
    /// Gets the last failed upload for a specific channel/version combination.
    /// </summary>
    public async Task<UploadLogEntry?> GetLastFailedAsync(
        string channel,
        string version,
        CancellationToken cancellationToken = default)
    {
        var logs = await GetLogsAsync(channel, 20, cancellationToken);
        return logs.FirstOrDefault(l =>
            l.Version == version &&
            (l.Status == UploadStatus.Failed || l.Status == UploadStatus.PartialSuccess));
    }

    /// <summary>
    /// Gets files that failed in a previous upload attempt.
    /// </summary>
    public List<UploadedFileLog> GetFailedFiles(UploadLogEntry entry)
    {
        return entry.Files.Where(f => !f.Success).ToList();
    }

    /// <summary>
    /// Cleans up old upload logs, keeping only the most recent entries.
    /// </summary>
    public void CleanupOldLogs(int keepCount = 100)
    {
        var files = GetLogFiles().Skip(keepCount).ToList();
        foreach (var file in files)
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // Ignore deletion errors
            }
        }
    }

    private async Task SaveLogAsync(UploadLogEntry entry, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_uploadsDir);

        var fileName = $"{entry.StartedAt:yyyyMMdd-HHmmss}-{entry.Channel}-{entry.Version}.json";
        var filePath = Path.Combine(_uploadsDir, fileName);

        var json = JsonSerializer.Serialize(entry, UploadLogJsonContext.Default.UploadLogEntry);
        await File.WriteAllTextAsync(filePath, json, cancellationToken);
    }

    private IEnumerable<string> GetLogFiles()
    {
        if (!Directory.Exists(_uploadsDir))
            return [];

        return Directory.GetFiles(_uploadsDir, "*.json")
            .OrderByDescending(f => f); // Newest first (files are named with timestamp)
    }
}
