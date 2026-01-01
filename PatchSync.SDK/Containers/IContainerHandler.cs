using PatchSync.Common.Signatures;

namespace PatchSync.SDK.Containers;

/// <summary>
/// Interface for parsing and reconstructing container formats.
/// Implementations are format-specific (UOP, ZIP, BSA, etc.).
/// </summary>
public interface IContainerHandler
{
    /// <summary>
    /// Unique identifier for this container format (e.g., "uop-v1", "zip-v1").
    /// </summary>
    string FormatId { get; }

    /// <summary>
    /// File extensions this handler supports (e.g., ".uop", ".mul").
    /// </summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    /// Checks if this handler can parse the given stream by examining magic bytes.
    /// The stream position should be at the start and will be reset after the check.
    /// </summary>
    /// <param name="stream">The stream to check.</param>
    /// <returns>True if this handler can parse the stream.</returns>
    bool CanHandle(Stream stream);

    /// <summary>
    /// Parses container structure into entries without extracting data.
    /// </summary>
    /// <param name="container">The container stream to parse.</param>
    /// <returns>Parsed container information.</returns>
    ContainerInfo Parse(Stream container);

    /// <summary>
    /// Reads entry data from container (decompressed if applicable).
    /// </summary>
    /// <param name="container">The container stream.</param>
    /// <param name="entry">The entry to read.</param>
    /// <returns>Stream containing the decompressed entry data.</returns>
    Stream ReadEntry(Stream container, ContainerEntry entry);

    /// <summary>
    /// Reads raw entry bytes (compressed form if applicable).
    /// </summary>
    /// <param name="container">The container stream.</param>
    /// <param name="entry">The entry to read.</param>
    /// <returns>Stream containing the raw stored bytes.</returns>
    Stream ReadRawEntry(Stream container, ContainerEntry entry);

    /// <summary>
    /// Extracts the layout information needed for reconstruction.
    /// </summary>
    /// <param name="container">The container stream.</param>
    /// <param name="info">The parsed container info.</param>
    /// <returns>Layout data for byte-identical reconstruction.</returns>
    ContainerLayout ExtractLayout(Stream container, ContainerInfo info);

    /// <summary>
    /// Writes a container from layout and data sources.
    /// </summary>
    /// <param name="output">The output stream to write to.</param>
    /// <param name="layout">The target container layout.</param>
    /// <param name="dataSource">Source for entry data.</param>
    void WriteContainer(Stream output, ContainerLayout layout, IEntryDataSource dataSource);
}

/// <summary>
/// Interface for providing entry data during container reconstruction.
/// </summary>
public interface IEntryDataSource
{
    /// <summary>
    /// Gets the data for an entry.
    /// </summary>
    /// <param name="entryId">The entry identifier.</param>
    /// <returns>Stream containing the entry data.</returns>
    Stream GetEntryData(string entryId);

    /// <summary>
    /// Gets the per-entry header bytes (if any).
    /// </summary>
    /// <param name="entryId">The entry identifier.</param>
    /// <returns>Header bytes or empty span if none.</returns>
    ReadOnlySpan<byte> GetEntryHeader(string entryId);
}
