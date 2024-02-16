using System.Diagnostics;

namespace PatchSync.Common;

public static class Downloader
{
    public static async Task DownloadFileAsync(
        string fileName,
        Uri url, Stream destination, IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        using var httpClient = HttpHandler.CreateHttpClient();
        var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        // Throw if not successful
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength;

        using var download = await response.Content.ReadAsStreamAsync(cancellationToken);
        // Ignore progress reporting when no progress reporter was
        // passed or when the content length is unknown
        if (progress == null || contentLength == null) {
            await download.CopyToAsync(destination);
            return;
        }

        var progressInfo = new ProgressInfo((long)contentLength);

        await CopyToStreamAsync(fileName, download, destination, progressInfo, progress, cancellationToken);
    }

    private static async Task CopyToStreamAsync(
        string fileName, Stream stream, Stream destination, ProgressInfo progressInfo, IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken
    )
    {
#if NET6_0_OR_GREATER
        byte[] buffer = GC.AllocateUninitializedArray<byte>(81920);
#else
        byte[] buffer = new byte[81920];
#endif

        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) != 0)
        {
            await destination.WriteAsync(buffer, 0, bytesRead, cancellationToken);
            progressInfo.TotalRead += bytesRead;

            if (!progressInfo.Stopwatch.IsRunning)
            {
                progressInfo.Stopwatch.Start();
            }

            double speed = progressInfo.TotalRead / progressInfo.Stopwatch.Elapsed.TotalSeconds;
            progress?.Report(new DownloadProgress(fileName, progressInfo.TotalSize, progressInfo.TotalRead, speed));
        }
    }

    private class ProgressInfo(long totalSize)
    {
        public string FileName { get; set; }
        public long TotalSize { get; } = totalSize;
        public long TotalRead { get; set; }
        public Stopwatch Stopwatch { get; } = new();
    }
}
