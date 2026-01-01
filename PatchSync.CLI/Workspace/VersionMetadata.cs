namespace PatchSync.CLI.Workspace;

/// <summary>
/// Version lifecycle status
/// </summary>
public enum VersionStatus
{
    /// <summary>Build in progress (lock file exists)</summary>
    Building,

    /// <summary>Built successfully, not yet published</summary>
    Staged,

    /// <summary>Upload to CDN in progress</summary>
    Publishing,

    /// <summary>Current live version for this channel</summary>
    Live,

    /// <summary>Was live, now replaced by newer version</summary>
    Superseded,

    /// <summary>Removed from CDN, metadata retained locally</summary>
    Archived,

    /// <summary>Build or publish failed</summary>
    Failed
}

/// <summary>
/// Metadata for a built version stored in version.json
/// </summary>
public sealed class VersionMetadata
{
    /// <summary>JSON schema version for forward compatibility</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Version string (e.g., "1.0.0", "1.1.0-beta.1")</summary>
    public required string Version { get; set; }

    /// <summary>Channel this version belongs to</summary>
    public required string Channel { get; set; }

    /// <summary>Build information</summary>
    public required BuildInfo Build { get; set; }

    /// <summary>Input source information</summary>
    public required InputInfo Input { get; set; }

    /// <summary>Build output information</summary>
    public required OutputInfo Output { get; set; }

    /// <summary>Chunking settings used for this build</summary>
    public required ChunkingInfo Chunking { get; set; }

    /// <summary>Current version status</summary>
    public VersionStatus Status { get; set; } = VersionStatus.Staged;

    /// <summary>Publish state and progress</summary>
    public PublishInfo? Publish { get; set; }

    /// <summary>Comparison with previous version</summary>
    public ComparisonInfo? Comparison { get; set; }

    /// <summary>Optional release notes</summary>
    public string? Notes { get; set; }

    /// <summary>Optional tags for categorization</summary>
    public List<string>? Tags { get; set; }
}

/// <summary>
/// Information about when and where the build was created
/// </summary>
public sealed class BuildInfo
{
    /// <summary>When the build started</summary>
    public DateTime BuiltAt { get; set; }

    /// <summary>Machine name that performed the build</summary>
    public string? BuiltBy { get; set; }

    /// <summary>CI build number if available</summary>
    public string? BuildNumber { get; set; }

    /// <summary>Build duration in seconds</summary>
    public double Duration { get; set; }

    /// <summary>PatchSync version used for the build</summary>
    public string? PatchSyncVersion { get; set; }
}

/// <summary>
/// Information about the input source files
/// </summary>
public sealed class InputInfo
{
    /// <summary>Path to the input directory</summary>
    public required string Path { get; set; }

    /// <summary>Hash of input directory contents for change detection</summary>
    public string? Hash { get; set; }

    /// <summary>Number of files processed</summary>
    public int FileCount { get; set; }

    /// <summary>Total size of input files in bytes</summary>
    public long TotalSize { get; set; }
}

/// <summary>
/// Information about the build outputs
/// </summary>
public sealed class OutputInfo
{
    /// <summary>Hash of the manifest file</summary>
    public string? ManifestHash { get; set; }

    /// <summary>Number of signature files generated</summary>
    public int SignatureCount { get; set; }

    /// <summary>Total size of signature files in bytes</summary>
    public long SignatureTotalSize { get; set; }

    /// <summary>Whether compressed fallback files are available</summary>
    public bool CompressedAvailable { get; set; }

    /// <summary>Total size of compressed files in bytes</summary>
    public long CompressedTotalSize { get; set; }
}

/// <summary>
/// Chunking algorithm settings used for the build
/// </summary>
public sealed class ChunkingInfo
{
    /// <summary>Algorithm identifier</summary>
    public required string Algorithm { get; set; }

    /// <summary>Minimum chunk size in bytes</summary>
    public int MinChunkSize { get; set; }

    /// <summary>Average chunk size in bytes</summary>
    public int AvgChunkSize { get; set; }

    /// <summary>Maximum chunk size in bytes</summary>
    public int MaxChunkSize { get; set; }

    /// <summary>Total number of chunks generated</summary>
    public long TotalChunks { get; set; }
}

/// <summary>
/// Publish state and progress tracking
/// </summary>
public sealed class PublishInfo
{
    /// <summary>Name of the publish profile used</summary>
    public string? Profile { get; set; }

    /// <summary>When the publish completed (null if not published)</summary>
    public DateTime? PublishedAt { get; set; }

    /// <summary>Public URL to the manifest</summary>
    public string? ManifestUrl { get; set; }

    /// <summary>S3 path prefix for this version (e.g., "prod/versions/1.0.0")</summary>
    public string? RemotePath { get; set; }

    /// <summary>Files that have been uploaded with details for resume/verification</summary>
    public List<UploadedFile>? Files { get; set; }

    /// <summary>Total bytes that need to be uploaded</summary>
    public long TotalBytes { get; set; }

    /// <summary>Total bytes uploaded so far</summary>
    public long UploadedBytes { get; set; }

    /// <summary>When the last upload occurred (for resume)</summary>
    public DateTime? LastUploadAt { get; set; }

    /// <summary>ETag of channel.json after last publish (for cache invalidation tracking)</summary>
    public string? ChannelJsonETag { get; set; }
}

/// <summary>
/// Information about an uploaded file
/// </summary>
public sealed class UploadedFile
{
    /// <summary>Local path relative to version directory</summary>
    public required string LocalPath { get; set; }

    /// <summary>Remote S3 key</summary>
    public required string RemoteKey { get; set; }

    /// <summary>S3 ETag for verification</summary>
    public required string ETag { get; set; }

    /// <summary>File size in bytes</summary>
    public long Size { get; set; }
}

/// <summary>
/// Comparison with a previous version
/// </summary>
public sealed class ComparisonInfo
{
    /// <summary>Version compared against</summary>
    public string? PreviousVersion { get; set; }

    /// <summary>Number of new files</summary>
    public int NewFiles { get; set; }

    /// <summary>Number of modified files</summary>
    public int ModifiedFiles { get; set; }

    /// <summary>Number of deleted files</summary>
    public int DeletedFiles { get; set; }

    /// <summary>Number of unchanged files</summary>
    public int UnchangedFiles { get; set; }

    /// <summary>Estimated bytes a client would need to download for delta update</summary>
    public long EstimatedDeltaDownload { get; set; }
}
