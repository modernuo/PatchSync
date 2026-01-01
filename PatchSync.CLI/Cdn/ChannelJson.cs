using System.Text.Json.Serialization;

namespace PatchSync.CLI.Cdn;

/// <summary>
/// Channel discovery file for CDN. Hosted at the channel root.
/// This file should have a short cache TTL or no-cache as it changes on publish.
/// </summary>
public sealed class ChannelJson
{
    /// <summary>Schema version for forward compatibility</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Channel identifier (e.g., "prod", "beta")</summary>
    [JsonPropertyName("channelId")]
    public required string ChannelId { get; set; }

    /// <summary>Current live version details</summary>
    [JsonPropertyName("current")]
    public required ChannelVersionInfo Current { get; set; }

    /// <summary>All available versions (most recent first)</summary>
    [JsonPropertyName("available")]
    public List<ChannelVersionInfo> Available { get; set; } = [];

    /// <summary>When this file was last updated</summary>
    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Version information within a channel
/// </summary>
public sealed class ChannelVersionInfo
{
    /// <summary>Version string (e.g., "7.0.85.15")</summary>
    [JsonPropertyName("version")]
    public required string Version { get; set; }

    /// <summary>Relative URL to manifest.json from channel root</summary>
    [JsonPropertyName("manifestUrl")]
    public required string ManifestUrl { get; set; }

    /// <summary>When this version was published</summary>
    [JsonPropertyName("publishedAt")]
    public DateTime? PublishedAt { get; set; }

    /// <summary>Total size of all files in bytes</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>Number of files in the manifest</summary>
    [JsonPropertyName("fileCount")]
    public int FileCount { get; set; }
}
