using System.Buffers.Binary;
using System.Text;
using PatchSync.Common.Containers;

namespace PatchSync.Common.Signatures;

/// <summary>
/// VSI1 (Virtual Signature Index v1) file format for container signatures.
///
/// Binary format:
/// [Magic: 4 bytes]            "VSI1" (0x56534931 big-endian)
/// [Version: 1 byte]           Format version
/// [Flags: 1 byte]             Reserved flags
/// [Algorithm: 1 byte]         CDC algorithm enum
/// [Reserved: 1 byte]          Padding
/// [MinSize: 4 bytes]          Minimum chunk size (little-endian)
/// [AvgSize: 4 bytes]          Average chunk size (little-endian)
/// [MaxSize: 4 bytes]          Maximum chunk size (little-endian)
/// [TotalSize: 8 bytes]        Container total size (little-endian)
/// [ContainerHash: 32 bytes]   SHA256 of complete container
/// [FormatLen: 1 byte]         Container format string length
/// [Format: N bytes]           Container format ID (UTF-8)
/// [LayoutLen: 4 bytes]        Layout section length (little-endian)
/// [Layout: N bytes]           Serialized layout data
/// [EntryCount: 4 bytes]       Number of entries (little-endian)
/// [Entries...]                Entry signatures
///
/// Per entry:
/// [EntryIdLen: 2 bytes]       Entry ID length (little-endian)
/// [EntryId: N bytes]          Entry ID (UTF-8)
/// [TargetOffset: 8 bytes]     Target container offset (little-endian)
/// [StoredSize: 4 bytes]       Stored data size (little-endian)
/// [Compression: 1 byte]       Compression type
/// [StoredHash: 32 bytes]      SHA256 of stored bytes
/// [ChunkCount: 4 bytes]       Number of CDC chunks (0 = none)
/// [Chunks: N * 44 bytes]      CDC chunks (same format as PSI1)
/// </summary>
public static class VirtualSignatureFormat
{
    /// <summary>
    /// Magic bytes "VSI1" for format identification.
    /// </summary>
    public const uint Magic = 0x56534931; // "VSI1" as big-endian uint32

    /// <summary>
    /// Current format version.
    /// </summary>
    public const byte CurrentVersion = 1;

    /// <summary>
    /// Fixed header size (before variable-length format string).
    /// </summary>
    public const int FixedHeaderSize = 60; // 4+1+1+1+1+4+4+4+8+32 = 60

    /// <summary>
    /// Size of each chunk entry in bytes (same as PSI1).
    /// </summary>
    public const int ChunkEntrySize = 44; // 8 (offset) + 4 (length) + 32 (hash)

    /// <summary>
    /// SHA256 hash size in bytes.
    /// </summary>
    public const int HashSize = 32;

    /// <summary>
    /// Writes a virtual signature file to a stream.
    /// </summary>
    public static void Write(Stream output, VirtualSignatureFile signature)
    {
        var formatBytes = Encoding.UTF8.GetBytes(signature.ContainerFormat);
        if (formatBytes.Length > 255)
            throw new ArgumentException("Container format string too long (max 255 bytes)");

        // Write fixed header
        Span<byte> header = stackalloc byte[FixedHeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(header[0..4], Magic);
        header[4] = CurrentVersion;
        header[5] = 0; // Flags (reserved)
        header[6] = (byte)signature.AlgorithmId.ToSignatureAlgorithm();
        header[7] = 0; // Reserved

        BinaryPrimitives.WriteInt32LittleEndian(header[8..12], signature.MinSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..16], signature.AverageSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..20], signature.MaxSize);
        BinaryPrimitives.WriteInt64LittleEndian(header[20..28], signature.TotalSize);
        signature.ContainerHash.AsSpan().CopyTo(header[28..60]);

        output.Write(header);

        // Write format string
        output.WriteByte((byte)formatBytes.Length);
        output.Write(formatBytes);

        // Serialize and write layout
        var layoutBytes = SerializeLayout(signature.Layout);
        Span<byte> layoutLen = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(layoutLen, layoutBytes.Length);
        output.Write(layoutLen);
        output.Write(layoutBytes);

