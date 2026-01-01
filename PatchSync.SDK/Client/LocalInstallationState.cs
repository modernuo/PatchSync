using System.Text.Json;
using System.Text.Json.Serialization;

namespace PatchSync.SDK.Client;

/// <summary>
/// Tracks the original state of files for supporting the UpdateIfNotModified strategy.
/// Stores the original hash at install/update time so we can detect local modifications.
/// </summary>
public sealed class LocalInstallationState
{
    /// <summary>
    /// File path to the state file (relative to installation root).
    /// </summary>
    public const string DefaultFileName = ".patchsync-state.json";

    /// <summary>
    /// Version of the manifest that was last applied.
    /// </summary>
    public string? LastAppliedVersion { get; set; }

    /// <summary>
    /// Timestamp of last successful update.
    /// </summary>
    public DateTime? LastUpdateTime { get; set; }

    /// <summary>
    /// Original hashes of files at install/update time.
    /// Key is relative file path, value is the original SHA256 hash.
    /// </summary>
    public Dictionary<string, string> OriginalHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Load state from a file.
    /// </summary>
    public static async Task<LocalInstallationState> LoadAsync(string stateFilePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(stateFilePath))
        {
            return new LocalInstallationState();
        }

        try
        {
            await using var stream = File.OpenRead(stateFilePath);
            var state = await JsonSerializer.DeserializeAsync(
                stream,
                LocalInstallationStateContext.Default.LocalInstallationState,
                cancellationToken);

            return state ?? new LocalInstallationState();
        }
        catch (JsonException)
        {
            // Corrupted file - start fresh
            return new LocalInstallationState();
        }
    }

    /// <summary>
    /// Save state to a file.
    /// </summary>
    public async Task SaveAsync(string stateFilePath, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(stateFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(stateFilePath);
        await JsonSerializer.SerializeAsync(
            stream,
            this,
            LocalInstallationStateContext.Default.LocalInstallationState,
            cancellationToken);
    }

    /// <summary>
    /// Check if a file has been modified locally since installation.
    /// </summary>
    /// <param name="relativePath">Relative path of the file.</param>
    /// <param name="currentHash">Current hash of the file on disk.</param>
    /// <returns>True if the file has been modified, false if unchanged or unknown.</returns>
    public bool IsFileModified(string relativePath, string currentHash)
    {
        if (!OriginalHashes.TryGetValue(relativePath, out var originalHash))
        {
            // File was not tracked - treat as not modified (will be updated)
            return false;
        }

        return !string.Equals(originalHash, currentHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Record the original hash for a file after installation/update.
    /// </summary>
    public void RecordFileHash(string relativePath, string hash)
    {
        OriginalHashes[relativePath] = hash;
    }

    /// <summary>
    /// Remove a file from tracking (e.g., when deleted).
    /// </summary>
    public void RemoveFile(string relativePath)
    {
        OriginalHashes.Remove(relativePath);
    }

    /// <summary>
    /// Update the version info after a successful update.
    /// </summary>
    public void RecordUpdate(string version)
    {
        LastAppliedVersion = version;
        LastUpdateTime = DateTime.UtcNow;
    }

    /// <summary>
    /// Clear all tracked hashes (for fresh install).
    /// </summary>
    public void Clear()
    {
        OriginalHashes.Clear();
        LastAppliedVersion = null;
        LastUpdateTime = null;
    }
}

/// <summary>
/// JSON serialization context for LocalInstallationState (AOT-compatible).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(LocalInstallationState))]
internal partial class LocalInstallationStateContext : JsonSerializerContext
{
}
