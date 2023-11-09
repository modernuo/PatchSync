namespace PatchSync.Common.Signatures;

public struct SignatureChunk(uint rollingHash, ulong hash)
{
  public uint RollingHash = rollingHash;

  public ulong Hash = hash;
}