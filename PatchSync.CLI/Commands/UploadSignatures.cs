using Amazon.Runtime;
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

    var endpointFromEnvironment = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL");

    bool agreed;
    string? endpointUrl;
    do
    {
      AnsiConsole.Clear();
      
      // Use the endpoint from AWS environment variables, otherwise prompt
      endpointUrl = endpointFromEnvironment ?? GetUploadProvider() switch
      {
        UploadProvider.BackBlaze    => $"https://s3.{GetBackBlazeRegion()}.backblazeb2.com",
        UploadProvider.CloudFlare   => $"https://{GetCloudFlareAccountId()}.r2.cloudflarestorage.com",
        UploadProvider.DigitalOcean => $"https://{GetDigitalOceanRegion()}.digitaloceanspaces.com",
        UploadProvider.Google       => "https://storage.googleapis.com",
        UploadProvider.Linode       => $"https://{GetLinodeRegion()}.linodeobjects.com",
        UploadProvider.Other        => GetGenericEndpoint(),
        _                           => null
      };
      
      var bucket = GetBucket();
      var path = GetBasePath();
      agreed = PromptLooksCorrect(endpointUrl, bucket, path);
    } while (!agreed);

    AnsiConsole.WriteLine("");

    AWSCredentials credentials;
    try
    {
      AnsiConsole.MarkupLine("[green]Attempting to retrieve S3-compatible credentials...[/]");
      credentials = FallbackCredentialsFactory.GetCredentials();
      _ = credentials.GetCredentials(); // Will throw if no credentials are configured
    }
    catch
    {
      credentials = PromptMissingCredentials();
    }

    var s3Client = endpointUrl == null
      ? new AmazonS3Client(credentials)
      : new AmazonS3Client(credentials, new AmazonS3Config { ServiceURL = endpointUrl });
  }
}