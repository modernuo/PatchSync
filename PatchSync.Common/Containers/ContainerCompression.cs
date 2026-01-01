namespace PatchSync.Common.Containers;

/// <summary>
/// Compression types used within container formats.
/// Values are aligned with common format conventions (e.g., UOP uses 0, 1, 3).
/// </summary>
public enum ContainerCompression : byte
{
    /// <summary>
    /// Entry is stored uncompressed.
    /// </summary>
    None = 0,

    /// <summary>
    /// Entry is compressed using zlib/deflate.
    /// </summary>
    Zlib = 1,

    /// <summary>
    /// Entry is compressed using zlib with Burrows-Wheeler Transform preprocessing.
    /// Used by some UOP files.
    /// </summary>
    ZlibBwt = 3,

    /// <summary>
    /// Entry is compressed using Zstandard.
    /// </summary>
    Zstd = 10,

    /// <summary>
    /// Entry is compressed using LZ4.
    /// </summary>
    Lz4 = 11,

    /// <summary>
    /// Entry compression is unknown or unsupported.
    /// Treat as opaque blob (entry-level hash only).
    /// </summary>
    Unknown = 255
}
