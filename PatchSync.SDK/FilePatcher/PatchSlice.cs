namespace PatchSync.SDK;

public record PatchSlice
{
    public PatchSliceLocation Location { get; }
    public long Offset { get; }

    public PatchSlice(PatchSliceLocation location, long offset)
    {
        Location = location;
        Offset = offset;
    }
}
