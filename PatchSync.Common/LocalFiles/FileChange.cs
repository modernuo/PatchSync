namespace PatchSync.Common.LocalFiles;

public enum FileChange
{
    None,
    Create,
    FullUpdate,
    DeltaUpdate,
    Delete
}
