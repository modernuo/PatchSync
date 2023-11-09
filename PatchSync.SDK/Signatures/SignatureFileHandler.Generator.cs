using System.IO.Hashing;
using System.Runtime.CompilerServices;
using PatchSync.Common.Signatures;
using PatchSync.Hashing;

namespace PatchSync.SDK.Signatures;

public static partial class SignatureFileHandler
{
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static int RoundUp(int x) => (x + 7) & -8;

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static int GetDefaultChunkSize(long length) =>
#if NET6_0_OR_GREATER
    Math.Clamp(RoundUp((int)Math.Sqrt(length)), 704, 65528);
#else
    Math.Min(Math.Max(RoundUp((int)Math.Sqrt(length)), 704), 65528);
#endif

  public static SignatureFile GenerateSignature(
    ReadOnlySpan<byte> source,
    int chunkSize = -1,
    Action<SignatureFileResult>? callback = null
  )
  {
    var length = source.Length;
    callback?.Invoke(SignatureFileResult.Started(length));
    
    var hasher = new XxHash3();
    chunkSize = chunkSize <= 0 ? GetDefaultChunkSize(length) : RoundUp(chunkSize);

    var chunkCount = Math.DivRem(length, chunkSize, out var lastChunkSize);
    var chunks = new SignatureChunk[chunkCount + (lastChunkSize >= 12 ? 1 : 0)];
    byte[]? remainingData = null;
    
    var pos = 0;
    for (var i = 0; i < chunkCount; i++)
    {
      var bytes = source.Slice(pos, chunkSize);
      pos += chunkSize;

      chunks[i] = GetChunk(bytes, hasher);
      callback?.Invoke(SignatureFileResult.InProgress(length, chunkSize));
    }

    if (lastChunkSize > 0)
    {
      var bytes = source.Slice(pos, lastChunkSize);

      if (lastChunkSize >= 12)
      {
        chunks[chunks.Length - 1] = GetChunk(bytes, hasher);
      }
      else
      {
        remainingData = bytes.ToArray();
      }
      callback?.Invoke(SignatureFileResult.InProgress(length, chunkSize));
    }
    
    callback?.Invoke(SignatureFileResult.Completed());

    return new SignatureFile(chunkSize, chunks, remainingData);
  }
  
  public static SignatureFile GenerateSignature(
    Stream stream,
    int chunkSize = -1,
    Action<SignatureFileResult>? callback = null
  )
  {
    var length = stream.Length;
    callback?.Invoke(SignatureFileResult.Started(length));
    
    var hasher = new XxHash3();
    chunkSize = chunkSize <= 0 ? GetDefaultChunkSize(length) : RoundUp(chunkSize);
#if NETSTANDARD2_1_OR_GREATER
    Span<byte> chunk = stackalloc byte[chunkSize];
#else
    var chunk = new byte[chunkSize];
#endif
    var chunkCount = Math.DivRem(length, chunkSize, out var lastChunkSize);
    var chunks = new SignatureChunk[chunkCount + (lastChunkSize >= 12 ? 1 : 0)];
    byte[]? remainingData = null;
    
    for (var i = 0; i < chunkCount; i++)
    {
#if NETSTANDARD2_1_OR_GREATER
      if (stream.Read(chunk) != chunkSize)
#else
      if (stream.Read(chunk, 0, chunkSize) != chunk.Length)
#endif
      {
        throw new InvalidOperationException("Reached end of stream prematurely");
      }

      chunks[i] = GetChunk(chunk, hasher);
      callback?.Invoke(SignatureFileResult.InProgress(length, stream.Position));
    }

    if (lastChunkSize > 0)
    {
#if NETSTANDARD2_1_OR_GREATER
      if (stream.Read(chunk) != chunkSize)
#else
      if (stream.Read(chunk, 0, (int)lastChunkSize) != lastChunkSize)
#endif
      {
        throw new InvalidOperationException("Reached end of stream prematurely");
      }

      if (lastChunkSize >= 12)
      {
        chunks[chunks.Length - 1] = GetChunk(chunk, hasher);
      }
      else
      {
#if NETSTANDARD2_1_OR_GREATER
        remainingData = chunk[..(int)lastChunkSize].ToArray();
#else
        remainingData = chunk.AsSpan(0, (int)lastChunkSize).ToArray();
#endif
      }
      
      callback?.Invoke(SignatureFileResult.InProgress(length, lastChunkSize));
    }
    
    callback?.Invoke(SignatureFileResult.Completed());

    return new SignatureFile(chunkSize, chunks, remainingData);
  }
  
