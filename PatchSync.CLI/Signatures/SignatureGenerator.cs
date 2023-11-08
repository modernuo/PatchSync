using System.Buffers.Binary;
using System.IO.Hashing;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using PatchSync.Hashing;
using Spectre.Console;

namespace PatchSync.Signatures;

[SkipLocalsInit]
public class SignatureGenerator
{
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static int RoundUp(int x) => (x + 7) & -8;

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static int GetDefaultChunkSize(long length) => Math.Clamp(RoundUp((int)Math.Sqrt(length)), 704, 65528);

  public static ulong GenerateSignature(string filePath, string signaturePath, int chunkSize, ProgressTask? task)
  {
    Span<byte> chunk = stackalloc byte[chunkSize];
    using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open);
    using var mmStream = mmf.CreateViewStream();
    
    var fi = new FileInfo(filePath);
    var fileSize = fi.Length;
    chunkSize = chunkSize == 0 ? GetDefaultChunkSize(fileSize) : RoundUp(chunkSize);
    
    task.MaxValue(fileSize);
    
    var hasher = new XxHash3();
    using var stream = File.Open(signaturePath, FileMode.Create);
    using var writer = new BinaryWriter(stream);
    writer.Write((ushort)chunkSize);
    
    hasher.Append(mmStream);
    if (!hasher.TryGetHashAndReset(chunk, out var bytesWritten) || bytesWritten != hasher.HashLengthInBytes)
    {
      throw new InvalidOperationException("Could not hash file.");
    }

    var totalHash = BinaryPrimitives.ReadUInt64BigEndian(chunk[..bytesWritten]);
    
    // Reset
    mmStream.Seek(0, SeekOrigin.Begin);
    
    var chunkCount = Math.DivRem(fileSize, chunkSize, out var lastChunkSize);

    for (var i = 0; i < chunkCount; i++)
    {
      var bytes = mmStream.Read(chunk);
      if (bytes != chunk.Length)
      {
        throw new Exception($"Failed to read entire chunk ({bytes})");
      }
      
      WriteChunk(chunk, hasher, writer);
      task.Increment(chunkSize);
    }

    if (lastChunkSize > 0)
    {
      var bytes = mmStream.Read(chunk);
      if (bytes != lastChunkSize)
      {
        throw new Exception($"Failed to read entire chunk ({bytes})");
      }
      
      WriteChunk(chunk[..(int)lastChunkSize], hasher, writer);
      task.Increment(lastChunkSize);
    }
    
    task.StopTask();

    return totalHash;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static void WriteChunk(Span<byte> chunk, XxHash3 hash, BinaryWriter writer)
  {
    if (chunk.Length <= 12)
    {
        writer.Write(chunk);
        return;
    }
    
    // Write rolling checksum
    writer.Write(Adler32RollingChecksum.Calculate(chunk));
    
    hash.Append(chunk);
    var result = hash.GetCurrentHashAsUInt64();
    hash.Reset();
    
    // Write hash
    writer.Write(result);
  }
}