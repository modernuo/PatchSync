using System.Runtime.CompilerServices;

namespace PatchSync.Common;

public class Utilities
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long RoundUp(long x) => (x + 7) & -8;
}
