using Amazon.S3;
using PatchSync.Common.Manifest;
using Spectre.Console;

namespace PatchSync.CLI.Commands;

public partial class UploadSignatures : ICommand
{
  public string Name => "Upload Signatures";
  
  public void ExecuteCommand(CLIContext cliContext)
  {
    PatchManifest? existingPatchManifest = null;
    string? manifestPath = null;
    if (cliContext.TryGetProperty<(PatchManifest, string)>(nameof(PatchManifest), out var manifestTuple))
    {
      existingPatchManifest = manifestTuple.Item1;
      manifestPath = manifestTuple.Item2;
    }

    // Cannot find existing, or we don't want to use it
    if (existingPatchManifest == null || manifestPath == null || !PromptExistingManifest())
    {
      manifestPath = GetInputFolder();
    }

    bool agreed;
    string? endpointUrl;
    do
    {
      AnsiConsole.Clear();
      var provider = GetUploadProvider();
      endpointUrl = provider switch
      {
        UploadProvider.CloudFlare => $"https://{GetCloudFlareAccountId()}.r2.cloudflarestorage.com",
        UploadProvider.BackBlaze  => $"https://s3.{GetBackBlazeRegion()}.backblazeb2.com",
        _                         => null
      };
      
      var bucket = GetBucket();
      var path = GetBasePath();
      agreed = PromptLooksCorrect(endpointUrl, bucket, path);
    } while (!agreed);
    
    var s3Client = new AmazonS3Client(new AmazonS3Config
    {
      ServiceURL = endpointUrl
    });
  }
}