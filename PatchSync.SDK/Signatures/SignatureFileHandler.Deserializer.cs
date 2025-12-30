using System.Buffers.Binary;
using PatchSync.Common.Signatures;
using System.Runtime.CompilerServices;

namespace PatchSync.SDK.Signatures;

[SkipLocalsInit]
public static partial class SignatureFileHandler
{
    public static SignatureFile LoadSignature(long originalFileSize, Stream stream, int chunkSize)
    {
        var chunkCount = Math.DivRem(originalFileSize, chunkSize, out var lastChunkSize);

        if (chunkCount < 1)
        {
            throw new InvalidOperationException("File size is too small for a signature file.");
        }

        Span<byte> chunk = stackalloc byte[12];

        var chunks = new SignatureChunk[chunkCount];
        for (var i = 0; i < chunkCount; i++)
        {
            if (stream.Read(chunk) != chunk.Length)
            {
                throw new InvalidOperationException("Reached end of stream prematurely");
            }

            chunks[i] = new SignatureChunk
            {
                RollingHash = BinaryPrimitives.ReadUInt32LittleEndian(chunk),
                Hash = BinaryPrimitives.ReadUInt64LittleEndian(chunk[4..])
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
