using System.Text.Json;
using System.Text.Json.Serialization;

namespace PatchSync.CLI.Workspace;

/// <summary>
/// JSON serialization context for upload log types.
/// Required for NativeAOT compatibility.
/// </summary>
[JsonSerializable(typeof(UploadLogEntry))]
[JsonSerializable(typeof(UploadedFileLog))]
[JsonSerializable(typeof(List<UploadLogEntry>))]
[JsonSerializable(typeof(List<UploadedFileLog>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(JsonStringEnumConverter<UploadStatus>)])]
public partial class UploadLogJsonContext : JsonSerializerContext
{
}
