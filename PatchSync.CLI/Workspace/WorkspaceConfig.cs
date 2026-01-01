namespace PatchSync.CLI.Workspace;

/// <summary>
/// Main workspace configuration stored in patchsync.workspace.json
/// </summary>
public sealed class WorkspaceConfig
{
    /// <summary>JSON schema version for forward compatibility</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Project identification and metadata</summary>
    public required ProjectInfo Project { get; set; }

    /// <summary>Default settings for builds</summary>
    public BuildDefaults? Defaults { get; set; }

    /// <summary>Channel configurations keyed by channel ID (e.g., "prod", "beta", "dev")</summary>
    public Dictionary<string, ChannelConfig> Channels { get; set; } = new();

    /// <summary>Publish profiles keyed by profile name</summary>
    public Dictionary<string, PublishProfile> PublishProfiles { get; set; } = new();
}

/// <summary>
/// Project identification and metadata
/// </summary>
public sealed class ProjectInfo
{
    /// <summary>Human-readable project name</summary>
    public required string Name { get; set; }

    /// <summary>Machine-readable project identifier (lowercase, no spaces)</summary>
    public required string Id { get; set; }

    /// <summary>Optional project description</summary>
    public string? Description { get; set; }
}

/// <summary>
/// Default settings applied to all builds unless overridden
/// </summary>
public sealed class BuildDefaults
{
    /// <summary>Default input directory for game files</summary>
    public string? InputPath { get; set; }

    /// <summary>Chunking algorithm settings</summary>
    public ChunkingDefaults? Chunking { get; set; }

    /// <summary>Compression settings for fallback files</summary>
    public CompressionDefaults? Compression { get; set; }
}

/// <summary>
/// Default chunking algorithm settings
/// </summary>
public sealed class ChunkingDefaults
{
    /// <summary>Algorithm identifier (e.g., "fastcdc-v1")</summary>
    public string Algorithm { get; set; } = "fastcdc-v1";

    /// <summary>Minimum chunk size in bytes</summary>
    public int MinChunkSize { get; set; } = 4096;

    /// <summary>Average/target chunk size in bytes</summary>
    public int AvgChunkSize { get; set; } = 16384;

    /// <summary>Maximum chunk size in bytes</summary>
    public int MaxChunkSize { get; set; } = 65536;

    /// <summary>Minimum file size to use delta updates (smaller files use full download)</summary>
    public int MinDeltaSize { get; set; } = 65536;
}

/// <summary>
/// Default compression settings for fallback files
/// </summary>
public sealed class CompressionDefaults
{
    /// <summary>Whether to generate compressed fallback files</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Compression algorithm (e.g., "zstd", "gzip")</summary>
    public string Algorithm { get; set; } = "zstd";

    /// <summary>Compression level (algorithm-specific)</summary>
    public int Level { get; set; } = 9;
}

/// <summary>
/// Configuration for a release channel (e.g., prod, beta, dev)
/// </summary>
public sealed class ChannelConfig
{
    /// <summary>Human-readable channel name</summary>
    public required string DisplayName { get; set; }

    /// <summary>Optional channel description</summary>
    public string? Description { get; set; }

    /// <summary>Whether this is the default channel for commands</summary>
    public bool IsDefault { get; set; }

    /// <summary>Regex pattern that version strings must match for this channel</summary>
    public string? VersionPattern { get; set; }

    /// <summary>Number of versions to retain (older versions are eligible for cleanup)</summary>
    public int RetainVersions { get; set; } = 10;

    /// <summary>Publish settings for this channel</summary>
    public ChannelPublishConfig? Publish { get; set; }
}

/// <summary>
/// Channel-specific publish configuration
/// </summary>
public sealed class ChannelPublishConfig
{
    /// <summary>Name of the publish profile to use</summary>
    public required string Profile { get; set; }

    /// <summary>Optional path prefix for this channel's files on CDN</summary>
    public string? Prefix { get; set; }
}

/// <summary>
/// Configuration for uploading to a storage provider
/// </summary>
public sealed class PublishProfile
{
    /// <summary>Storage type (e.g., "s3")</summary>
    public string Type { get; set; } = "s3";

    /// <summary>S3-compatible endpoint URL</summary>
    public required string Endpoint { get; set; }

    /// <summary>S3 bucket name</summary>
    public required string Bucket { get; set; }

    /// <summary>AWS region</summary>
    public string? Region { get; set; }

    /// <summary>Base path prefix within the bucket</summary>
    public string? Prefix { get; set; }

    /// <summary>Public URL for accessing uploaded files</summary>
    public string? PublicUrl { get; set; }

    /// <summary>How to obtain credentials: "environment", "stored", or "prompt"</summary>
    public string CredentialSource { get; set; } = "environment";

    /// <summary>Use path-style URLs for S3 requests</summary>
    public bool PathStyle { get; set; } = true;

    /// <summary>CDN configuration for cache management</summary>
    public CdnConfig? Cdn { get; set; }
}

/// <summary>
/// CDN configuration for cache purging
/// </summary>
public sealed class CdnConfig
{
    /// <summary>CDN provider: "cloudflare", "fastly", etc.</summary>
    public string? Provider { get; set; }

    /// <summary>Cloudflare Zone ID</summary>
    public string? ZoneId { get; set; }

    /// <summary>Whether to automatically purge channel.json on publish</summary>
    public bool AutoPurge { get; set; } = true;
}
