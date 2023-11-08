using System.Runtime.CompilerServices;

namespace PatchSync.Common.Text;

public static class HexStringConverter
{
    private static readonly uint[] _lookup32Chars = CreateLookup32Chars();

    private static uint[] CreateLookup32Chars()
    {
        var result = new uint[256];
        for (var i = 0; i < 256; i++)
        {
            var s = i.ToString("X2");
            if (BitConverter.IsLittleEndian)
            {
                result[i] = s[0] + ((uint)s[1] << 16);
            }
            else
            {
                result[i] = s[1] + ((uint)s[0] << 16);
            }
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ToHexString(this byte[] bytes) => new ReadOnlySpan<byte>(bytes).ToHexString();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ToHexString(this Span<byte> bytes) => ((ReadOnlySpan<byte>)bytes).ToHexString();

    public static unsafe string ToHexString(this ReadOnlySpan<byte> bytes)
    {
        var result = new string((char)0, bytes.Length * 2);
        fixed (char* resultP = result)
        {
            var resultP2 = (uint*)resultP;
            for (var i = 0; i < bytes.Length; i++)
            {
                resultP2[i] = _lookup32Chars[bytes[i]];
            }
        }

        return result;
    }

    public static unsafe void GetBytes(this string str, Span<byte> bytes)
    {
        fixed (char* strP = str)
        {
            var i = 0;
            var j = 0;
            while (i < str.Length)
            {
                int chr1 = strP[i++];
                int chr2 = strP[i++];
                if (BitConverter.IsLittleEndian)
                {
                    bytes[j++] = (byte)(((chr1 - (chr1 >= 65 ? 55 : 48)) << 4) | (chr2 - (chr2 >= 65 ? 55 : 48)));
                }
                else
                {
                    bytes[j++] = (byte)((chr1 - (chr1 >= 65 ? 55 : 48)) | ((chr2 - (chr2 >= 65 ? 55 : 48)) << 4));
                }
            }
        }
    }
}
