namespace PatchSync.Common.Signatures;

public record struct SignatureChunk(uint rollingHash, ulong hash)
{
    public uint RollingHash = rollingHash;

    public ulong Hash = hash;
}
