using System.Text.Json;
using PatchSync.Common.Manifest;
using PatchSync.Common.Signatures;

namespace PatchSync.SDK.Client;

public class PatchSyncClient(string baseUri)
{
  private static async Task<Stream> DownloadFile(string baseUri, string relativeUri)
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
    var stream = await DownloadFile(baseUri, relativeUri);
    var manifest = await JsonSerializer.DeserializeAsync<PatchManifest>(stream, JsonSerializerOptions.Default);
    
    await stream.DisposeAsync();
    return manifest ?? throw new InvalidOperationException("Could not properly download the manifest.");
  }

  public async Task<SignatureFile> GetSignatureFile(int originalFileSize, string relativeUri)
  {
    var stream = await DownloadFile(baseUri, relativeUri);
    var signatureFile = SignatureFile.Deserialize(originalFileSize, stream);
    
    await stream.DisposeAsync();
    return signatureFile;
  }
}