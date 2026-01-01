namespace PatchSync.CLI.Workspace;

/// <summary>
/// Status of an upload operation.
/// </summary>
public enum UploadStatus
{
    /// <summary>Upload in progress</summary>
    InProgress,

    /// <summary>Upload completed successfully</summary>
    Completed,

    /// <summary>Upload failed</summary>
    Failed,

    /// <summary>Upload was cancelled by user</summary>
    Cancelled,

    /// <summary>Some files uploaded, some failed</summary>
    PartialSuccess
}

/// <summary>
/// Log entry for tracking publish/upload operations.
/// Stored in .patchsync/uploads/ directory.
/// </summary>
public sealed class UploadLogEntry
{
    /// <summary>Unique identifier for this upload operation</summary>
    public required string Id { get; init; }

    /// <summary>Channel being published to</summary>
    public required string Channel { get; set; }

    /// <summary>Version being published</summary>
    public required string Version { get; set; }

    /// <summary>Name of the publish profile used</summary>
    public required string Profile { get; set; }

    /// <summary>When the upload started</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>When the upload completed (null if still in progress)</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Current status of the upload</summary>
    public UploadStatus Status { get; set; }

    /// <summary>Total bytes to upload</summary>
    public long TotalBytes { get; set; }

    /// <summary>Bytes uploaded so far</summary>
    public long UploadedBytes { get; set; }

    /// <summary>Total number of files to upload</summary>
    public int TotalFiles { get; set; }

    /// <summary>Number of files uploaded successfully</summary>
    public int UploadedFiles { get; set; }

    /// <summary>Number of files that failed to upload</summary>
    public int FailedFiles { get; set; }

    /// <summary>Duration of the upload operation</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>List of files with upload details</summary>
    public List<UploadedFileLog> Files { get; set; } = [];

    /// <summary>Error message if the upload failed</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Number of retry attempts</summary>
    public int RetryCount { get; set; }

    /// <summary>Remote path prefix used for the upload</summary>
    public string? RemotePath { get; set; }

    /// <summary>Public URL to the uploaded manifest</summary>
    public string? ManifestUrl { get; set; }
}

/// <summary>
/// Log entry for a single uploaded file.
/// </summary>
public sealed class UploadedFileLog
{
    /// <summary>Local path relative to version directory</summary>
    public required string LocalPath { get; init; }

    /// <summary>Remote S3 key</summary>
    public required string RemoteKey { get; init; }

    /// <summary>File size in bytes</summary>
    public long Size { get; set; }

    /// <summary>When the file was uploaded</summary>
    public DateTime? UploadedAt { get; set; }

    /// <summary>S3 ETag for verification</summary>
    public string? ETag { get; set; }

    /// <summary>Whether the upload was successful</summary>
    public bool Success { get; set; }

    /// <summary>Error message if the upload failed</summary>
    public string? Error { get; set; }

    /// <summary>Upload speed in bytes per second</summary>
    public long BytesPerSecond { get; set; }
}
