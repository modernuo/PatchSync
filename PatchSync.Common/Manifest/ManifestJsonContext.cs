using System.Text.Json.Serialization;

namespace PatchSync.Common.Manifest;

/// <summary>
/// JSON serialization context for NativeAOT compatibility.
/// Uses source generation to avoid reflection.
/// </summary>
[JsonSerializable(typeof(GameManifest))]
[JsonSerializable(typeof(ManifestFile))]
[JsonSerializable(typeof(UpdateStrategy))]
[JsonSerializable(typeof(IReadOnlyList<ManifestFile>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class ManifestJsonContext : JsonSerializerContext;
