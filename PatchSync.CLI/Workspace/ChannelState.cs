namespace PatchSync.CLI.Workspace;

/// <summary>
/// Channel state stored in channels/{channel}/channel.json
/// </summary>
public sealed class ChannelState
{
    /// <summary>JSON schema version for forward compatibility</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Channel identifier</summary>
    public required string ChannelId { get; set; }

    /// <summary>Human-readable channel name</summary>
    public required string DisplayName { get; set; }

    /// <summary>Currently live version info</summary>
    public CurrentVersionInfo? Current { get; set; }

    /// <summary>Version history for this channel</summary>
    public List<VersionHistoryEntry> History { get; set; } = new();

    /// <summary>Versions available for rollback (still on CDN)</summary>
    public List<string> RollbackAvailable { get; set; } = new();
}

/// <summary>
/// Information about the currently live version
/// </summary>
public sealed class CurrentVersionInfo
{
    /// <summary>Version string</summary>
    public required string Version { get; set; }

    /// <summary>When this version was published</summary>
    public DateTime PublishedAt { get; set; }

    /// <summary>Public URL to the manifest</summary>
    public string? ManifestUrl { get; set; }
}

/// <summary>
/// Entry in the channel's version history
/// </summary>
public sealed class VersionHistoryEntry
{
    /// <summary>Version string</summary>
    public required string Version { get; set; }

    /// <summary>Current status of this version</summary>
    public VersionStatus Status { get; set; }

    /// <summary>When the version was built</summary>
    public DateTime BuiltAt { get; set; }

    /// <summary>When the version was published (if applicable)</summary>
    public DateTime? PublishedAt { get; set; }

    /// <summary>Number of files in this version</summary>
    public int FileCount { get; set; }

    /// <summary>Total size in bytes</summary>
    public long TotalSize { get; set; }
}
