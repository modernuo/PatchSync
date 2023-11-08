namespace PatchSync.SDK.Client;

public interface IDownloadProgress
{
    public long TotalSize { get; }
    public long TotalDownloaded { get; }
    public double Speed { get; }
}
