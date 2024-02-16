using System.Runtime.CompilerServices;

namespace PatchSync.CLI;

public static class Utilities
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReplaceAny(
        this ReadOnlySpan<char> chars, ReadOnlySpan<char> invalidChars, char replacementChar, Span<char> dest
    )
    {
        while (true)
        {
            var indexOf = chars.IndexOfAny(invalidChars);
            if (indexOf == -1)
            {
                chars.CopyTo(dest);
                break;
            }

            chars[..indexOf].CopyTo(dest);
            dest[indexOf] = '-';

            chars = chars[(indexOf + 1)..];
            dest = dest[(indexOf + 1)..];
        }
    }
}
