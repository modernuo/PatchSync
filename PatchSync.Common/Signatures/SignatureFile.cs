namespace PatchSync.Common.Signatures;

public class SignatureFile(int chunkSize, SignatureChunk[] chunks, byte[]? remainingData = null)
{
    private static readonly byte[] _emptyRemainingData = Array.Empty<byte>();

    public int ChunkSize { get; } = chunkSize;
    public SignatureChunk[] Chunks { get; } = chunks;

    // We can't get around this allocation if we use type-safe classes
    // For high performance situations, just stream directly to a file/memory/etc
    public byte[] RemainingData { get; } = remainingData ?? _emptyRemainingData;
}
