using System.Text.Json.Serialization;

namespace PatchSync.Common.Manifest;

/// <summary>
/// Game/application manifest describing all files and their update strategies.
/// This is the entry point for patching - downloaded first to determine what needs updating.
/// </summary>
public sealed class GameManifest
{
    /// <summary>
    /// Version string (e.g., "1.2.3" or "2024.01.15").
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// When this manifest was built.
    /// </summary>
    public required DateTime BuildDate { get; init; }

    /// <summary>
    /// Chunking algorithms supported by this manifest.
    /// Client must support at least one to use delta patching.
    /// </summary>
    public required IReadOnlyList<string> SupportedAlgorithms { get; init; }

    /// <summary>
    /// Preferred algorithm to use if client supports multiple.
    /// </summary>
    public required string PreferredAlgorithm { get; init; }

    /// <summary>
    /// Optional URL for full archive download (de novo install).
    /// Used when client doesn't support any algorithms or delta is inefficient.
    /// </summary>
    public string? FallbackUrl { get; init; }

    /// <summary>
    /// Base URL for all file paths in this manifest.
    /// </summary>
    public required string BaseUrl { get; init; }

    /// <summary>
    /// All files in this version.
    /// </summary>
    public required IReadOnlyList<ManifestFile> Files { get; init; }

    /// <summary>
    /// Optional metadata (game name, channel, etc.).
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// A file entry in the manifest.
/// </summary>
public sealed class ManifestFile
{
    /// <summary>
    /// Relative path from install root (e.g., "game.exe" or "data/assets.pak").
    /// Uses forward slashes as separator.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Uncompressed file size in bytes.
    /// </summary>
    public required long Size { get; init; }

    /// <summary>
    /// SHA256 hash of the file (lowercase hex).
    /// </summary>
    public required string Hash { get; init; }

    /// <summary>
    /// Update strategy for this file.
    /// </summary>
    public UpdateStrategy Strategy { get; init; } = UpdateStrategy.Delta;

    /// <summary>
    /// Relative URL to signature file (e.g., "signatures/game.exe.psi1").
    /// Required if Strategy is Delta.
    /// </summary>
    public string? SignatureUrl { get; init; }

    /// <summary>
    /// Size of compressed file (for fallback decision).
    /// </summary>
    public long CompressedSize { get; init; }

    /// <summary>
    /// Relative URL to compressed file (e.g., "compressed/game.exe.zst").
    /// Optional fallback when delta is inefficient.
    /// </summary>
    public string? CompressedUrl { get; init; }

    /// <summary>
    /// Relative URL to the raw file for byte-range requests.
    /// Defaults to "files/{Path}" if not specified.
    /// </summary>
    public string? FileUrl { get; init; }

    /// <summary>
    /// Container format identifier (e.g., "uop-v1", "zip-v1").
    /// Only set if Strategy is VirtualDelta.
    /// </summary>
    public string? ContainerFormat { get; init; }

    /// <summary>
    /// Relative URL to virtual signature file (e.g., "signatures/game.uop.vsig").
    /// Required if Strategy is VirtualDelta.
    /// </summary>
    public string? VirtualSignatureUrl { get; init; }

    /// <summary>
    /// SHA256 hash of the file at the time the base version was created.
    /// Used for "UpdateIfNotModified" strategy - if local file matches this hash,
    /// it hasn't been modified by the user and can be safely updated.
    /// Only set when Strategy is UpdateIfNotModified.
    /// </summary>
    public string? BaseHash { get; init; }

    /// <summary>
    /// Whether this file's strategy was manually overridden (not auto-determined).
    /// When true, the strategy was explicitly set by the developer rather than
    /// being automatically determined based on file type/size.
    /// </summary>
    public bool IsStrategyOverride { get; init; }
}

/// <summary>
/// Strategy for updating a file.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<UpdateStrategy>))]
public enum UpdateStrategy
{
    /// <summary>
    /// Use delta patching with CDC signatures.
    /// Best for large files that change partially between versions.
    /// </summary>
    Delta,

    /// <summary>
    /// Always download compressed file (no delta).
    /// Best for compressed media (mp3, ogg, mp4) where delta is useless.
    /// </summary>
    AlwaysCompressed,

    /// <summary>
    /// Download full file if hash doesn't match.
    /// Best for small files where delta overhead isn't worth it.
    /// </summary>
    HashCheck,

    /// <summary>
    /// Never update this file (user-modifiable).
    /// File is created on first install but never overwritten.
    /// </summary>
    CreateOnly,

    /// <summary>
    /// Delete this file if it exists.
    /// Used for removing files from previous versions.
    /// </summary>
    Delete,

    /// <summary>
    /// Virtual delta patching for container formats (UOP, ZIP, BSA, etc.).
    /// Uses entry-level and sub-entry CDC chunking for efficient updates.
    /// </summary>
    VirtualDelta,

    /// <summary>
    /// Update only if the local file matches the original hash from install.
    /// If the user has modified the file (hash differs from BaseHash),
    /// preserve their changes and skip the update.
    /// Requires BaseHash to be set in the ManifestFile.
    /// </summary>
    UpdateIfNotModified
}
