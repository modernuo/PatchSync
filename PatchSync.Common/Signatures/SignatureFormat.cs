using System.Buffers.Binary;
using PatchSync.Common.Chunking;

namespace PatchSync.Common.Signatures;

/// <summary>
/// PSI1 (PatchSync Index v1) signature file format.
///
/// Binary format:
/// [Magic: 4 bytes]       "PSI1" (0x50534931 big-endian)
/// [Algorithm: 1 byte]    Algorithm ID enum value
/// [Version: 1 byte]      Algorithm version for forward compat
/// [MinSize: 4 bytes]     Minimum chunk size (little-endian)
/// [AvgSize: 4 bytes]     Average chunk size (little-endian)
/// [MaxSize: 4 bytes]     Maximum chunk size (little-endian)
/// [ChunkCount: 4 bytes]  Number of chunks (little-endian)
/// [Chunks...]            Each: [Offset:8][Length:4][Hash:32] = 44 bytes
///
/// Total header: 22 bytes
/// Per chunk: 44 bytes
/// </summary>
public static class SignatureFormat
{
    /// <summary>
    /// Magic bytes "PSI1" for format identification.
    /// </summary>
    public const uint Magic = 0x50534931; // "PSI1" as big-endian uint32

    /// <summary>
    /// Current format version.
    /// </summary>
    public const byte CurrentVersion = 1;

    /// <summary>
    /// Header size in bytes.
    /// </summary>
    public const int HeaderSize = 22;

    /// <summary>
    /// Size of each chunk entry in bytes.
    /// </summary>
    public const int ChunkEntrySize = 44; // 8 (offset) + 4 (length) + 32 (hash)

    /// <summary>
    /// Hash size in bytes (SHA256).
    /// </summary>
    public const int HashSize = 32;
}

/// <summary>
/// Algorithm identifiers for signature files.
/// These must remain stable for backwards compatibility.
/// </summary>
public enum SignatureAlgorithm : byte
{
    /// <summary>Unknown or unsupported algorithm.</summary>
    Unknown = 0,

    /// <summary>FastCDC v1 (gear hash, normalized chunking).</summary>
    FastCDC_V1 = 1,

    /// <summary>Buzhash (casync-compatible, rolling window).</summary>
    Buzhash_Casync_V1 = 2,

    // Future algorithms:
    // SeqCDC_V1 = 3,
    // VRAM_V1 = 4,
}

/// <summary>
/// Extension methods for algorithm ID conversion.
/// </summary>
public static class SignatureAlgorithmExtensions
{
    /// <summary>
    /// Converts algorithm ID string to enum.
    /// </summary>
    public static SignatureAlgorithm ToSignatureAlgorithm(this string algorithmId)
    {
        return algorithmId switch
        {
            "fastcdc-v1" => SignatureAlgorithm.FastCDC_V1,
            "buzhash-casync-v1" => SignatureAlgorithm.Buzhash_Casync_V1,
            _ => SignatureAlgorithm.Unknown
        };
    }

    /// <summary>
    /// Converts enum to algorithm ID string.
    /// </summary>
    public static string ToAlgorithmId(this SignatureAlgorithm algorithm)
    {
        return algorithm switch
        {
            SignatureAlgorithm.FastCDC_V1 => "fastcdc-v1",
            SignatureAlgorithm.Buzhash_Casync_V1 => "buzhash-casync-v1",
            _ => throw new NotSupportedException($"Unknown algorithm: {algorithm}")
        };
    }
}

/// <summary>
/// Represents a signature file containing chunk boundaries and hashes.
/// </summary>
public sealed class SignatureFile
{
    /// <summary>Algorithm used to generate this signature.</summary>
    public required string AlgorithmId { get; init; }

    /// <summary>Algorithm version.</summary>
    public byte AlgorithmVersion { get; init; } = SignatureFormat.CurrentVersion;

    /// <summary>Minimum chunk size used.</summary>
    public required int MinSize { get; init; }

    /// <summary>Average chunk size used.</summary>
    public required int AverageSize { get; init; }

    /// <summary>Maximum chunk size used.</summary>
    public required int MaxSize { get; init; }

    /// <summary>List of chunk signatures.</summary>
    public required IReadOnlyList<SignatureChunk> Chunks { get; init; }

    /// <summary>
    /// Total file size (sum of all chunk lengths).
    /// </summary>
    public long TotalSize => Chunks.Sum(c => (long)c.Length);

