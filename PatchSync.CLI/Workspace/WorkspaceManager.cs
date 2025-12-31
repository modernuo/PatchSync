using System.Text.Json;
using System.Text.RegularExpressions;

namespace PatchSync.CLI.Workspace;

/// <summary>
/// Core workspace operations manager.
/// Handles loading, saving, and validating workspace configuration and state.
/// </summary>
public sealed class WorkspaceManager
{
    public const string WorkspaceConfigFileName = "patchsync.workspace.json";
    public const string InternalDirName = ".patchsync";
    public const string ChannelsDirName = "channels";
    public const string VersionsDirName = "versions";
    public const string SignaturesDirName = "signatures";
    public const string ChannelStateFileName = "channel.json";
    public const string VersionMetadataFileName = "version.json";
    public const string StateFileName = "state.json";

    private readonly string _workspacePath;
    private WorkspaceConfig? _config;
    private WorkspaceState? _state;

    /// <summary>
    /// Path to the workspace root directory
    /// </summary>
    public string WorkspacePath => _workspacePath;

    /// <summary>
    /// Path to the workspace configuration file
    /// </summary>
    public string ConfigPath => Path.Combine(_workspacePath, WorkspaceConfigFileName);

    /// <summary>
    /// Path to the internal .patchsync directory
    /// </summary>
    public string InternalPath => Path.Combine(_workspacePath, InternalDirName);

    /// <summary>
    /// Path to the channels directory
    /// </summary>
    public string ChannelsPath => Path.Combine(_workspacePath, ChannelsDirName);

    /// <summary>
    /// Path to the state file
    /// </summary>
    public string StatePath => Path.Combine(InternalPath, StateFileName);

    /// <summary>
    /// Loaded workspace configuration (null if not loaded)
    /// </summary>
    public WorkspaceConfig? Config => _config;

    /// <summary>
    /// Whether a workspace exists at the configured path
    /// </summary>
    public bool Exists => File.Exists(ConfigPath);

    private WorkspaceManager(string workspacePath)
    {
        _workspacePath = Path.GetFullPath(workspacePath);
    }

    /// <summary>
    /// Creates a workspace manager for the specified path.
    /// Does not load or validate the workspace.
    /// </summary>
    public static WorkspaceManager ForPath(string path)
    {
        return new WorkspaceManager(path);
    }

    /// <summary>
    /// Finds a workspace by searching upward from the specified path.
    /// </summary>
    public static WorkspaceManager? FindWorkspace(string startPath)
    {
        var current = Path.GetFullPath(startPath);

        while (!string.IsNullOrEmpty(current))
        {
            var configPath = Path.Combine(current, WorkspaceConfigFileName);
            if (File.Exists(configPath))
            {
                return new WorkspaceManager(current);
            }

            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }

        return null;
    }

    /// <summary>
    /// Loads the workspace configuration from disk.
    /// </summary>
    public async Task<WorkspaceConfig> LoadConfigAsync(CancellationToken ct = default)
    {
        if (!File.Exists(ConfigPath))
            throw new WorkspaceException($"Workspace configuration not found at: {ConfigPath}");

        await using var stream = File.OpenRead(ConfigPath);
        _config = await JsonSerializer.DeserializeAsync(stream, WorkspaceJsonContext.Default.WorkspaceConfig, ct)
            ?? throw new WorkspaceException("Failed to deserialize workspace configuration");

        return _config;
    }

