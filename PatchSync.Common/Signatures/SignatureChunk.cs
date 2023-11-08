using System.Runtime.InteropServices;

namespace PatchSync.Common.Signatures;

[StructLayout(LayoutKind.Sequential)]
public struct SignatureChunk
{
  public uint RollingHash;
  public ulong Hash;
}