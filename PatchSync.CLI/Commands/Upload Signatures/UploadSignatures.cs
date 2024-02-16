using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using PatchSync.CLI.Json;
using PatchSync.Common.Manifest;
using Spectre.Console;

namespace PatchSync.CLI.Commands;

public partial class UploadSignatures : ICommand
{
    public string Name => "Upload Signatures";

    public void ExecuteCommand(CLIContext cliContext)
    {
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

        AnsiConsole.WriteLine();

        PatchManifest? patchManifest = null;
        string? manifestPath = null;
        if (cliContext.TryGetProperty<(PatchManifest, string)>(nameof(PatchManifest), out var manifestTuple))
        {
            patchManifest = manifestTuple.Item1;
            manifestPath = manifestTuple.Item2;
        }

        // Cannot find existing, or we don't want to use it
        if (patchManifest == null || manifestPath == null || !PromptExistingManifest())
        {
            manifestPath = GetManifestFile();
            using var openManifestFile = File.Open(manifestPath, FileMode.Open);
            patchManifest = JsonSerializer.Deserialize(
                openManifestFile,
                JsonSourceGenerationContext.Default.PatchManifest
            ) ?? throw new Exception("Failed to json deserialize manifest file");
        }

        AnsiConsole.MarkupLineInterpolated($"[green]Manifest File[/] - {manifestPath}");
        var table = new Table();
        table.AddColumn("File");
        table.AddColumn("File");
        table.Border(TableBorder.Heavy);
        table.Collapse();

        var manifestFiles = patchManifest.Files.OrderBy(entry => entry.FilePath, StringComparer.OrdinalIgnoreCase).ToArray();

        var length = manifestFiles.Length;
        var halfLength = length / 2;
        for (var i = 0; i < halfLength; i++)
        {
            var manifestFile = manifestFiles[i];
            var markup = Markup.FromInterpolated($"{manifestFile.Command.GetIcon()} {manifestFile.FilePath}");

            if (halfLength + i >= length)
            {
                table.AddRow(markup);
                continue;
            }

            manifestFile = manifestFiles[halfLength + i];
            table.AddRow(
                markup,
                Markup.FromInterpolated($"{manifestFile.Command.GetIcon()} {manifestFile.FilePath}")
            );
        }

        AnsiConsole.Write(table);

        AnsiConsole.WriteLine();

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

        var s3Client = endpointUrl == null
            ? new AmazonS3Client(credentials)
            : new AmazonS3Client(credentials, new AmazonS3Config { ServiceURL = endpointUrl });
    }
}