    /// <summary>
    /// Saves the workspace configuration to disk atomically.
    /// </summary>
    public async Task SaveConfigAsync(WorkspaceConfig config, CancellationToken ct = default)
    {
        var tempPath = ConfigPath + ".tmp";

        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, config, WorkspaceJsonContext.Default.WorkspaceConfig, ct);
        }

        File.Move(tempPath, ConfigPath, overwrite: true);
        _config = config;
    }

    /// <summary>
    /// Loads the workspace runtime state.
    /// </summary>
    public async Task<WorkspaceState> LoadStateAsync(CancellationToken ct = default)
    {
        if (!File.Exists(StatePath))
        {
            _state = new WorkspaceState { LastUpdated = DateTime.UtcNow };
            return _state;
        }

        await using var stream = File.OpenRead(StatePath);
        _state = await JsonSerializer.DeserializeAsync(stream, WorkspaceJsonContext.Default.WorkspaceState, ct)
            ?? new WorkspaceState { LastUpdated = DateTime.UtcNow };

        return _state;
    }

    /// <summary>
    /// Saves the workspace runtime state atomically.
    /// </summary>
    public async Task SaveStateAsync(WorkspaceState state, CancellationToken ct = default)
    {
        Directory.CreateDirectory(InternalPath);
        var tempPath = StatePath + ".tmp";

        state.LastUpdated = DateTime.UtcNow;

        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, state, WorkspaceJsonContext.Default.WorkspaceState, ct);
        }

        File.Move(tempPath, StatePath, overwrite: true);
        _state = state;
    }

    /// <summary>
    /// Gets the path to a channel directory.
    /// </summary>
    public string GetChannelPath(string channelId)
    {
        return Path.Combine(ChannelsPath, channelId);
    }

    /// <summary>
    /// Gets the path to a channel's state file.
    /// </summary>
    public string GetChannelStatePath(string channelId)
    {
        return Path.Combine(GetChannelPath(channelId), ChannelStateFileName);
    }

    /// <summary>
    /// Gets the path to a version directory.
    /// </summary>
    public string GetVersionPath(string channelId, string version)
    {
        return Path.Combine(GetChannelPath(channelId), VersionsDirName, version);
    }

    /// <summary>
    /// Gets the path to a version's metadata file.
    /// </summary>
    public string GetVersionMetadataPath(string channelId, string version)
    {
        return Path.Combine(GetVersionPath(channelId, version), VersionMetadataFileName);
    }

    /// <summary>
    /// Gets the path to a version's signatures directory.
    /// </summary>
    public string GetSignaturesPath(string channelId, string version)
    {
        return Path.Combine(GetVersionPath(channelId, version), SignaturesDirName);
    }

    /// <summary>
    /// Gets the path to a version's manifest file.
    /// </summary>
    public string GetManifestPath(string channelId, string version)
    {
        return Path.Combine(GetVersionPath(channelId, version), "manifest.json");
    }

    /// <summary>
    /// Loads a channel's state from disk.
    /// </summary>
    public async Task<ChannelState?> LoadChannelStateAsync(string channelId, CancellationToken ct = default)
    {
        var path = GetChannelStatePath(channelId);
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync(stream, WorkspaceJsonContext.Default.ChannelState, ct);
    }

    /// <summary>
    /// Saves a channel's state atomically.
    /// </summary>
    public async Task SaveChannelStateAsync(string channelId, ChannelState state, CancellationToken ct = default)
    {
        var path = GetChannelStatePath(channelId);
        var tempPath = path + ".tmp";

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, state, WorkspaceJsonContext.Default.ChannelState, ct);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// Loads a version's metadata from disk.
    /// </summary>
    public async Task<VersionMetadata?> LoadVersionMetadataAsync(string channelId, string version, CancellationToken ct = default)
    {
        var path = GetVersionMetadataPath(channelId, version);
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync(stream, WorkspaceJsonContext.Default.VersionMetadata, ct);
    }

    /// <summary>
    /// Saves a version's metadata atomically.
    /// </summary>
    public async Task SaveVersionMetadataAsync(string channelId, string version, VersionMetadata metadata, CancellationToken ct = default)
    {
        var path = GetVersionMetadataPath(channelId, version);
        var tempPath = path + ".tmp";

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, metadata, WorkspaceJsonContext.Default.VersionMetadata, ct);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// Gets all version directories for a channel, sorted by creation time descending.
    /// </summary>
    public IEnumerable<string> GetVersions(string channelId)
    {
        var versionsPath = Path.Combine(GetChannelPath(channelId), VersionsDirName);
        if (!Directory.Exists(versionsPath))
            return [];

        return Directory.GetDirectories(versionsPath)
            .OrderByDescending(d => Directory.GetCreationTimeUtc(d))
            .Select(Path.GetFileName)
            .Where(n => n != null)!;
    }

    /// <summary>
    /// Checks if a version exists for a channel.
    /// </summary>
    public bool VersionExists(string channelId, string version)
    {
        return Directory.Exists(GetVersionPath(channelId, version));
    }

    /// <summary>
    /// Validates a version string against a channel's pattern.
    /// </summary>
    public bool ValidateVersionPattern(string channelId, string version)
    {
        if (_config == null)
            throw new InvalidOperationException("Workspace configuration not loaded");

        if (!_config.Channels.TryGetValue(channelId, out var channelConfig))
            throw new WorkspaceException($"Channel not found: {channelId}");

        if (string.IsNullOrEmpty(channelConfig.VersionPattern))
            return true;

        try
        {
            var regex = new Regex(channelConfig.VersionPattern);
            return regex.IsMatch(version);
        }
        catch (RegexParseException)
        {
            // Invalid regex pattern in config, allow all versions
            return true;
        }
    }

    /// <summary>
    /// Gets the default channel ID from configuration.
    /// </summary>
    public string? GetDefaultChannelId()
    {
        if (_config == null)
            return null;

        foreach (var (id, config) in _config.Channels)
        {
            if (config.IsDefault)
                return id;
        }

        // Return first channel if none marked as default
        return _config.Channels.Keys.FirstOrDefault();
    }

    /// <summary>
    /// Resolves a publish profile for a channel.
    /// </summary>
    public PublishProfile? GetPublishProfile(string channelId)
    {
        if (_config == null)
            return null;

        if (!_config.Channels.TryGetValue(channelId, out var channelConfig))
            return null;

        var profileName = channelConfig.Publish?.Profile;
        if (string.IsNullOrEmpty(profileName))
            return null;

        _config.PublishProfiles.TryGetValue(profileName, out var profile);
        return profile;
    }

    /// <summary>
    /// Creates the initial workspace directory structure.
    /// </summary>
    public void CreateDirectoryStructure()
    {
        Directory.CreateDirectory(_workspacePath);
        Directory.CreateDirectory(InternalPath);
        Directory.CreateDirectory(ChannelsPath);
    }

    /// <summary>
    /// Creates directory structure for a channel.
    /// </summary>
    public void CreateChannelStructure(string channelId)
    {
        var channelPath = GetChannelPath(channelId);
        Directory.CreateDirectory(channelPath);
        Directory.CreateDirectory(Path.Combine(channelPath, VersionsDirName));
    }

    /// <summary>
    /// Creates directory structure for a version.
    /// </summary>
    public void CreateVersionStructure(string channelId, string version)
    {
        var versionPath = GetVersionPath(channelId, version);
        Directory.CreateDirectory(versionPath);
        Directory.CreateDirectory(GetSignaturesPath(channelId, version));
    }

    /// <summary>
    /// Creates the default .gitignore file for the workspace.
    /// </summary>
    public async Task CreateGitIgnoreAsync(CancellationToken ct = default)
    {
        var gitignorePath = Path.Combine(_workspacePath, ".gitignore");
        if (File.Exists(gitignorePath))
            return;

        const string content = """
            # PatchSync workspace ignore patterns

            # Internal state and credentials
            .patchsync/

            # Large build artifacts (optional - remove if you want to version them)
            channels/*/versions/*/files/
            channels/*/versions/*/compressed/

            # Temporary files
            *.tmp
            *.lock
            """;

        await File.WriteAllTextAsync(gitignorePath, content, ct);
    }
}

/// <summary>
/// Exception thrown for workspace-related errors.
/// </summary>
public class WorkspaceException : Exception
{
    public WorkspaceException(string message) : base(message) { }
    public WorkspaceException(string message, Exception innerException) : base(message, innerException) { }
}
