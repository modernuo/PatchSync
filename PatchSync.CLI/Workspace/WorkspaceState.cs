namespace PatchSync.CLI.Workspace;

/// <summary>
/// Runtime workspace state stored in .patchsync/state.json
/// </summary>
public sealed class WorkspaceState
{
    /// <summary>JSON schema version for forward compatibility</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>When this state was last updated</summary>
    public DateTime LastUpdated { get; set; }

    /// <summary>Builds currently in progress</summary>
    public List<ActiveBuildInfo> ActiveBuilds { get; set; } = new();

    /// <summary>Versions staged and ready for publish</summary>
    public List<PendingPublishInfo> PendingPublish { get; set; } = new();

    /// <summary>Recent operations for quick reference</summary>
    public List<RecentOperationInfo> RecentOperations { get; set; } = new();
}

/// <summary>
/// Information about an active build
/// </summary>
public sealed class ActiveBuildInfo
{
    /// <summary>Channel being built</summary>
    public required string Channel { get; set; }

    /// <summary>Version being built</summary>
    public required string Version { get; set; }

    /// <summary>When the build started</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>Machine performing the build</summary>
    public string? MachineName { get; set; }

    /// <summary>Process ID of the build</summary>
    public int ProcessId { get; set; }
}

/// <summary>
/// Information about a version pending publish
/// </summary>
public sealed class PendingPublishInfo
{
    /// <summary>Channel of the staged version</summary>
    public required string Channel { get; set; }

    /// <summary>Version string</summary>
    public required string Version { get; set; }

    /// <summary>When the version was staged</summary>
    public DateTime StagedAt { get; set; }
}

/// <summary>
/// Information about a recent operation
/// </summary>
public sealed class RecentOperationInfo
{
    /// <summary>Operation type (build, publish, promote, rollback, clean)</summary>
    public required string Type { get; set; }

    /// <summary>Channel involved</summary>
    public required string Channel { get; set; }

    /// <summary>Version involved</summary>
    public required string Version { get; set; }

    /// <summary>When the operation occurred</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Operation result (success, failed, cancelled)</summary>
    public required string Status { get; set; }

    /// <summary>Optional error message if failed</summary>
    public string? ErrorMessage { get; set; }
}
