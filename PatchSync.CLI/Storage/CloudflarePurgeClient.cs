using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PatchSync.CLI.Storage;

/// <summary>
/// Client for Cloudflare Cache Purge API.
/// Requires an API token with Zone.Cache Purge permission.
/// </summary>
public sealed class CloudflarePurgeClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _zoneId;
    private const string BaseUrl = "https://api.cloudflare.com/client/v4";

    public CloudflarePurgeClient(string zoneId, string apiToken)
    {
        if (string.IsNullOrEmpty(zoneId))
            throw new ArgumentNullException(nameof(zoneId));
        if (string.IsNullOrEmpty(apiToken))
            throw new ArgumentNullException(nameof(apiToken));

        _zoneId = zoneId;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", apiToken);
    }

    /// <summary>
    /// Purges specific URLs from Cloudflare cache.
    /// Maximum 30 URLs per request for free/pro plans.
    /// </summary>
    public async Task<PurgeResult> PurgeUrlsAsync(
        IEnumerable<string> urls,
        CancellationToken cancellationToken = default)
    {
        var urlList = urls.ToList();
        if (urlList.Count == 0)
            return new PurgeResult { Success = true, PurgedCount = 0 };

        // Cloudflare limits to 30 URLs per request for non-Enterprise
        const int batchSize = 30;
        var totalPurged = 0;
        var errors = new List<string>();

        foreach (var batch in urlList.Chunk(batchSize))
        {
            var requestObj = new CloudflarePurgeRequest { Files = batch };
            var json = JsonSerializer.Serialize(requestObj, CloudflareJsonContext.Default.CloudflarePurgeRequest);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(
                $"{BaseUrl}/zones/{_zoneId}/purge_cache",
                content,
                cancellationToken);

            var result = await response.Content.ReadFromJsonAsync(
                CloudflareJsonContext.Default.CloudflareApiResponse,
                cancellationToken);

            if (result?.Success == true)
            {
                totalPurged += batch.Length;
            }
            else
            {
                var errorMessages = result?.Errors?.Select(e => e.Message) ?? ["Unknown error"];
                errors.AddRange(errorMessages);
            }
        }

        return new PurgeResult
        {
            Success = errors.Count == 0,
            PurgedCount = totalPurged,
            Errors = errors
        };
    }

    /// <summary>
    /// Purges all content with a URL prefix (Enterprise only).
    /// Falls back to specific URL purge for non-Enterprise.
    /// </summary>
    public async Task<PurgeResult> PurgeByPrefixAsync(
        string urlPrefix,
        CancellationToken cancellationToken = default)
    {
        // Prefix purge requires Enterprise plan
        // Try it first, fall back to warning if not available
        var requestObj = new CloudflarePrefixPurgeRequest { Prefixes = [urlPrefix] };

        try
        {
            var json = JsonSerializer.Serialize(requestObj, CloudflareJsonContext.Default.CloudflarePrefixPurgeRequest);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(
                $"{BaseUrl}/zones/{_zoneId}/purge_cache",
                content,
                cancellationToken);

            var result = await response.Content.ReadFromJsonAsync(
                CloudflareJsonContext.Default.CloudflareApiResponse,
                cancellationToken);

            if (result?.Success == true)
            {
                return new PurgeResult { Success = true, PurgedCount = 1, Message = $"Purged prefix: {urlPrefix}" };
            }

            // Check if it's an Enterprise-only feature error
            var errorCode = result?.Errors?.FirstOrDefault()?.Code;
            if (errorCode == 1019) // Feature not available
            {
                return new PurgeResult
                {
                    Success = false,
                    Errors = ["Prefix purge requires Cloudflare Enterprise. Use specific URL purge instead."]
                };
            }

            return new PurgeResult
            {
                Success = false,
                Errors = result?.Errors?.Select(e => e.Message).ToList() ?? ["Unknown error"]
            };
        }
        catch (Exception ex)
        {
            return new PurgeResult { Success = false, Errors = [ex.Message] };
        }
    }

    /// <summary>
    /// Purges everything in the zone (nuclear option).
    /// Use sparingly as it clears all cached content.
    /// </summary>
    public async Task<PurgeResult> PurgeEverythingAsync(CancellationToken cancellationToken = default)
    {
        var requestObj = new CloudflarePurgeEverythingRequest { PurgeEverything = true };

        try
        {
            var json = JsonSerializer.Serialize(requestObj, CloudflareJsonContext.Default.CloudflarePurgeEverythingRequest);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(
                $"{BaseUrl}/zones/{_zoneId}/purge_cache",
                content,
                cancellationToken);

            var result = await response.Content.ReadFromJsonAsync(
                CloudflareJsonContext.Default.CloudflareApiResponse,
                cancellationToken);

            return new PurgeResult
            {
                Success = result?.Success ?? false,
                Message = result?.Success == true ? "Purged all cached content" : null,
                Errors = result?.Errors?.Select(e => e.Message).ToList() ?? []
            };
        }
        catch (Exception ex)
        {
            return new PurgeResult { Success = false, Errors = [ex.Message] };
        }
    }

    /// <summary>
    /// Verifies the API token has correct permissions.
    /// </summary>
    public async Task<bool> VerifyTokenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync(
                $"{BaseUrl}/zones/{_zoneId}",
                cancellationToken);

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
/// Result of a cache purge operation
/// </summary>
public sealed class PurgeResult
{
    public bool Success { get; init; }
    public int PurgedCount { get; init; }
    public string? Message { get; init; }
    public List<string> Errors { get; init; } = [];
}

// JSON serialization types for Cloudflare API

internal sealed class CloudflarePurgeRequest
{
    [JsonPropertyName("files")]
    public string[] Files { get; set; } = [];
}

internal sealed class CloudflarePrefixPurgeRequest
{
    [JsonPropertyName("prefixes")]
    public string[] Prefixes { get; set; } = [];
}

internal sealed class CloudflarePurgeEverythingRequest
{
    [JsonPropertyName("purge_everything")]
    public bool PurgeEverything { get; set; }
}

internal sealed class CloudflareApiResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("errors")]
    public List<CloudflareError>? Errors { get; set; }
}

internal sealed class CloudflareError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

/// <summary>
/// JSON serialization context for Cloudflare API types (AOT-compatible)
/// </summary>
[JsonSerializable(typeof(CloudflarePurgeRequest))]
[JsonSerializable(typeof(CloudflarePrefixPurgeRequest))]
[JsonSerializable(typeof(CloudflarePurgeEverythingRequest))]
[JsonSerializable(typeof(CloudflareApiResponse))]
[JsonSerializable(typeof(CloudflareError))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
internal partial class CloudflareJsonContext : JsonSerializerContext
{
}
