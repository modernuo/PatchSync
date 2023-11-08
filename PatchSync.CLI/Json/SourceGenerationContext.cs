using System.Text.Json.Serialization;
using PatchSync.Common.Manifest;

namespace PatchSync.CLI.Json;

[JsonSourceGenerationOptions(UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PatchManifest))]
internal partial class JsonSourceGenerationContext : JsonSerializerContext
{
}
