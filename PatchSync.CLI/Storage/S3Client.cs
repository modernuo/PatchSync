using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using PatchSync.CLI.Config;

namespace PatchSync.CLI.Storage;

/// <summary>
/// Minimal S3-compatible client with AWS Signature V4.
/// Fully AOT-compatible - no reflection, no AWS SDK dependencies.
/// Supports multipart uploads for large files.
/// </summary>
public sealed class S3Client : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly S3Config _config;
    private readonly string _service = "s3";

    /// <summary>
    /// Minimum size for multipart upload (5 MB).
    /// </summary>
    public const long MultipartThreshold = 5 * 1024 * 1024;

    /// <summary>
    /// Part size for multipart uploads (50 MB).
    /// </summary>
    public const long MultipartPartSize = 50 * 1024 * 1024;

    public S3Client(S3Config config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));

        if (string.IsNullOrEmpty(_config.Endpoint))
            throw new ArgumentException("S3 endpoint is required", nameof(config));
        if (string.IsNullOrEmpty(_config.AccessKey))
            throw new ArgumentException("S3 access key is required", nameof(config));
        if (string.IsNullOrEmpty(_config.SecretKey))
            throw new ArgumentException("S3 secret key is required", nameof(config));
        if (string.IsNullOrEmpty(_config.Bucket))
            throw new ArgumentException("S3 bucket is required", nameof(config));

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
    }

    /// <summary>
    /// Uploads a file to S3. Automatically uses multipart upload for large files.
    /// </summary>
    public async Task UploadFileAsync(
        string localPath,
        string remotePath,
        string? contentType = null,
        IProgress<UploadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var fileInfo = new FileInfo(localPath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException("File not found", localPath);

        remotePath = NormalizePath(remotePath);
        contentType ??= GetContentType(remotePath);

        if (fileInfo.Length > MultipartThreshold)
        {
            await UploadMultipartAsync(localPath, remotePath, contentType, progress, cancellationToken);
        }
        else
        {
            await UploadSingleAsync(localPath, remotePath, contentType, progress, cancellationToken);
        }
    }

    /// <summary>
    /// Uploads a stream to S3.
    /// </summary>
    public async Task UploadStreamAsync(
        Stream stream,
        string remotePath,
        string? contentType = null,
        CancellationToken cancellationToken = default)
    {
        remotePath = NormalizePath(remotePath);
        contentType ??= GetContentType(remotePath);

        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        await SendRequestAsync(HttpMethod.Put, remotePath, content, cancellationToken);
    }

    private async Task UploadSingleAsync(
        string localPath,
        string remotePath,
        string contentType,
        IProgress<UploadProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            localPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var fileSize = stream.Length;
        var progressStream = new ProgressStream(stream, fileSize, progress);

        var content = new StreamContent(progressStream);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Headers.ContentLength = fileSize;

        await SendRequestAsync(HttpMethod.Put, remotePath, content, cancellationToken);
    }

    private async Task UploadMultipartAsync(
        string localPath,
        string remotePath,
        string contentType,
        IProgress<UploadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(localPath);
        var totalSize = fileInfo.Length;
        var uploadedBytes = 0L;

        // Initiate multipart upload
        var uploadId = await InitiateMultipartUploadAsync(remotePath, contentType, cancellationToken);

        try
        {
            var parts = new List<(int PartNumber, string ETag)>();
            var partNumber = 1;

            await using var stream = new FileStream(
                localPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = new byte[MultipartPartSize];

            while (true)
            {
                var bytesRead = await ReadFullBufferAsync(stream, buffer, cancellationToken);
                if (bytesRead == 0)
                    break;

                var partData = bytesRead < buffer.Length
                    ? buffer[..bytesRead]
                    : buffer;

                var etag = await UploadPartAsync(
                    remotePath, uploadId, partNumber, partData, cancellationToken);

                parts.Add((partNumber, etag));
                partNumber++;

                uploadedBytes += bytesRead;
                progress?.Report(new UploadProgress(uploadedBytes, totalSize, remotePath));
            }

            // Complete multipart upload
            await CompleteMultipartUploadAsync(remotePath, uploadId, parts, cancellationToken);
        }
        catch
        {
            // Abort on failure
            try
            {
                await AbortMultipartUploadAsync(remotePath, uploadId, cancellationToken);
            }
            catch
            {
                // Ignore abort errors
            }
            throw;
        }
    }

    private async Task<int> ReadFullBufferAsync(
        Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(
                buffer.AsMemory(totalRead, buffer.Length - totalRead),
                cancellationToken);

            if (bytesRead == 0)
                break;

            totalRead += bytesRead;
        }
        return totalRead;
    }

    private async Task<string> InitiateMultipartUploadAsync(
        string path, string contentType, CancellationToken cancellationToken)
    {
        var uri = BuildUri(path + "?uploads");
        var request = CreateSignedRequest(HttpMethod.Post, uri, null);
        request.Headers.Add("x-amz-content-sha256", "UNSIGNED-PAYLOAD");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response);

        var xml = await response.Content.ReadAsStringAsync(cancellationToken);
        var doc = XDocument.Parse(xml);
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

        return doc.Descendants(ns + "UploadId").First().Value;
    }

    private async Task<string> UploadPartAsync(
        string path, string uploadId, int partNumber, byte[] data, CancellationToken cancellationToken)
    {
        var uri = BuildUri($"{path}?partNumber={partNumber}&uploadId={uploadId}");
        var content = new ByteArrayContent(data);
        var request = CreateSignedRequest(HttpMethod.Put, uri, content);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response);

        return response.Headers.ETag?.Tag.Trim('"') ?? "";
    }

    private async Task CompleteMultipartUploadAsync(
        string path, string uploadId, List<(int PartNumber, string ETag)> parts,
        CancellationToken cancellationToken)
    {
        var uri = BuildUri($"{path}?uploadId={uploadId}");

        var xml = new XElement("CompleteMultipartUpload",
            parts.Select(p => new XElement("Part",
                new XElement("PartNumber", p.PartNumber),
                new XElement("ETag", p.ETag))));

        var content = new StringContent(xml.ToString(), Encoding.UTF8, "application/xml");
        var request = CreateSignedRequest(HttpMethod.Post, uri, content);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response);
    }

    private async Task AbortMultipartUploadAsync(
        string path, string uploadId, CancellationToken cancellationToken)
    {
        var uri = BuildUri($"{path}?uploadId={uploadId}");
        var request = CreateSignedRequest(HttpMethod.Delete, uri, null);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        // Don't throw on abort - it's cleanup
    }

    private async Task SendRequestAsync(
        HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        var uri = BuildUri(path);
        var request = CreateSignedRequest(method, uri, content);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response);
    }

    private Uri BuildUri(string path)
    {
        var endpoint = _config.Endpoint!.TrimEnd('/');

        if (_config.PathStyle)
        {
            return new Uri($"{endpoint}/{_config.Bucket}/{path.TrimStart('/')}");
        }
        else
        {
            // Virtual-hosted style: bucket.endpoint/path
            var uri = new Uri(endpoint);
            return new Uri($"{uri.Scheme}://{_config.Bucket}.{uri.Host}/{path.TrimStart('/')}");
        }
    }

    private HttpRequestMessage CreateSignedRequest(
        HttpMethod method, Uri uri, HttpContent? content)
    {
        var request = new HttpRequestMessage(method, uri);
        if (content != null)
            request.Content = content;

        var now = DateTime.UtcNow;
        var dateStamp = now.ToString("yyyyMMdd");
        var amzDate = now.ToString("yyyyMMddTHHmmssZ");

        request.Headers.Add("x-amz-date", amzDate);
        request.Headers.Add("x-amz-content-sha256", "UNSIGNED-PAYLOAD");
        request.Headers.Host = uri.Host;

        // Build canonical request
        var canonicalUri = Uri.EscapeDataString(uri.AbsolutePath).Replace("%2F", "/");
        var canonicalQueryString = uri.Query.TrimStart('?');
        var signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        var canonicalHeaders =
            $"host:{uri.Host}\n" +
            $"x-amz-content-sha256:UNSIGNED-PAYLOAD\n" +
            $"x-amz-date:{amzDate}\n";

        var canonicalRequest =
            $"{method}\n{canonicalUri}\n{canonicalQueryString}\n{canonicalHeaders}\n{signedHeaders}\nUNSIGNED-PAYLOAD";

        // Create string to sign
        var region = _config.Region ?? "us-east-1";
        var credentialScope = $"{dateStamp}/{region}/{_service}/aws4_request";
        var hashedCanonicalRequest = HashSha256(canonicalRequest);
        var stringToSign =
            $"AWS4-HMAC-SHA256\n{amzDate}\n{credentialScope}\n{hashedCanonicalRequest}";

        // Calculate signature
        var signingKey = GetSignatureKey(_config.SecretKey!, dateStamp, region, _service);
        var signature = HmacSha256Hex(signingKey, stringToSign);

        // Add authorization header
        var authorization =
            $"AWS4-HMAC-SHA256 Credential={_config.AccessKey}/{credentialScope}, " +
            $"SignedHeaders={signedHeaders}, Signature={signature}";

        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        return request;
    }

    private static byte[] GetSignatureKey(string key, string dateStamp, string region, string service)
    {
        var kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + key), dateStamp);
        var kRegion = HmacSha256(kDate, region);
        var kService = HmacSha256(kRegion, service);
        var kSigning = HmacSha256(kService, "aws4_request");
        return kSigning;
    }

    private static byte[] HmacSha256(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string HmacSha256Hex(byte[] key, string data)
    {
        return Convert.ToHexString(HmacSha256(key, data)).ToLowerInvariant();
    }

    private static string HashSha256(string data)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"S3 request failed: {response.StatusCode} - {body}");
        }
    }

    private string NormalizePath(string path)
    {
        path = path.Replace('\\', '/').TrimStart('/');
        if (!string.IsNullOrEmpty(_config.Prefix))
        {
            var prefix = _config.Prefix.Replace('\\', '/').Trim('/');
            path = $"{prefix}/{path}";
        }
        return path;
    }

    private static string GetContentType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".json" => "application/json",
            ".sig" => "application/octet-stream",
            ".vsig" => "application/octet-stream",
            ".exe" => "application/octet-stream",
            ".dll" => "application/octet-stream",
            ".zip" => "application/zip",
            ".zst" => "application/zstd",
            ".gz" => "application/gzip",
            ".txt" => "text/plain",
            ".html" => "text/html",
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".mp3" => "audio/mpeg",
            ".ogg" => "audio/ogg",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            _ => "application/octet-stream"
        };
    }

    /// <summary>
    /// Checks if an object exists at the specified path.
    /// </summary>
    public async Task<bool> ObjectExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        var info = await GetObjectInfoAsync(path, cancellationToken);
        return info != null;
    }

    /// <summary>
    /// Gets information about an object (ETag, size, last modified).
    /// Returns null if the object does not exist.
    /// </summary>
    public async Task<S3ObjectInfo?> GetObjectInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        var uri = BuildUri(path);
        var request = CreateSignedRequest(HttpMethod.Head, uri, null);

        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"S3 HEAD request failed: {response.StatusCode} - {body}");
        }

        var etag = response.Headers.ETag?.Tag.Trim('"') ?? "";
        var size = response.Content.Headers.ContentLength ?? 0;
        var lastModified = response.Content.Headers.LastModified?.UtcDateTime ?? DateTime.UtcNow;

        return new S3ObjectInfo
        {
            Key = path,
            Size = size,
            ETag = etag,
            LastModified = lastModified
        };
    }

    /// <summary>
    /// Lists objects with the specified prefix.
    /// </summary>
    public async Task<List<S3ObjectInfo>> ListObjectsAsync(
        string prefix,
        CancellationToken cancellationToken = default)
    {
        prefix = NormalizePath(prefix);
        var results = new List<S3ObjectInfo>();
        string? continuationToken = null;

        do
        {
            var query = $"list-type=2&prefix={Uri.EscapeDataString(prefix)}";
            if (continuationToken != null)
                query += $"&continuation-token={Uri.EscapeDataString(continuationToken)}";

            var uri = BuildUri($"?{query}");
            var request = CreateSignedRequest(HttpMethod.Get, uri, null);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response);

            var xml = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = XDocument.Parse(xml);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

            foreach (var content in doc.Descendants(ns + "Contents"))
            {
                var key = content.Element(ns + "Key")?.Value ?? "";
                var sizeStr = content.Element(ns + "Size")?.Value ?? "0";
                var etag = content.Element(ns + "ETag")?.Value.Trim('"') ?? "";
                var lastModStr = content.Element(ns + "LastModified")?.Value;

                results.Add(new S3ObjectInfo
                {
                    Key = key,
                    Size = long.Parse(sizeStr),
                    ETag = etag,
                    LastModified = lastModStr != null ? DateTime.Parse(lastModStr) : DateTime.UtcNow
                });
            }

            var isTruncated = doc.Descendants(ns + "IsTruncated").FirstOrDefault()?.Value == "true";
            continuationToken = isTruncated
                ? doc.Descendants(ns + "NextContinuationToken").FirstOrDefault()?.Value
                : null;

        } while (continuationToken != null);

        return results;
    }

    /// <summary>
    /// Deletes multiple objects from S3.
    /// </summary>
    public async Task DeleteObjectsAsync(
        IEnumerable<string> keys,
        CancellationToken cancellationToken = default)
    {
        var keyList = keys.Select(k => NormalizePath(k)).ToList();
        if (keyList.Count == 0)
            return;

        // S3 allows up to 1000 objects per delete request
        const int batchSize = 1000;

        foreach (var batch in keyList.Chunk(batchSize))
        {
            var deleteXml = new XElement("Delete",
                batch.Select(key => new XElement("Object",
                    new XElement("Key", key))));

            var content = new StringContent(deleteXml.ToString(), Encoding.UTF8, "application/xml");

            // Delete request requires ?delete query parameter
            var uri = BuildUri("?delete");
            var request = CreateSignedRequest(HttpMethod.Post, uri, content);

            // Calculate MD5 for delete request body
            var bodyBytes = Encoding.UTF8.GetBytes(deleteXml.ToString());
            var md5 = Convert.ToBase64String(MD5.HashData(bodyBytes));
            request.Content!.Headers.Add("Content-MD5", md5);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response);
        }
    }

    /// <summary>
    /// Deletes a single object from S3.
    /// </summary>
    public async Task DeleteObjectAsync(string path, CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        var uri = BuildUri(path);
        var request = CreateSignedRequest(HttpMethod.Delete, uri, null);

        using var response = await _httpClient.SendAsync(request, cancellationToken);

        // 404 is acceptable - object already doesn't exist
        if (response.StatusCode != HttpStatusCode.NotFound)
            await EnsureSuccessAsync(response);
    }

    /// <summary>
    /// Tests connectivity and credentials by performing a HEAD request on the bucket.
    /// Returns true if credentials are valid and bucket is accessible.
    /// </summary>
    public async Task<bool> TestConnectivityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Try to list with empty prefix (limited to 1 result) to verify access
            var query = "list-type=2&max-keys=1";
            var uri = BuildUri($"?{query}");
            var request = CreateSignedRequest(HttpMethod.Get, uri, null);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Lists all buckets accessible with current credentials.
    /// Note: May not work with all S3-compatible providers (e.g., R2 with bucket-scoped tokens).
    /// </summary>
    public async Task<List<string>> ListBucketsAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<string>();

        try
        {
            // ListBuckets is at the service root, not bucket-specific
            var endpoint = _config.Endpoint!.TrimEnd('/');
            var uri = new Uri($"{endpoint}/");
            var request = CreateSignedRequest(HttpMethod.Get, uri, null);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response);

            var xml = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = XDocument.Parse(xml);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

            foreach (var bucket in doc.Descendants(ns + "Bucket"))
            {
                var name = bucket.Element(ns + "Name")?.Value;
                if (!string.IsNullOrEmpty(name))
                    results.Add(name);
            }
        }
        catch (HttpRequestException)
        {
            // Some providers don't support ListBuckets with bucket-scoped tokens
            // Return empty list instead of throwing
        }

        return results;
    }

    /// <summary>
    /// Lists "folders" (common prefixes) at a given path.
    /// </summary>
    public async Task<List<string>> ListPrefixesAsync(
        string prefix,
        CancellationToken cancellationToken = default)
    {
        prefix = NormalizePath(prefix);
        if (!prefix.EndsWith('/') && !string.IsNullOrEmpty(prefix))
            prefix += "/";

        var results = new List<string>();
        string? continuationToken = null;

        do
        {
            var query = $"list-type=2&prefix={Uri.EscapeDataString(prefix)}&delimiter=/";
            if (continuationToken != null)
                query += $"&continuation-token={Uri.EscapeDataString(continuationToken)}";

            var uri = BuildUri($"?{query}");
            var request = CreateSignedRequest(HttpMethod.Get, uri, null);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response);

            var xml = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = XDocument.Parse(xml);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

            // CommonPrefixes contains the "folders"
            foreach (var commonPrefix in doc.Descendants(ns + "CommonPrefixes"))
            {
                var prefixValue = commonPrefix.Element(ns + "Prefix")?.Value;
                if (!string.IsNullOrEmpty(prefixValue))
                {
                    // Remove trailing slash and get just the folder name
                    var folderName = prefixValue.TrimEnd('/');
                    if (folderName.Contains('/'))
                        folderName = folderName[(folderName.LastIndexOf('/') + 1)..];
                    results.Add(folderName);
                }
            }

            var isTruncated = doc.Descendants(ns + "IsTruncated").FirstOrDefault()?.Value == "true";
            continuationToken = isTruncated
                ? doc.Descendants(ns + "NextContinuationToken").FirstOrDefault()?.Value
                : null;

        } while (continuationToken != null);

        return results;
    }

    /// <summary>
    /// Creates a bucket if it doesn't exist.
    /// Note: May require additional permissions and may not work with all providers.
    /// </summary>
    public async Task<bool> CreateBucketAsync(string bucketName, CancellationToken cancellationToken = default)
    {
        try
        {
            var endpoint = _config.Endpoint!.TrimEnd('/');
            Uri uri;

            if (_config.PathStyle)
            {
                uri = new Uri($"{endpoint}/{bucketName}");
            }
            else
            {
                var baseUri = new Uri(endpoint);
                uri = new Uri($"{baseUri.Scheme}://{bucketName}.{baseUri.Host}/");
            }

            var request = CreateSignedRequest(HttpMethod.Put, uri, null);

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            // 409 Conflict means bucket already exists (which is fine)
            if (response.StatusCode == HttpStatusCode.Conflict)
                return true;

            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

/// <summary>
/// Information about an S3 object.
/// </summary>
public sealed class S3ObjectInfo
{
    public required string Key { get; init; }
    public required long Size { get; init; }
    public required string ETag { get; init; }
    public required DateTime LastModified { get; init; }
}

/// <summary>
/// Progress information for uploads.
/// </summary>
public readonly record struct UploadProgress(
    long BytesUploaded,
    long TotalBytes,
    string CurrentFile)
{
    public double Percentage => TotalBytes > 0 ? (double)BytesUploaded / TotalBytes : 0;
}

/// <summary>
/// Stream wrapper that reports read progress.
/// </summary>
internal sealed class ProgressStream : Stream
{
    private readonly Stream _inner;
    private readonly long _totalLength;
    private readonly IProgress<UploadProgress>? _progress;
    private long _bytesRead;

    public ProgressStream(Stream inner, long totalLength, IProgress<UploadProgress>? progress)
    {
        _inner = inner;
        _totalLength = totalLength;
        _progress = progress;
    }

    public override bool CanRead => true;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _totalLength;
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var bytesRead = _inner.Read(buffer, offset, count);
        _bytesRead += bytesRead;
        _progress?.Report(new UploadProgress(_bytesRead, _totalLength, ""));
        return bytesRead;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var bytesRead = await _inner.ReadAsync(buffer, offset, count, cancellationToken);
        _bytesRead += bytesRead;
        _progress?.Report(new UploadProgress(_bytesRead, _totalLength, ""));
        return bytesRead;
    }

    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
