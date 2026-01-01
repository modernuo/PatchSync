using System.Buffers.Binary;
using System.IO.Compression;
using PatchSync.Common.Containers;
using PatchSync.Common.Signatures;

namespace PatchSync.SDK.Containers.Handlers;

/// <summary>
/// Handler for UOP (MYP) container format used by Ultima Online.
/// </summary>
public sealed class UopHandler : IContainerHandler
{
    /// <summary>
    /// UOP magic number "MYP\0" as little-endian uint32.
    /// </summary>
    private const uint UopMagic = 0x0050594D; // "MYP\0" little-endian

    /// <summary>
    /// Size of the UOP file header.
    /// </summary>
    private const int HeaderSize = 28;

    /// <summary>
    /// Size of each entry in a block.
    /// </summary>
    private const int EntrySize = 34;

    /// <inheritdoc />
    public string FormatId => "uop-v1";

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedExtensions { get; } = new[] { ".uop" };

    /// <inheritdoc />
    public bool CanHandle(Stream stream)
    {
        if (stream.Length < HeaderSize)
            return false;

        Span<byte> magic = stackalloc byte[4];
        stream.ReadExactly(magic);
        stream.Position -= 4;

        return BinaryPrimitives.ReadUInt32LittleEndian(magic) == UopMagic;
    }

    /// <inheritdoc />
    public ContainerInfo Parse(Stream container)
    {
        container.Position = 0;

        // Read header
        Span<byte> header = stackalloc byte[HeaderSize];
        container.ReadExactly(header);

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header[0..4]);
        if (magic != UopMagic)
            throw new InvalidDataException($"Invalid UOP magic: expected 0x{UopMagic:X8}, got 0x{magic:X8}");

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
        uint signature = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
        long nextBlockOffset = BinaryPrimitives.ReadInt64LittleEndian(header[12..20]);
        uint blockCapacity = BinaryPrimitives.ReadUInt32LittleEndian(header[20..24]);
        int fileCount = BinaryPrimitives.ReadInt32LittleEndian(header[24..28]);

        var entries = new List<ContainerEntry>(fileCount);

        // Follow block chain
        while (nextBlockOffset != 0)
        {
            container.Position = nextBlockOffset;

            // Read block header
            Span<byte> blockHeader = stackalloc byte[12];
            container.ReadExactly(blockHeader);

            int entryCount = BinaryPrimitives.ReadInt32LittleEndian(blockHeader[0..4]);
            long nextBlock = BinaryPrimitives.ReadInt64LittleEndian(blockHeader[4..12]);

            // Read entries
            Span<byte> entryBuffer = stackalloc byte[EntrySize];
            for (int i = 0; i < entryCount; i++)
            {
                container.ReadExactly(entryBuffer);

                long offset = BinaryPrimitives.ReadInt64LittleEndian(entryBuffer[0..8]);
                int headerLength = BinaryPrimitives.ReadInt32LittleEndian(entryBuffer[8..12]);
                int compressedSize = BinaryPrimitives.ReadInt32LittleEndian(entryBuffer[12..16]);
                int decompressedSize = BinaryPrimitives.ReadInt32LittleEndian(entryBuffer[16..20]);
                ulong fileHash = BinaryPrimitives.ReadUInt64LittleEndian(entryBuffer[20..28]);
                uint dataHash = BinaryPrimitives.ReadUInt32LittleEndian(entryBuffer[28..32]);
                short compressionFlag = BinaryPrimitives.ReadInt16LittleEndian(entryBuffer[32..34]);

                // Skip empty entries
                if (offset == 0)
                    continue;

                // Data starts after the per-entry header
                long dataOffset = offset + headerLength;

                // Read per-entry header bytes for reconstruction
                byte[]? entryHeaderBytes = null;
                if (headerLength > 0)
                {
                    var savedPos = container.Position;
                    container.Position = offset;
                    entryHeaderBytes = new byte[headerLength];
                    container.ReadExactly(entryHeaderBytes);
                    container.Position = savedPos;
                }

                var compression = compressionFlag switch
                {
                    0 => ContainerCompression.None,
                    1 => ContainerCompression.Zlib,
                    3 => ContainerCompression.ZlibBwt,
                    _ => ContainerCompression.Unknown
                };

                entries.Add(new ContainerEntry
                {
                    EntryId = fileHash.ToString("x16"),
                    Offset = dataOffset,
                    StoredSize = compressedSize,
                    DecompressedSize = compression == ContainerCompression.None ? compressedSize : decompressedSize,
                    Compression = compression,
                    HeaderSize = headerLength,
                    HeaderBytes = entryHeaderBytes,
                    ExtraData = fileHash
                });
            }

            nextBlockOffset = nextBlock;
        }

