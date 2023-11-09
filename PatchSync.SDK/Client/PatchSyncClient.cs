using System.Text.Json;
using PatchSync.Common.Manifest;
using PatchSync.Common.Signatures;
using PatchSync.SDK.Signatures;

namespace PatchSync.SDK.Client;

public class PatchSyncClient(string baseUri)
{
  public PatchManifest Manifest { get; private set; }
  
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
    Manifest = await JsonSerializer.DeserializeAsync<PatchManifest>(stream, JsonSerializerOptions.Default) ??
               throw new InvalidOperationException("Could not properly download the manifest.");
    
#if NETSTANDARD2_1_OR_GREATER
    await stream.DisposeAsync();
#else
    stream.Dispose();
#endif
    return Manifest;
  }

  public async Task<SignatureFile> GetSignatureFile(int originalFileSize, string relativeUri)
  {
    var stream = await DownloadFile(baseUri, relativeUri);
    var signatureFile = SignatureFileHandler.LoadSignature(originalFileSize, stream);
    
#if NETSTANDARD2_1_OR_GREATER
    await stream.DisposeAsync();
#else
    stream.Dispose();
#endif
    return signatureFile;
  }
  
  public void ValidateFiles(string baseFolder, Func<ValidationResult> callback) =>
    FileValidator.ValidateFiles(Manifest, baseFolder, callback);
}