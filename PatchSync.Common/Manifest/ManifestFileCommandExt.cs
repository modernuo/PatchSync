using System.Runtime.CompilerServices;

namespace PatchSync.Common.Manifest;

public static class ManifestFileCommandExt
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string GetIcon(this ManifestFileCommand command)
    {
        return command switch
        {
            ManifestFileCommand.DeltaUpdate              => ":writing_hand:",
            ManifestFileCommand.AlwaysFullUpdate         => ":page_facing_up:",
            ManifestFileCommand.UpdateIfMissing          => ":plus:",
            ManifestFileCommand.NeverUpdate              => ":prohibited:",
            ManifestFileCommand.Delete                   => ":wastebasket:",
            ManifestFileCommand.UpdateIfFullHashMismatch => ":computer_disk:",
            _                                            => throw new ArgumentOutOfRangeException(nameof(command))
        };
    }
}
