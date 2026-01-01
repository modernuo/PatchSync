using System.Text.Json;
using System.Text.Json.Serialization;
using PatchSync.CLI.Cdn;

namespace PatchSync.CLI.Workspace;

/// <summary>
/// JSON serialization context for workspace types.
/// Required for NativeAOT compatibility - no reflection-based serialization.
/// </summary>
[JsonSerializable(typeof(WorkspaceConfig))]
[JsonSerializable(typeof(ProjectInfo))]
[JsonSerializable(typeof(BuildDefaults))]
[JsonSerializable(typeof(ChunkingDefaults))]
[JsonSerializable(typeof(CompressionDefaults))]
[JsonSerializable(typeof(ChannelConfig))]
[JsonSerializable(typeof(ChannelPublishConfig))]
[JsonSerializable(typeof(PublishProfile))]
[JsonSerializable(typeof(CdnConfig))]
[JsonSerializable(typeof(VersionMetadata))]
[JsonSerializable(typeof(BuildInfo))]
[JsonSerializable(typeof(InputInfo))]
[JsonSerializable(typeof(OutputInfo))]
[JsonSerializable(typeof(ChunkingInfo))]
[JsonSerializable(typeof(PublishInfo))]
[JsonSerializable(typeof(UploadedFile))]
[JsonSerializable(typeof(ComparisonInfo))]
[JsonSerializable(typeof(ChannelState))]
[JsonSerializable(typeof(CurrentVersionInfo))]
[JsonSerializable(typeof(VersionHistoryEntry))]
[JsonSerializable(typeof(WorkspaceState))]
[JsonSerializable(typeof(ActiveBuildInfo))]
[JsonSerializable(typeof(PendingPublishInfo))]
[JsonSerializable(typeof(RecentOperationInfo))]
[JsonSerializable(typeof(ChannelJson))]
[JsonSerializable(typeof(ChannelVersionInfo))]
[JsonSerializable(typeof(Dictionary<string, ChannelConfig>))]
[JsonSerializable(typeof(Dictionary<string, PublishProfile>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<UploadedFile>))]
[JsonSerializable(typeof(List<ChannelVersionInfo>))]
[JsonSerializable(typeof(List<VersionHistoryEntry>))]
[JsonSerializable(typeof(List<ActiveBuildInfo>))]
[JsonSerializable(typeof(List<PendingPublishInfo>))]
[JsonSerializable(typeof(List<RecentOperationInfo>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(JsonStringEnumConverter<VersionStatus>)])]
public partial class WorkspaceJsonContext : JsonSerializerContext
{
    /// <summary>
    /// Default options configured for workspace serialization
    /// </summary>
    public static JsonSerializerOptions DefaultOptions => Default.Options;
}
