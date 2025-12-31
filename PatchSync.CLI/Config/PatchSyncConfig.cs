using System.Text.Json;
using System.Text.Json.Serialization;

namespace PatchSync.CLI.Config;

/// <summary>
/// PatchSync CLI configuration.
/// Can be stored in patchsync.json in the current directory or user profile.
/// </summary>
public sealed class PatchSyncConfig
{
    /// <summary>
    /// S3-compatible storage configuration.
    /// </summary>
    public S3Config? S3 { get; set; }

    /// <summary>
    /// Default build settings.
    /// </summary>
    public BuildDefaults? Build { get; set; }

    /// <summary>
    /// Saves configuration to a file.
    /// </summary>
    public async Task SaveAsync(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, this, ConfigJsonContext.Default.PatchSyncConfig);
    }

    /// <summary>
    /// Loads configuration from a file.
    /// </summary>
    public static async Task<PatchSyncConfig?> LoadAsync(string path)
    {
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync(stream, ConfigJsonContext.Default.PatchSyncConfig);
    }

    /// <summary>
    /// Loads configuration from default locations.
    /// Priority: ./patchsync.json > ~/.patchsync/config.json
    /// </summary>
    public static async Task<PatchSyncConfig> LoadOrDefaultAsync()
    {
        // Check current directory first
        var localPath = Path.Combine(Directory.GetCurrentDirectory(), "patchsync.json");
        var config = await LoadAsync(localPath);
        if (config != null)
            return config;

        // Check user profile
        var userPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".patchsync",
            "config.json");
        config = await LoadAsync(userPath);
        if (config != null)
            return config;

        return new PatchSyncConfig();
    }

    /// <summary>
    /// Gets the default config file path in user profile.
    /// </summary>
    public static string GetDefaultConfigPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".patchsync",
            "config.json");
    }
}

/// <summary>
/// S3-compatible storage configuration.
/// NOTE: Secrets (AccessKey, SecretKey) should be stored via CredentialManager, not in this config.
/// </summary>
public sealed class S3Config
{
    /// <summary>
    /// S3 endpoint URL (e.g., https://s3.amazonaws.com, https://nyc3.digitaloceanspaces.com).
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// S3 region (e.g., us-east-1).
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// S3 bucket name.
    /// </summary>
    public string? Bucket { get; set; }

    /// <summary>
    /// Access key ID.
    /// WARNING: Consider using CredentialManager or environment variables instead.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AccessKey { get; set; }

    /// <summary>
    /// Secret access key.
    /// WARNING: This field is NOT serialized to JSON. Use CredentialManager for secure storage.
    /// </summary>
    [JsonIgnore]
    public string? SecretKey { get; set; }

    /// <summary>
    /// Optional prefix for all uploads (e.g., "game/v1.0.0/").
    /// </summary>
    public string? Prefix { get; set; }

    /// <summary>
    /// Whether to use path-style URLs instead of virtual-hosted style.
    /// Required for some S3-compatible providers.
    /// </summary>
    public bool PathStyle { get; set; } = true;

    /// <summary>
    /// Public CDN URL for accessing uploaded files.
    /// Used to generate manifest base URLs.
    /// </summary>
    public string? PublicUrl { get; set; }

    /// <summary>
    /// Checks if this config has all required fields for upload.
    /// </summary>
    [JsonIgnore]
    public bool IsComplete =>
        !string.IsNullOrEmpty(Endpoint) &&
        !string.IsNullOrEmpty(Bucket) &&
        !string.IsNullOrEmpty(AccessKey) &&
        !string.IsNullOrEmpty(SecretKey);

    /// <summary>
    /// Gets a description of which fields are missing.
    /// </summary>
    [JsonIgnore]
    public string MissingFields
    {
        get
        {
            var missing = new List<string>();
            if (string.IsNullOrEmpty(Endpoint)) missing.Add("endpoint");
            if (string.IsNullOrEmpty(Bucket)) missing.Add("bucket");
            if (string.IsNullOrEmpty(AccessKey)) missing.Add("access-key");
            if (string.IsNullOrEmpty(SecretKey)) missing.Add("secret-key");
            return string.Join(", ", missing);
        }
    }
}

/// <summary>
/// Default build settings.
/// </summary>
public sealed class BuildDefaults
{
    /// <summary>
    /// Default chunking algorithm.
    /// </summary>
    public string Algorithm { get; set; } = "fastcdc-v1";

    /// <summary>
    /// Default minimum chunk size.
    /// </summary>
    public int MinChunkSize { get; set; } = 4096;

    /// <summary>
    /// Default average chunk size.
    /// </summary>
    public int AvgChunkSize { get; set; } = 16384;

    /// <summary>
    /// Default maximum chunk size.
    /// </summary>
    public int MaxChunkSize { get; set; } = 65536;

    /// <summary>
    /// Minimum file size for delta patching.
    /// </summary>
    public long MinDeltaSize { get; set; } = 65536;
}

/// <summary>
/// AOT-compatible JSON serialization context for config.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PatchSyncConfig))]
[JsonSerializable(typeof(S3Config))]
[JsonSerializable(typeof(BuildDefaults))]
public partial class ConfigJsonContext : JsonSerializerContext;