  public static ulong CreateSignatureFile(
    Stream inputStream,
    Stream outputStream,
    int chunkSize = -1,
    Action<SignatureFileResult>? callback = null
  )
  {
    var length = inputStream.Length;
    callback?.Invoke(SignatureFileResult.Started(length));
    
    using var binaryWriter = new BinaryWriter(outputStream);

    var totalHasher = new XxHash3();
    var hasher = new XxHash3();
    chunkSize = chunkSize <= 0 ? GetDefaultChunkSize(length) : RoundUp(chunkSize);
#if NETSTANDARD2_1_OR_GREATER
    Span<byte> chunk = stackalloc byte[chunkSize];
#else
    var chunk = new byte[chunkSize];
#endif
    var chunkCount = Math.DivRem(length, chunkSize, out var lastChunkSize);
    
    for (var i = 0; i < chunkCount; i++)
    {
#if NETSTANDARD2_1_OR_GREATER
      if (inputStream.Read(chunk) != chunkSize)
#else
      if (inputStream.Read(chunk, 0, chunkSize) != chunk.Length)
#endif
      {
        throw new InvalidOperationException("Reached end of stream prematurely");
      }
      
#if NETSTANDARD2_1_OR_GREATER
        WriteChunk(chunk, hasher, binaryWriter);
#else
      WriteChunk(chunk, chunkSize, hasher, binaryWriter);
#endif
      
      totalHasher.Append(chunk);

      callback?.Invoke(SignatureFileResult.InProgress(length, chunkSize));
    }

    if (lastChunkSize > 0)
    {
#if NETSTANDARD2_1_OR_GREATER
      if (inputStream.Read(chunk) != chunkSize)
#else
      if (inputStream.Read(chunk, 0, (int)lastChunkSize) != lastChunkSize)
#endif
      {
        throw new InvalidOperationException("Reached end of stream prematurely");
      }

      if (lastChunkSize >= 12)
      {
#if NETSTANDARD2_1_OR_GREATER
        WriteChunk(chunk.Slice(0, (int)lastChunkSize), hasher, binaryWriter);
#else
        WriteChunk(chunk, (int)lastChunkSize, hasher, binaryWriter);
#endif
      }
      else
      {
#if NETSTANDARD2_1_OR_GREATER
        binaryWriter.Write(chunk.Slice(0, (int)lastChunkSize));
#else
        binaryWriter.Write(chunk, 0, (int)lastChunkSize);
#endif
      }
      
#if NETSTANDARD2_1_OR_GREATER
     totalHasher.Append(chunk.Slice(0, (int)lastChunkSize));
#else
      totalHasher.Append(chunk.AsSpan(0, (int)lastChunkSize));
#endif
      
      callback?.Invoke(SignatureFileResult.InProgress(length, lastChunkSize));
    }
    
    callback?.Invoke(SignatureFileResult.Completed());

    return totalHasher.GetCurrentHashAsUInt64();
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static SignatureChunk GetChunk(ReadOnlySpan<byte> bytes, XxHash3 hasher)
  {
    hasher.Append(bytes);
    var hash = hasher.GetCurrentHashAsUInt64();
    hasher.Reset();

    return new SignatureChunk(Adler32RollingChecksum.Calculate(bytes), hash);
  }
  
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if NETSTANDARD2_1_OR_GREATER
  private static void WriteChunk(Span<byte> chunk, XxHash3 hash, BinaryWriter binaryWriter)
#else
  private static void WriteChunk(byte[] chunk, int length, XxHash3 hash, BinaryWriter binaryWriter)
#endif
  {
    if (chunk.Length <= 12)
    {
#if NETSTANDARD2_1_OR_GREATER
      binaryWriter.Write(chunk);
#else
      binaryWriter.Write(chunk, 0, length);
#endif
      return;
    }
    
    // Write rolling checksum
#if NETSTANDARD2_1_OR_GREATER
    binaryWriter.Write(Adler32RollingChecksum.Calculate(chunk));
#else
    binaryWriter.Write(Adler32RollingChecksum.Calculate(chunk.AsSpan(0, length)));
#endif

    hash.Append(chunk);
    
    // Write hash
    binaryWriter.Write(hash.GetCurrentHashAsUInt64());
    
    hash.Reset();
  }
}