using System.Diagnostics;
using System.Text.Json;
using PatchSync.Common.Manifest;
using PatchSync.Common.Signatures;
using PatchSync.SDK.Signatures;

namespace PatchSync.SDK.Client;

public class PatchSyncClient(string baseUri)
{
    public PatchManifest Manifest { get; private set; }

    private async Task<Stream> DownloadFile(string relativeUri)
    {
        using var httpClient = HttpHandler.CreateHttpClient();
        var url = new Uri($"{baseUri}/{relativeUri}");
        var response = await httpClient.GetAsync(url);

        // Throw if not successful
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStreamAsync();
    }

    public async Task<PatchManifest> GetPatchManifest(string relativeUri)
    {
        var stream = await DownloadFile(relativeUri);
        Manifest = await JsonSerializer.DeserializeAsync<PatchManifest>(stream, JsonSerializerOptions.Default) ??
                   throw new InvalidOperationException("Could not properly download the manifest.");

#if NETSTANDARD2_1_OR_GREATER
        await stream.DisposeAsync();
#else
        stream.Dispose();
#endif
        return Manifest;
    }

    public async Task<SignatureFile> GetSignatureFile(int originalFileSize, string relativeUri, int chunkSize)
    {
        var stream = await DownloadFile(relativeUri);
        var signatureFile = SignatureFileHandler.LoadSignature(originalFileSize, stream, chunkSize);

#if NETSTANDARD2_1_OR_GREATER
        await stream.DisposeAsync();
#else
        stream.Dispose();
#endif
        return signatureFile;
    }

    public async IAsyncEnumerable<Stream> DownloadDeltaPatchFileAsync(
        string relativeUri,
        IEnumerable<(long Start, long End)> ranges,
        IProgress<IDownloadProgress> progress = null
    )
    {
        long totalSize = 0;
        using var httpClient = HttpHandler.CreateHttpClient();
        var url = new Uri($"{baseUri}/{relativeUri}");

        // Check if the server supports multi-part ranges
        var headResponse = await httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
        if (!headResponse.Headers.AcceptRanges.Contains("bytes"))
        {
            throw new InvalidOperationException("Server does not support multi-part ranges.");
        }

        httpClient.DefaultRequestHeaders.Range = new System.Net.Http.Headers.RangeHeaderValue();
        foreach (var range in ranges)
        {
            totalSize += range.End - range.Start + 1;
            httpClient.DefaultRequestHeaders.Range.Ranges.Add(
                new System.Net.Http.Headers.RangeItemHeaderValue(range.Start, range.End)
            );
        }

        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

        // Throw if not successful
        response.EnsureSuccessStatusCode();

        var progressInfo = new ProgressInfo(totalSize);

        if (response.Content.Headers.ContentType?.MediaType.Equals(
                "multipart/byteranges", StringComparison.OrdinalIgnoreCase
            ) == true)
        {
            var memoryStream = new MemoryStream();

            if (response.Content is MultipartContent multipart)
            {
                foreach (var content in multipart)
                {
                    await CopyToStreamAsync(content, memoryStream, progressInfo, progress);
                    memoryStream.Seek(0, SeekOrigin.Begin); // Reset memory stream position for reading
                    yield return memoryStream;
                    memoryStream.SetLength(0); // Clear memory stream for next part
                }
            }
        }
        else
        {
            yield return await response.Content.ReadAsStreamAsync();
        }
    }

    private static async Task CopyToStreamAsync(HttpContent content, Stream destination, ProgressInfo progressInfo, IProgress<IDownloadProgress> progress)
    {
#if NET6_0_OR_GREATER
        byte[] buffer = GC.AllocateUninitializedArray<byte>(81920);
#else
        byte[] buffer = new byte[81920];
#endif

        using var stream = await content.ReadAsStreamAsync();
        progressInfo.Stopwatch.Start();

        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) != 0)
        {
            await destination.WriteAsync(buffer, 0, bytesRead);

            double speed = 0;
            if (progressInfo.Stopwatch.Elapsed.TotalSeconds > 0)
            {
                speed = progressInfo.TotalRead / progressInfo.Stopwatch.Elapsed.TotalSeconds;
            }

            progress?.Report(new DownloadProgress(progressInfo.TotalSize, progressInfo.TotalRead, speed));
        }
    }

    public void ValidateFiles(string baseFolder, Func<ValidationResult> callback) =>
        FileValidator.ValidateFiles(Manifest, baseFolder, callback);

    private class ProgressInfo(long totalSize)
    {
        public long TotalSize { get; } = totalSize;
        public long TotalRead { get; set; }
        public Stopwatch Stopwatch { get; } = new();
    }

    private class DownloadProgress(long totalSize, long totalDownloaded, double speed) : IDownloadProgress
    {
        public long TotalSize { get; } = totalSize;
        public long TotalDownloaded { get; } = totalDownloaded;
        public double Speed { get; } = speed;
    }
}
