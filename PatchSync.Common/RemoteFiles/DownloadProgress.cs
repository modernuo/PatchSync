using PatchSync.SDK.Client;

namespace PatchSync.Common;

public class DownloadProgress(string uri, long totalSize, long totalDownloaded, double speed) : IDownloadProgress
{
    public string Uri { get; } = uri;
    public long TotalSize { get; } = totalSize;
    public long TotalDownloaded { get; } = totalDownloaded;
    public double Speed { get; } = speed;
}
