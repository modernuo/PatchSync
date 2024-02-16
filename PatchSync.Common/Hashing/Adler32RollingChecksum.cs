namespace PatchSync.Common.Hashing;

public static class Adler32RollingChecksum
{
    public static uint Calculate(ReadOnlySpan<byte> block)
    {
        var a = 1;
        var b = 0;
        for (var i = 0; i < block.Length; i++)
        {
            var z = block[i];
            a = (ushort)(z + a);
            b = (ushort)(b + a);
        }

        return (uint)((b << 16) | a);
    }

    public static uint Rotate(uint checksum, byte remove, byte add, int chunkSize)
    {
        var b = (ushort)((checksum >> 16) & 0xffff);
        var a = (ushort)(checksum & 0xffff);

        a = (ushort)(a - remove + add);
        b = (ushort)(b - chunkSize * remove + a - 1);

        return (uint)((b << 16) | a);
    }
}
