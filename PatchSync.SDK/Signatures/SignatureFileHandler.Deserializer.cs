using System.Buffers.Binary;
#if NET6_0_OR_GREATER
using System.Runtime.CompilerServices;
#endif
using System.Runtime.InteropServices;
using PatchSync.Common.Signatures;

namespace PatchSync.SDK.Signatures;

#if NET6_0_OR_GREATER
[SkipLocalsInit]
#endif
public static partial class SignatureFileHandler
{
  public static SignatureFile LoadSignature(int originalFileSize, Stream stream)
  {
#if NETSTANDARD2_1_OR_GREATER
    Span<byte> stack = stackalloc byte[4];
    if (stream.Read(stack) != 4)
#else
    var stack = new byte[4];
    if (stream.Read(stack, 0, 4) != 4)
#endif
    {
      throw new InvalidOperationException("Could not read chunk size");
    }

    var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(stack);
    var chunkCount = Math.DivRem(originalFileSize, chunkSize, out var lastChunkSize);
    if (lastChunkSize > 0)
    {
      chunkCount++;
    }
    
    if (chunkCount < 1)
    {
      throw new InvalidOperationException("File size is too small for signature.");
    }

    var chunkSignatureSize = Marshal.SizeOf(typeof(SignatureChunk));

#if NETSTANDARD2_1_OR_GREATER
    Span<byte> chunk = stackalloc byte[chunkSignatureSize];
#else
    var chunk = new byte[chunkSignatureSize];
#endif
    
    var chunks = new SignatureChunk[chunkCount];
    for (var i = 0; i < chunkCount; i++)
    {
#if NETSTANDARD2_1_OR_GREATER
      if (stream.Read(chunk) != chunk.Length)
#else
      if (stream.Read(chunk, 0, chunkSignatureSize) != chunk.Length)
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
    
    return new SignatureFile(chunkSize, chunks);
  }
}