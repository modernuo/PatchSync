using System.Text.Json;

namespace PatchSync.CLI.Workspace;

/// <summary>
/// Manages build locks to prevent concurrent builds of the same version.
/// Uses atomic file creation to ensure only one process can hold the lock.
/// </summary>
public sealed class BuildLock : IDisposable
{
    private const string LockFileName = ".build.lock";
    private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromHours(4);

    private readonly string _lockPath;
    private readonly FileStream _lockStream;
    private bool _disposed;

    /// <summary>
    /// Information about the lock holder
    /// </summary>
    public LockInfo Info { get; }

    private BuildLock(string lockPath, FileStream lockStream, LockInfo info)
    {
        _lockPath = lockPath;
        _lockStream = lockStream;
        Info = info;
    }

    /// <summary>
    /// Attempts to acquire a build lock for the specified version path.
    /// Returns null if the lock is held by another process.
    /// </summary>
    /// <param name="versionPath">Path to the version directory</param>
    /// <param name="timeout">Lock timeout for detecting stale locks</param>
    /// <returns>BuildLock if acquired, null if lock is held</returns>
    public static BuildLock? TryAcquire(string versionPath, TimeSpan? timeout = null)
    {
        timeout ??= DefaultLockTimeout;
        var lockPath = Path.Combine(versionPath, LockFileName);

        // Ensure the directory exists
        Directory.CreateDirectory(versionPath);

        // Check for existing stale lock
        if (File.Exists(lockPath))
        {
            var existingLock = TryReadLockInfo(lockPath);
            if (existingLock != null)
            {
                // Check if lock is stale (older than timeout)
                if (DateTime.UtcNow - existingLock.LockedAt > timeout.Value)
                {
                    // Try to remove stale lock
                    try
                    {
                        File.Delete(lockPath);
                    }
                    catch
                    {
                        // Another process may have taken it, that's fine
                        return null;
                    }
                }
                else
                {
                    // Lock is valid and held by another process
                    return null;
                }
            }
        }

        // Try to create lock file atomically
        try
        {
            var stream = new FileStream(lockPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                4096,
                FileOptions.DeleteOnClose);

            var info = new LockInfo
            {
                MachineName = Environment.MachineName,
                LockedAt = DateTime.UtcNow,
                ProcessId = Environment.ProcessId,
                UserName = Environment.UserName
            };

            // Write lock info
            var json = JsonSerializer.Serialize(info, BuildLockJsonContext.Default.LockInfo);
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            stream.Write(bytes);
            stream.Flush();

            return new BuildLock(lockPath, stream, info);
        }
        catch (IOException)
        {
            // File already exists or cannot be created - lock is held
            return null;
        }
    }

    /// <summary>
    /// Gets information about who holds the lock, if any.
    /// </summary>
    public static LockInfo? GetLockHolder(string versionPath)
    {
        var lockPath = Path.Combine(versionPath, LockFileName);
        return TryReadLockInfo(lockPath);
    }

    /// <summary>
    /// Checks if a lock is held for the specified version path.
    /// </summary>
    public static bool IsLocked(string versionPath, TimeSpan? timeout = null)
    {
        timeout ??= DefaultLockTimeout;
        var lockPath = Path.Combine(versionPath, LockFileName);

        if (!File.Exists(lockPath))
            return false;

        var info = TryReadLockInfo(lockPath);
        if (info == null)
            return false;

        // Check if lock is stale
        return DateTime.UtcNow - info.LockedAt <= timeout.Value;
    }

    /// <summary>
    /// Forces removal of a lock (use with caution - only for recovery).
    /// </summary>
    public static bool ForceRemove(string versionPath)
    {
        var lockPath = Path.Combine(versionPath, LockFileName);
        try
        {
            if (File.Exists(lockPath))
            {
                File.Delete(lockPath);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static LockInfo? TryReadLockInfo(string lockPath)
    {
        try
        {
            if (!File.Exists(lockPath))
                return null;

            var json = File.ReadAllText(lockPath);
            return JsonSerializer.Deserialize(json, BuildLockJsonContext.Default.LockInfo);
        }
        catch
        {
            // Lock file may be being written or is corrupted
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            _lockStream.Dispose();
        }
        catch
        {
            // Ignore disposal errors
        }

        // FileOptions.DeleteOnClose should remove the file,
        // but ensure it's gone as a fallback
        try
        {
            if (File.Exists(_lockPath))
                File.Delete(_lockPath);
        }
        catch
        {
            // Best effort
        }
    }
}

/// <summary>
/// Information about a build lock
/// </summary>
public sealed class LockInfo
{
    /// <summary>Name of the machine holding the lock</summary>
    public required string MachineName { get; set; }

    /// <summary>When the lock was acquired</summary>
    public DateTime LockedAt { get; set; }

    /// <summary>Process ID of the lock holder</summary>
    public int ProcessId { get; set; }

    /// <summary>Username of the lock holder</summary>
    public string? UserName { get; set; }
}

/// <summary>
/// JSON context for lock info serialization
/// </summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(LockInfo))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
internal partial class BuildLockJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
