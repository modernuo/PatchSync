using System.Text.Json.Serialization;
using PatchSync.Common.Manifest;

namespace PatchSync.CLI.Json;

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(PatchManifest))]
internal partial class JsonSourceGenerationContext : JsonSerializerContext
{
}