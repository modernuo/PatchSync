using System.Diagnostics;
using PatchSync.Common;
using PatchSync.Common.Manifest;
using PatchSync.Common.Signatures;
using PatchSync.SDK.Client;
using PatchSync.SDK.Signatures;

namespace PatchSync.SDK;

public partial class PatchSyncClient : IDisposable
{
    private string _temporaryLocation;
    private string _channel;
    private readonly string _localManifestPath;

    // e.g. https://patches.mygameserver.com
    // With the patchChannel - https://patches.mygameserver.com/Prod
    private readonly Uri _baseUri;

    public PatchSyncClient(
        string baseUri, PatchChannel patchChannel, string localInstallationPath, string? localManifestPath = null
    ) : this(baseUri, localInstallationPath, localManifestPath, patchChannel.ToString())
    {
    }

    public PatchSyncClient(
        string baseUri,
        string localInstallationPath,
        string? localManifestPath = null,
        string channel = "prod"
    )
    {
        _channel = channel;
        _baseUri = new Uri(new Uri(baseUri), _channel);
        LocalInstallationPath = localInstallationPath;
        _localManifestPath = localManifestPath ?? Path.Combine(LocalInstallationPath, "manifest.json");
    }

    public string LocalInstallationPath { get; }

    public CancellationToken CancellationToken { get; private set; }

    private Uri GetFileUri(string relativeUri) => new(_baseUri, relativeUri);

    private DirectoryInfo GetTemporaryDirectory()
    {
        if (string.IsNullOrEmpty(_temporaryLocation))
        {
            _temporaryLocation = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(_temporaryLocation);
        }

        return new DirectoryInfo(_temporaryLocation);
    }

    public PatchSyncClient WithCancellationToken(CancellationToken cancellationToken)
    {
        CancellationToken = cancellationToken;
        return this;
    }

    public async Task<SignatureFile> GetSignatureFile(int originalFileSize, string relativeUri, int chunkSize)
    {
        var stream = await Downloader.DownloadFileAsync(GetFileUri(relativeUri), cancellationToken: CancellationToken);
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
        var url = new Uri($"{_baseUri}/{relativeUri}");

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

            progress?.Report(new DownloadProgress(progressInfo.Name, progressInfo.TotalSize, progressInfo.TotalRead, speed));
        }
    }

    public void ValidateFiles(string baseFolder, Func<ValidationResult> callback) =>
        FileValidator.ValidateFiles(_manifest, baseFolder, callback);

    private class ProgressInfo(long totalSize)
    {
        public string FileName { get; set; }
        public long TotalSize { get; } = totalSize;
        public long TotalRead { get; set; }
        public Stopwatch Stopwatch { get; } = new();
    }

    public void Dispose()
    {
        Directory.Delete(_temporaryLocation);
    }
}
