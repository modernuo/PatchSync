using System.Buffers.Binary;
using System.Runtime.InteropServices;
using PatchSync.Common.Signatures;
#if NET6_0_OR_GREATER
using System.Runtime.CompilerServices;
#endif

namespace PatchSync.SDK.Signatures;

#if NET6_0_OR_GREATER
[SkipLocalsInit]
#endif
public static partial class SignatureFileHandler
{
    public static SignatureFile LoadSignature(long originalFileSize, Stream stream, int chunkSize)
    {
        var chunkCount = Math.DivRem(originalFileSize, chunkSize, out var lastChunkSize);

        if (chunkCount < 1)
        {
            throw new InvalidOperationException("File size is too small for a signature file.");
        }

#if NETSTANDARD2_1_OR_GREATER
        Span<byte> chunk = stackalloc byte[12];
#else
        var chunk = new byte[12];
#endif

        var chunks = new SignatureChunk[chunkCount];
        for (var i = 0; i < chunkCount; i++)
        {
#if NETSTANDARD2_1_OR_GREATER
            if (stream.Read(chunk) != chunk.Length)
#else
            if (stream.Read(chunk, 0, 12) != chunk.Length)
#endif
            {
                throw new InvalidOperationException("Reached end of stream prematurely");
            }

            chunks[i] = new SignatureChunk
            {
                RollingHash = BinaryPrimitives.ReadUInt32LittleEndian(chunk),
#if NETSTANDARD2_1_OR_GREATER
                Hash = BinaryPrimitives.ReadUInt64LittleEndian(chunk[4..])
#else
                Hash = BinaryPrimitives.ReadUInt64LittleEndian(chunk.AsSpan(4))
#endif
            };
        }

        // Last remaining data
        if (lastChunkSize == 0)
        {
            return new SignatureFile(chunkSize, chunks);
        }

        var remainingChunk = new byte[lastChunkSize];
        if (stream.Read(remainingChunk, 0, (int)lastChunkSize) != lastChunkSize)
        {
            throw new InvalidOperationException("Reached end of stream prematurely");
        }

        return new SignatureFile(chunkSize, chunks, remainingChunk);
    }
}
