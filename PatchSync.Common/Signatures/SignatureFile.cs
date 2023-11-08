using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace PatchSync.Common.Signatures;

public class SignatureFile
{
  private int _chunkSize;
  private SignatureChunk[] _chunks;

  private SignatureFile(int chunkSize, SignatureChunk[] chunks)
  {
    _chunkSize = chunkSize;
    _chunks = chunks;
  }
  
  public static SignatureFile Deserialize(int originalFileSize, Stream stream)
  {
    Span<byte> stack = stackalloc byte[4];
    if (stream.Read(stack) != 4)
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

    Span<byte> chunk = stackalloc byte[Marshal.SizeOf(typeof(SignatureChunk))];
    var chunks = new SignatureChunk[chunkCount + (lastChunkSize > 0 ? 1 : 0)];
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
    
    return new SignatureFile(chunkSize, chunks);
  }
}