    /// <summary>
    /// Writes the signature file to a stream.
    /// </summary>
    public void Write(Stream output)
    {
        Span<byte> header = stackalloc byte[SignatureFormat.HeaderSize];

        // Magic (big-endian for readability in hex editors)
        BinaryPrimitives.WriteUInt32BigEndian(header[0..4], SignatureFormat.Magic);

        // Algorithm and version
        header[4] = (byte)AlgorithmId.ToSignatureAlgorithm();
        header[5] = AlgorithmVersion;

        // Chunk size parameters (little-endian)
        BinaryPrimitives.WriteInt32LittleEndian(header[6..10], MinSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[10..14], AverageSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[14..18], MaxSize);

        // Chunk count
        BinaryPrimitives.WriteInt32LittleEndian(header[18..22], Chunks.Count);

        output.Write(header);

        // Write chunks
        Span<byte> chunkBuffer = stackalloc byte[SignatureFormat.ChunkEntrySize];
        foreach (var chunk in Chunks)
        {
            BinaryPrimitives.WriteInt64LittleEndian(chunkBuffer[0..8], chunk.Offset);
            BinaryPrimitives.WriteInt32LittleEndian(chunkBuffer[8..12], chunk.Length);
            chunk.Hash.AsSpan().CopyTo(chunkBuffer[12..44]);
            output.Write(chunkBuffer);
        }
    }

    /// <summary>
    /// Reads a signature file from a stream.
    /// </summary>
    public static SignatureFile Read(Stream input)
    {
        Span<byte> header = stackalloc byte[SignatureFormat.HeaderSize];
        if (!TryReadExact(input, header))
        {
            throw new InvalidDataException("Signature file too short to contain header");
        }

        // Verify magic
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(header[0..4]);
        if (magic != SignatureFormat.Magic)
        {
            throw new InvalidDataException(
                $"Invalid signature file magic: expected 0x{SignatureFormat.Magic:X8}, got 0x{magic:X8}");
        }

        // Read algorithm
        var algorithm = (SignatureAlgorithm)header[4];
        if (algorithm == SignatureAlgorithm.Unknown)
        {
            throw new NotSupportedException("Unknown chunking algorithm in signature file");
        }

        byte version = header[5];

        // Read parameters
        int minSize = BinaryPrimitives.ReadInt32LittleEndian(header[6..10]);
        int avgSize = BinaryPrimitives.ReadInt32LittleEndian(header[10..14]);
        int maxSize = BinaryPrimitives.ReadInt32LittleEndian(header[14..18]);
        int chunkCount = BinaryPrimitives.ReadInt32LittleEndian(header[18..22]);

        if (chunkCount < 0)
        {
            throw new InvalidDataException($"Invalid chunk count: {chunkCount}");
        }

        // Read chunks
        var chunks = new SignatureChunk[chunkCount];
        Span<byte> chunkBuffer = stackalloc byte[SignatureFormat.ChunkEntrySize];

        for (int i = 0; i < chunkCount; i++)
        {
            if (!TryReadExact(input, chunkBuffer))
            {
                throw new InvalidDataException($"Signature file truncated at chunk {i}");
            }

            long offset = BinaryPrimitives.ReadInt64LittleEndian(chunkBuffer[0..8]);
            int length = BinaryPrimitives.ReadInt32LittleEndian(chunkBuffer[8..12]);
            byte[] hash = chunkBuffer[12..44].ToArray();

            chunks[i] = new SignatureChunk(offset, length, hash);
        }

        return new SignatureFile
        {
            AlgorithmId = algorithm.ToAlgorithmId(),
            AlgorithmVersion = version,
            MinSize = minSize,
            AverageSize = avgSize,
            MaxSize = maxSize,
            Chunks = chunks
        };
    }

    /// <summary>
    /// Creates a signature file from chunking results.
    /// </summary>
    public static SignatureFile Create(
        string algorithmId,
        ChunkingOptions options,
        IEnumerable<ChunkBoundary> chunks)
    {
        return new SignatureFile
        {
            AlgorithmId = algorithmId,
            AlgorithmVersion = SignatureFormat.CurrentVersion,
            MinSize = options.MinSize,
            AverageSize = options.AverageSize,
            MaxSize = options.MaxSize,
            Chunks = chunks.Select(c => new SignatureChunk(c.Offset, c.Length, c.Hash)).ToList()
        };
    }

    /// <summary>
    /// Gets the chunking options from this signature file.
    /// </summary>
    public ChunkingOptions GetChunkingOptions()
    {
        return new ChunkingOptions
        {
            MinSize = MinSize,
            AverageSize = AverageSize,
            MaxSize = MaxSize
        };
    }

    /// <summary>
    /// Reads exactly the requested number of bytes from a stream.
    /// Network streams may return fewer bytes than requested per Read() call.
    /// </summary>
    private static bool TryReadExact(Stream stream, Span<byte> buffer)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int bytesRead = stream.Read(buffer.Slice(totalRead));
            if (bytesRead == 0)
                return false; // End of stream before buffer filled
            totalRead += bytesRead;
        }
        return true;
    }
}

/// <summary>
/// Represents a single chunk in a signature file.
/// </summary>
/// <param name="Offset">Byte offset from start of file.</param>
/// <param name="Length">Length of chunk in bytes.</param>
/// <param name="Hash">SHA256 hash of chunk data (32 bytes).</param>
public readonly record struct SignatureChunk(long Offset, int Length, byte[] Hash)
{
    /// <summary>
    /// End offset (exclusive) of this chunk.
    /// </summary>
    public long End => Offset + Length;

    /// <summary>
    /// Returns the hash as a hex string.
    /// </summary>
    public string HashHex => Convert.ToHexString(Hash);
}
