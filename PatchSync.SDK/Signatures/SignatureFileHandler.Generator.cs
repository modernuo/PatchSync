using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using PatchSync.Common;
using PatchSync.Common.Hashing;
using PatchSync.Common.Signatures;

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
        Action<FileProcessingResult>? callback = null
    )
    {
        var length = source.Length;

        var processingResult = new FileProcessingResult();
        processingResult.Started(length);
        callback?.Invoke(processingResult);

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
            processingResult.InProgress(chunkSize);
            callback?.Invoke(processingResult);
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

            processingResult.InProgress(chunkSize);
            callback?.Invoke(processingResult);
        }

        processingResult.Completed();
        callback?.Invoke(processingResult);

        return new SignatureFile(chunkSize, chunks, remainingData);
    }

    public static SignatureFile GenerateSignature(
        Stream stream,
        int chunkSize = -1,
        Action<FileProcessingResult>? callback = null
    )
    {
        var length = stream.Length;
        var processingResult = new FileProcessingResult();
        processingResult.Started(length);
        callback?.Invoke(processingResult);

        var hasher = new XxHash3();
        chunkSize = chunkSize <= 0 ? GetDefaultChunkSize(length) : RoundUp(chunkSize);
        var chunk = new byte[chunkSize];

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
            processingResult.InProgress(chunk.Length);
            callback?.Invoke(processingResult);
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

            processingResult.InProgress(lastChunkSize);
            callback?.Invoke(processingResult);
        }

        processingResult.Completed();
        callback?.Invoke(processingResult);

        return new SignatureFile(chunkSize, chunks, remainingData);
    }

    public static (ulong, byte[]) CreateSignatureFile(
        Stream inputStream,
        Stream outputStream,
        int chunkSize = -1,
        Action<FileProcessingResult>? callback = null
    )
    {
        var length = inputStream.Length;
        var processingResult = new FileProcessingResult();
        processingResult.Started(length);

        callback?.Invoke(processingResult);

        using var binaryWriter = new BinaryWriter(outputStream);

        using var fullHasher = SHA256.Create();
        var fastHasher = new XxHash3();
        var hasher = new XxHash3();
        chunkSize = chunkSize <= 0 ? GetDefaultChunkSize(length) : RoundUp(chunkSize);

        var chunk = new byte[chunkSize];

        var chunkCount = Math.DivRem(length, chunkSize, out var lastChunkSize);

        for (var i = 0; i < chunkCount; i++)
        {
            if (inputStream.Read(chunk, 0, chunkSize) != chunk.Length)
            {
                throw new InvalidOperationException("Reached end of stream prematurely");
            }

            WriteChunk(chunk, chunkSize, hasher, binaryWriter);

            fastHasher.Append(chunk);
            fullHasher.TransformBlock(chunk, 0, chunkSize, null, 0);

            processingResult.InProgress(chunkSize);
            callback?.Invoke(processingResult);
        }

        if (lastChunkSize > 0)
        {
            if (inputStream.Read(chunk, 0, (int)lastChunkSize) != lastChunkSize)
            {
                throw new InvalidOperationException("Reached end of stream prematurely");
            }

            binaryWriter.Write(chunk, 0, (int)lastChunkSize);

            fastHasher.Append(chunk.AsSpan(0, (int)lastChunkSize));
            fullHasher.TransformFinalBlock(chunk, 0, (int)lastChunkSize);

            processingResult.InProgress(lastChunkSize);
            callback?.Invoke(processingResult);
        }
        else
        {
            fullHasher.TransformFinalBlock(chunk, 0, 0);
        }

        processingResult.Completed();
        callback?.Invoke(processingResult);

        return (fastHasher.GetCurrentHashAsUInt64(), fullHasher.Hash);
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
    private static void WriteChunk(byte[] chunk, int length, XxHash3 hash, BinaryWriter binaryWriter)
    {
        if (chunk.Length <= 12)
        {
            binaryWriter.Write(chunk, 0, length);
            return;
        }

        // Write rolling checksum
        var adlerChecksum = Adler32RollingChecksum.Calculate(chunk.AsSpan(0, length));
        binaryWriter.Write(adlerChecksum);

        hash.Append(chunk);

        // Write hash
        binaryWriter.Write(hash.GetCurrentHashAsUInt64());

        hash.Reset();
    }
}
