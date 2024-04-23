namespace PatchSync.SDK.Client;

public interface IDownloadProgress
{
    public string Uri { get; }
    public long TotalSize { get; }
    public long TotalDownloaded { get; }
    public double Speed { get; }
}