        return new ContainerInfo
        {
            FormatId = FormatId,
            TotalSize = container.Length,
            Entries = entries,
            Metadata = new Dictionary<string, object>
            {
                ["version"] = version,
                ["signature"] = signature,
                ["blockCapacity"] = blockCapacity
            }
        };
    }

    /// <inheritdoc />
    public Stream ReadEntry(Stream container, ContainerEntry entry)
    {
        var raw = ReadRawEntry(container, entry);

        if (entry.Compression == ContainerCompression.None)
            return raw;

        // Decompress
        var rawBytes = new byte[entry.StoredSize];
        raw.ReadExactly(rawBytes);
        raw.Dispose();

        var decompressed = new byte[entry.DecompressedSize];

        if (entry.Compression == ContainerCompression.Zlib ||
            entry.Compression == ContainerCompression.ZlibBwt)
        {
            // Skip zlib header (2 bytes) and use DeflateStream
            using var compressed = new MemoryStream(rawBytes, 2, rawBytes.Length - 2);
            using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
            deflate.ReadExactly(decompressed);

            // ZlibBwt would need additional BWT decompression here
            // For now, we treat it same as Zlib
        }
        else
        {
            throw new NotSupportedException($"Unsupported compression type: {entry.Compression}");
        }

        return new MemoryStream(decompressed, writable: false);
    }

    /// <inheritdoc />
    public Stream ReadRawEntry(Stream container, ContainerEntry entry)
    {
        container.Position = entry.Offset;
        var buffer = new byte[entry.StoredSize];
        container.ReadExactly(buffer);
        return new MemoryStream(buffer, writable: false);
    }

    /// <inheritdoc />
    public ContainerLayout ExtractLayout(Stream container, ContainerInfo info)
    {
        container.Position = 0;

        // For UOP, HeaderTemplate contains ONLY the structural metadata:
        // - File header (28 bytes)
        // - All block tables (block headers + entry metadata)
        // Entry DATA is NOT included - that's what we reconstruct via delta patching.
        //
        // We serialize this as a compact format:
        // [FileHeader: 28 bytes]
        // [BlockCount: 4 bytes]
        // [Block0Offset: 8 bytes][Block0Data: 12 + N*34 bytes]
        // [Block1Offset: 8 bytes][Block1Data: ...]
        // ...

        using var templateStream = new MemoryStream();

        // Read and store file header
        var fileHeader = new byte[HeaderSize];
        container.ReadExactly(fileHeader);
        templateStream.Write(fileHeader);

        // Parse header to find first block
        long nextBlockOffset = BinaryPrimitives.ReadInt64LittleEndian(fileHeader.AsSpan(12, 8));
        uint blockCapacity = BinaryPrimitives.ReadUInt32LittleEndian(fileHeader.AsSpan(20, 4));

        // Collect all blocks
        var blocks = new List<(long Offset, byte[] Data)>();
        while (nextBlockOffset != 0)
        {
            container.Position = nextBlockOffset;

            // Read block header to get entry count and next block
            Span<byte> blockHeader = stackalloc byte[12];
            container.ReadExactly(blockHeader);

            int entryCount = BinaryPrimitives.ReadInt32LittleEndian(blockHeader[0..4]);
            long nextBlock = BinaryPrimitives.ReadInt64LittleEndian(blockHeader[4..12]);

            // Read complete block (header + all entries)
            int blockSize = 12 + entryCount * EntrySize;
            container.Position = nextBlockOffset;
            var blockData = new byte[blockSize];
            container.ReadExactly(blockData);

            blocks.Add((nextBlockOffset, blockData));
            nextBlockOffset = nextBlock;
        }

        // Write block count
        Span<byte> countBuffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(countBuffer, blocks.Count);
        templateStream.Write(countBuffer);

        // Write each block with its offset
        Span<byte> offsetBuffer = stackalloc byte[8];
        foreach (var (offset, data) in blocks)
        {
            BinaryPrimitives.WriteInt64LittleEndian(offsetBuffer, offset);
            templateStream.Write(offsetBuffer);
            templateStream.Write(data);
        }

        var entryLayouts = info.Entries.Select(e => new EntryLayout
        {
            EntryId = e.EntryId,
            Offset = e.Offset,
            HeaderSize = e.HeaderSize,
            DataSize = e.StoredSize,
            HeaderBytes = e.HeaderBytes
        }).ToList();

        return new ContainerLayout
        {
            TotalSize = container.Length,
            HeaderTemplate = templateStream.ToArray(),
            Entries = entryLayouts
        };
    }

    /// <inheritdoc />
    public void WriteContainer(Stream output, ContainerLayout layout, IEntryDataSource dataSource)
    {
        // HeaderTemplate contains compact format:
        // [FileHeader: 28 bytes]
        // [BlockCount: 4 bytes]
        // [Block0Offset: 8 bytes][Block0Data: N bytes]
        // ...

        using var templateStream = new MemoryStream(layout.HeaderTemplate);

        // Pre-allocate output file
        output.SetLength(layout.TotalSize);

        // Read and write file header
        var fileHeader = new byte[HeaderSize];
        templateStream.ReadExactly(fileHeader);
        output.Position = 0;
        output.Write(fileHeader);

        // Read block count
        Span<byte> countBuffer = stackalloc byte[4];
        templateStream.ReadExactly(countBuffer);
        int blockCount = BinaryPrimitives.ReadInt32LittleEndian(countBuffer);

        // Write each block at its original offset
        Span<byte> offsetBuffer = stackalloc byte[8];
        Span<byte> blockHeader = stackalloc byte[12];
        for (int i = 0; i < blockCount; i++)
        {
            templateStream.ReadExactly(offsetBuffer);
            long blockOffset = BinaryPrimitives.ReadInt64LittleEndian(offsetBuffer);

            // Read block header to determine size
            templateStream.ReadExactly(blockHeader);
            int entryCount = BinaryPrimitives.ReadInt32LittleEndian(blockHeader[0..4]);
            int blockSize = 12 + entryCount * EntrySize;

            // Write block header
            output.Position = blockOffset;
            output.Write(blockHeader);

            // Read and write entry metadata
            var entryData = new byte[entryCount * EntrySize];
            templateStream.ReadExactly(entryData);
            output.Write(entryData);
        }

        // Write entry data at their specified offsets
        // Note: HeaderBytes are no longer stored in layout to save space.
        // For patching, ContainerAssembler downloads headers with entry data.
        // For local reconstruction, the data source must provide header+data together,
        // or we write entry data only (suitable for extracted content).
        foreach (var entry in layout.Entries)
        {
            // Write per-entry header if present (legacy support)
            if (entry.HeaderBytes != null && entry.HeaderBytes.Length > 0)
            {
                output.Position = entry.Offset - entry.HeaderSize;
                output.Write(entry.HeaderBytes);
            }

            // Write entry data
            output.Position = entry.Offset;
            using var entryStream = dataSource.GetEntryData(entry.EntryId);
            entryStream.CopyTo(output);
        }
    }
}
