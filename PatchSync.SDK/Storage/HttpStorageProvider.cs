using System.Net;
using System.Net.Http.Headers;
using PatchSync.Common.Storage;

namespace PatchSync.SDK.Storage;

/// <summary>
/// Storage provider for HTTP/HTTPS with byte-range request support.
/// Uses HTTP/2 when available for multiplexed connections.
/// </summary>
public sealed class HttpStorageProvider : IStorageProvider, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly bool _supportsMultiRange;

    /// <summary>
    /// Creates a new HTTP storage provider.
    /// </summary>
    /// <param name="baseUrl">Base URL for all requests.</param>
    /// <param name="supportsMultiRange">Whether the server supports multi-range requests.</param>
    public HttpStorageProvider(Uri baseUrl, bool supportsMultiRange = false)
    {
        BaseUrl = baseUrl;
        _supportsMultiRange = supportsMultiRange;

        var handler = new SocketsHttpHandler
        {
            // Enable HTTP/2 for multiplexed connections
            EnableMultipleHttp2Connections = true,
            // Connection pooling for CDN efficiency
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 10,
            // Auto-decompress responses
            AutomaticDecompression = DecompressionMethods.All
        };

        _httpClient = new HttpClient(handler)
        {
            BaseAddress = baseUrl,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Timeout = TimeSpan.FromMinutes(30)
        };

        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("PatchSync", "2.0"));

        _ownsClient = true;
    }

    /// <summary>
    /// Creates a new HTTP storage provider with a custom HttpClient.
    /// </summary>
    public HttpStorageProvider(HttpClient httpClient, bool supportsMultiRange = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        BaseUrl = httpClient.BaseAddress ?? throw new ArgumentException("HttpClient must have BaseAddress set");
        _supportsMultiRange = supportsMultiRange;
        _ownsClient = false;
    }

    /// <inheritdoc />
    public Uri BaseUrl { get; }

    /// <inheritdoc />
    public bool SupportsMultiRange => _supportsMultiRange;

    /// <inheritdoc />
    public async Task<Stream> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Stream> GetRangeAsync(
        string path,
        long offset,
        long length,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        // Accept both 200 (full content) and 206 (partial content)
        if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.PartialContent)
        {
            response.EnsureSuccessStatusCode(); // Will throw with details
        }

        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Stream>> GetMultiRangeAsync(
        string path,
        IReadOnlyList<(long Offset, long Length)> ranges,
        CancellationToken cancellationToken = default)
    {
        if (ranges.Count == 0)
            return Array.Empty<Stream>();

        if (ranges.Count == 1)
        {
            var stream = await GetRangeAsync(path, ranges[0].Offset, ranges[0].Length, cancellationToken);
            return new[] { stream };
        }

        // For servers that support multi-range, try it first
        if (_supportsMultiRange)
        {
            try
            {
                return await GetMultiRangeInternalAsync(path, ranges, cancellationToken);
            }
            catch (HttpRequestException)
            {
                // Fall back to sequential requests
            }
        }

        // Sequential fallback
        return await GetRangesSequentialAsync(path, ranges, cancellationToken);
    }

    private async Task<IReadOnlyList<Stream>> GetMultiRangeInternalAsync(
        string path,
        IReadOnlyList<(long Offset, long Length)> ranges,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        // Build multi-range header: bytes=0-99,100-199,200-299
        var rangeHeader = new RangeHeaderValue();
        foreach (var (offset, length) in ranges)
        {
            rangeHeader.Ranges.Add(new RangeItemHeaderValue(offset, offset + length - 1));
        }
        request.Headers.Range = rangeHeader;

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            // Server doesn't support multi-range, fall back
            throw new HttpRequestException("Multi-range not supported");
        }

        // Check if response is multipart
        var contentType = response.Content.Headers.ContentType;
        if (contentType?.MediaType == "multipart/byteranges")
        {
            return await ParseMultipartResponseAsync(response, ranges.Count, cancellationToken);
        }

        // Single range returned (server may have merged or returned first only)
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return new[] { stream };
    }

    private async Task<IReadOnlyList<Stream>> ParseMultipartResponseAsync(
        HttpResponseMessage response,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        // Read the entire response into memory for parsing
        // (multipart boundaries require full content)
        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var boundary = response.Content.Headers.ContentType?.Parameters
            .FirstOrDefault(p => p.Name == "boundary")?.Value?.Trim('"');

        if (string.IsNullOrEmpty(boundary))
            throw new InvalidOperationException("Missing boundary in multipart response");

        var streams = new List<Stream>();
        var boundaryBytes = System.Text.Encoding.ASCII.GetBytes("--" + boundary);

        int pos = 0;
        while (pos < content.Length && streams.Count < expectedCount)
        {
            // Find next boundary
            int boundaryStart = FindBytes(content, boundaryBytes, pos);
            if (boundaryStart < 0) break;

            // Skip boundary and CRLF
            pos = boundaryStart + boundaryBytes.Length;
            if (pos + 2 <= content.Length && content[pos] == '\r' && content[pos + 1] == '\n')
                pos += 2;

            // Skip headers until empty line
            while (pos < content.Length - 1)
            {
                if (content[pos] == '\r' && content[pos + 1] == '\n')
                {
                    pos += 2;
                    break;
                }
                // Skip to next line
                while (pos < content.Length && content[pos] != '\n') pos++;
                pos++;
            }

            // Find end of this part (next boundary)
            int partEnd = FindBytes(content, boundaryBytes, pos);
            if (partEnd < 0) partEnd = content.Length;

            // Trim trailing CRLF before boundary
            if (partEnd >= 2 && content[partEnd - 2] == '\r' && content[partEnd - 1] == '\n')
                partEnd -= 2;

            // Extract part content
            int partLength = partEnd - pos;
            if (partLength > 0)
            {
                var partContent = new byte[partLength];
                Array.Copy(content, pos, partContent, 0, partLength);
                streams.Add(new MemoryStream(partContent, writable: false));
            }

            pos = partEnd;
        }

        return streams;
    }

    private static int FindBytes(byte[] haystack, byte[] needle, int start)
    {
        for (int i = start; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return i;
        }
        return -1;
    }

    private async Task<IReadOnlyList<Stream>> GetRangesSequentialAsync(
        string path,
        IReadOnlyList<(long Offset, long Length)> ranges,
        CancellationToken cancellationToken)
    {
        var streams = new List<Stream>(ranges.Count);

        foreach (var (offset, length) in ranges)
        {
            var stream = await GetRangeAsync(path, offset, length, cancellationToken);
            streams.Add(stream);
        }

        return streams;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