        // Write entry count
        Span<byte> entryCount = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(entryCount, signature.Entries.Count);
        output.Write(entryCount);

        // Write entries
        Span<byte> entryBuffer = stackalloc byte[49]; // 2+8+4+1+32+4 = 51 minus entryId
        Span<byte> chunkBuffer = stackalloc byte[ChunkEntrySize];
        foreach (var entry in signature.Entries)
        {
            var entryIdBytes = Encoding.UTF8.GetBytes(entry.EntryId);
            if (entryIdBytes.Length > ushort.MaxValue)
                throw new ArgumentException($"Entry ID too long: {entry.EntryId}");

            // Entry ID length + ID
            BinaryPrimitives.WriteUInt16LittleEndian(entryBuffer[0..2], (ushort)entryIdBytes.Length);
            output.Write(entryBuffer[0..2]);
            output.Write(entryIdBytes);

            // Entry metadata
            BinaryPrimitives.WriteInt64LittleEndian(entryBuffer[0..8], entry.TargetOffset);
            BinaryPrimitives.WriteInt32LittleEndian(entryBuffer[8..12], entry.StoredSize);
            entryBuffer[12] = (byte)entry.Compression;
            entry.StoredHash.AsSpan().CopyTo(entryBuffer[13..45]);
            BinaryPrimitives.WriteInt32LittleEndian(entryBuffer[45..49], entry.Chunks?.Count ?? 0);
            output.Write(entryBuffer[0..49]);

            // Write chunks if present
            if (entry.Chunks is { Count: > 0 })
            {
                foreach (var chunk in entry.Chunks)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(chunkBuffer[0..8], chunk.Offset);
                    BinaryPrimitives.WriteInt32LittleEndian(chunkBuffer[8..12], chunk.Length);
                    chunk.Hash.AsSpan().CopyTo(chunkBuffer[12..44]);
                    output.Write(chunkBuffer);
                }
            }
        }
    }

    /// <summary>
    /// Reads a virtual signature file from a stream.
    /// </summary>
    public static VirtualSignatureFile Read(Stream input)
    {
        Span<byte> header = stackalloc byte[FixedHeaderSize];
        if (!TryReadExact(input, header))
            throw new InvalidDataException("Virtual signature file too short for header");

        // Verify magic
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(header[0..4]);
        if (magic != Magic)
            throw new InvalidDataException($"Invalid virtual signature magic: expected 0x{Magic:X8}, got 0x{magic:X8}");

        byte version = header[4];
        if (version > CurrentVersion)
            throw new NotSupportedException($"Virtual signature version {version} not supported (max {CurrentVersion})");

        // byte flags = header[5]; // Reserved
        var algorithm = (SignatureAlgorithm)header[6];
        // byte reserved = header[7];

        int minSize = BinaryPrimitives.ReadInt32LittleEndian(header[8..12]);
        int avgSize = BinaryPrimitives.ReadInt32LittleEndian(header[12..16]);
        int maxSize = BinaryPrimitives.ReadInt32LittleEndian(header[16..20]);
        long totalSize = BinaryPrimitives.ReadInt64LittleEndian(header[20..28]);
        byte[] containerHash = header[28..60].ToArray();

        // Read format string
        int formatLen = input.ReadByte();
        if (formatLen < 0)
            throw new InvalidDataException("Unexpected end of stream reading format length");

        Span<byte> formatBytes = stackalloc byte[formatLen];
        if (!TryReadExact(input, formatBytes))
            throw new InvalidDataException("Unexpected end of stream reading format string");
        string containerFormat = Encoding.UTF8.GetString(formatBytes);

        // Read layout
        Span<byte> layoutLenBytes = stackalloc byte[4];
        if (!TryReadExact(input, layoutLenBytes))
            throw new InvalidDataException("Unexpected end of stream reading layout length");
        int layoutLen = BinaryPrimitives.ReadInt32LittleEndian(layoutLenBytes);

        byte[] layoutBytes = new byte[layoutLen];
        if (!TryReadExact(input, layoutBytes))
            throw new InvalidDataException("Unexpected end of stream reading layout data");
        var layout = DeserializeLayout(layoutBytes);

        // Read entry count
        Span<byte> entryCountBytes = stackalloc byte[4];
        if (!TryReadExact(input, entryCountBytes))
            throw new InvalidDataException("Unexpected end of stream reading entry count");
        int entryCount = BinaryPrimitives.ReadInt32LittleEndian(entryCountBytes);

        // Read entries
        var entries = new EntrySignature[entryCount];
        Span<byte> entryBuffer = stackalloc byte[49];
        Span<byte> idLenBytes = stackalloc byte[2];
        Span<byte> chunkBuffer = stackalloc byte[ChunkEntrySize];

        for (int i = 0; i < entryCount; i++)
        {
            // Read entry ID
            if (!TryReadExact(input, idLenBytes))
                throw new InvalidDataException($"Unexpected end of stream reading entry {i} ID length");
            int idLen = BinaryPrimitives.ReadUInt16LittleEndian(idLenBytes);

            Span<byte> idBytes = stackalloc byte[idLen];
            if (!TryReadExact(input, idBytes))
                throw new InvalidDataException($"Unexpected end of stream reading entry {i} ID");
            string entryId = Encoding.UTF8.GetString(idBytes);

            // Read entry metadata
            if (!TryReadExact(input, entryBuffer))
                throw new InvalidDataException($"Unexpected end of stream reading entry {i} metadata");

            long targetOffset = BinaryPrimitives.ReadInt64LittleEndian(entryBuffer[0..8]);
            int storedSize = BinaryPrimitives.ReadInt32LittleEndian(entryBuffer[8..12]);
            var compression = (ContainerCompression)entryBuffer[12];
            byte[] storedHash = entryBuffer[13..45].ToArray();
            int chunkCount = BinaryPrimitives.ReadInt32LittleEndian(entryBuffer[45..49]);

            // Read chunks if present
            IReadOnlyList<SignatureChunk>? chunks = null;
            if (chunkCount > 0)
            {
                var chunkList = new SignatureChunk[chunkCount];

                for (int j = 0; j < chunkCount; j++)
                {
                    if (!TryReadExact(input, chunkBuffer))
                        throw new InvalidDataException($"Unexpected end of stream reading entry {i} chunk {j}");

                    long offset = BinaryPrimitives.ReadInt64LittleEndian(chunkBuffer[0..8]);
                    int length = BinaryPrimitives.ReadInt32LittleEndian(chunkBuffer[8..12]);
                    byte[] hash = chunkBuffer[12..44].ToArray();
                    chunkList[j] = new SignatureChunk(offset, length, hash);
                }

                chunks = chunkList;
            }

            entries[i] = new EntrySignature
            {
                EntryId = entryId,
                TargetOffset = targetOffset,
                StoredSize = storedSize,
                Compression = compression,
                StoredHash = storedHash,
                Chunks = chunks
            };
        }

        return new VirtualSignatureFile
        {
            ContainerFormat = containerFormat,
            AlgorithmId = algorithm.ToAlgorithmId(),
            MinSize = minSize,
            AverageSize = avgSize,
            MaxSize = maxSize,
            TotalSize = totalSize,
            ContainerHash = containerHash,
            Layout = layout,
            Entries = entries
        };
    }

    /// <summary>
    /// Serializes container layout to bytes.
    /// NOTE: HeaderBytes are NOT stored - they're downloaded/copied with entry data.
    /// </summary>
    private static byte[] SerializeLayout(ContainerLayout layout)
    {
        using var ms = new MemoryStream();

        // Write total size
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, layout.TotalSize);
        ms.Write(buffer[..8]);

        // Write header template
        BinaryPrimitives.WriteInt32LittleEndian(buffer, layout.HeaderTemplate.Length);
        ms.Write(buffer[..4]);
        ms.Write(layout.HeaderTemplate);

        // Write entry count
        BinaryPrimitives.WriteInt32LittleEndian(buffer, layout.Entries.Count);
        ms.Write(buffer[..4]);

        // Write entries (compact: no HeaderBytes, only HeaderSize)
        foreach (var entry in layout.Entries)
        {
            var idBytes = Encoding.UTF8.GetBytes(entry.EntryId);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, (ushort)idBytes.Length);
            ms.Write(buffer[..2]);
            ms.Write(idBytes);

            BinaryPrimitives.WriteInt64LittleEndian(buffer, entry.Offset);
            ms.Write(buffer[..8]);

            BinaryPrimitives.WriteInt32LittleEndian(buffer, entry.HeaderSize);
            ms.Write(buffer[..4]);

            BinaryPrimitives.WriteInt32LittleEndian(buffer, entry.DataSize);
            ms.Write(buffer[..4]);

            // No longer storing HeaderBytes - they're part of the download range
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Deserializes container layout from bytes.
    /// NOTE: HeaderBytes are NOT stored - they're downloaded/copied with entry data.
    /// </summary>
    private static ContainerLayout DeserializeLayout(byte[] data)
    {
        using var ms = new MemoryStream(data);

        Span<byte> buffer = stackalloc byte[8];

        // Read total size
        if (!TryReadExact(ms, buffer[..8]))
            throw new InvalidDataException("Layout data truncated reading total size");
        long totalSize = BinaryPrimitives.ReadInt64LittleEndian(buffer);

        // Read header template
        if (!TryReadExact(ms, buffer[..4]))
            throw new InvalidDataException("Layout data truncated reading header template length");
        int headerLen = BinaryPrimitives.ReadInt32LittleEndian(buffer);

        byte[] headerTemplate = new byte[headerLen];
        if (!TryReadExact(ms, headerTemplate))
            throw new InvalidDataException("Layout data truncated reading header template");

        // Read entry count
        if (!TryReadExact(ms, buffer[..4]))
            throw new InvalidDataException("Layout data truncated reading entry count");
        int entryCount = BinaryPrimitives.ReadInt32LittleEndian(buffer);

        // Read entries
        var entries = new EntryLayout[entryCount];
        for (int i = 0; i < entryCount; i++)
        {
            // Read entry ID
            if (!TryReadExact(ms, buffer[..2]))
                throw new InvalidDataException($"Layout entry {i} truncated reading ID length");
            int idLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer);

            Span<byte> idBytes = stackalloc byte[idLen];
            if (!TryReadExact(ms, idBytes))
                throw new InvalidDataException($"Layout entry {i} truncated reading ID");
            string entryId = Encoding.UTF8.GetString(idBytes);

            // Read offset, header size, data size
            if (!TryReadExact(ms, buffer[..8]))
                throw new InvalidDataException($"Layout entry {i} truncated reading offset");
            long offset = BinaryPrimitives.ReadInt64LittleEndian(buffer);

            if (!TryReadExact(ms, buffer[..4]))
                throw new InvalidDataException($"Layout entry {i} truncated reading header size");
            int headerSize = BinaryPrimitives.ReadInt32LittleEndian(buffer);

            if (!TryReadExact(ms, buffer[..4]))
                throw new InvalidDataException($"Layout entry {i} truncated reading data size");
            int dataSize = BinaryPrimitives.ReadInt32LittleEndian(buffer);

            // HeaderBytes are NOT stored - they're part of the download range
            entries[i] = new EntryLayout
            {
                EntryId = entryId,
                Offset = offset,
                HeaderSize = headerSize,
                DataSize = dataSize,
                HeaderBytes = null // Populated at runtime if needed
            };
        }

        return new ContainerLayout
        {
            TotalSize = totalSize,
            HeaderTemplate = headerTemplate,
            Entries = entries
        };
    }

    /// <summary>
    /// Reads exactly the requested number of bytes from a stream.
    /// </summary>
    private static bool TryReadExact(Stream stream, Span<byte> buffer)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int bytesRead = stream.Read(buffer.Slice(totalRead));
            if (bytesRead == 0)
                return false;
            totalRead += bytesRead;
        }
        return true;
    }
}
