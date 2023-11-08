using Amazon.Runtime;
using Spectre.Console;

namespace PatchSync.CLI.Commands;

public partial class UploadSignatures
{
    private static bool PromptExistingManifest() =>
        AnsiConsole.Prompt(
            new ConfirmationPrompt("Use newly created patch manifest at [green]patch manifest[/]:")
            {
                DefaultValue = true
            }
        );

    private static string GetManifestFile() =>
        new FileBrowser
            {
                Title = "Please choose the [green]manifest.json[/] file",
                SearchPattern = "manifest.json"
            }
            .SelectFile()
            .GetPath();

    private static UploadProvider GetUploadProvider()
    {
        var value = AnsiConsole.Prompt(
            new SelectionPrompt<UploadProvider>()
                .Title("Select the [green]hosting provider[/]:")
                .AddChoices(Enum.GetValues<UploadProvider>())
        );

        AnsiConsole.MarkupLineInterpolated($"Select the [green]hosting provider[/]: [blue]{value}[/]");
        return value;
    }

    private static string GetCloudFlareAccountId()
    {
        var accountId = Environment.GetEnvironmentVariable("API_ACCOUNT_ID");
        if (accountId != null)
        {
            AnsiConsole.MarkupLineInterpolated($"Cloudflare Account ID: [blue]{accountId}[/]");
            return accountId;
        }

        return AnsiConsole.Prompt(
            new TextPrompt<string>("Please specify the [green]CloudFlare Account ID[/]:")
                .PromptStyle("blue")
                .Validate(
                    accountId =>
                    {
                        if (string.IsNullOrWhiteSpace(accountId))
                        {
                            return ValidationResult.Error("[red]You must specify a valid account id.[/]");
                        }

                        return ValidationResult.Success();
                    }
                )
        );
    }

    private static string GetBackBlazeRegion()
    {
        var endpointOrRegion = AnsiConsole.Prompt(
            new TextPrompt<string>("Please specify the Backblaze [green]endpoint[/] or [green]region[/]:")
                .DefaultValue("us-west-000")
                .ShowDefaultValue(true)
                .PromptStyle("blue")
                .Validate(
                    endpointUrl =>
                    {
                        // s3.us-west-000.backblazeb2.com
                        if (string.IsNullOrWhiteSpace(endpointUrl))
                        {
                            return ValidationResult.Error("[red]You must specify a valid endpoint url or region.[/]");
                        }

                        if (endpointUrl.EndsWith("backblazeb2.com"))
                        {
                            var endpointUri = new Uri(endpointUrl);
                            if (!endpointUri.Host.StartsWith("s3"))
                            {
                                return ValidationResult.Error("[red]You must specify a valid endpoint url or region.[/]");
                            }
                        }
                        else if (endpointUrl.Split('-').Length != 3)
                        {
                            return ValidationResult.Error("[red]You must specify a valid endpoint url or region.[/]");
                        }

                        return ValidationResult.Success();
                    }
                )
        );

        return endpointOrRegion.EndsWith("backblazeb2.com")
            ? new Uri(endpointOrRegion).Host.Split('.')[1]
            : endpointOrRegion;
    }

    private static string GetDigitalOceanRegion()
    {
        var endpointOrRegion = AnsiConsole.Prompt(
            new TextPrompt<string>("Please specify the Digital Ocean [green]endpoint[/] or [green]region[/]:")
                .DefaultValue("nyc3")
                .ShowDefaultValue(true)
                .PromptStyle("blue")
                .Validate(
                    endpointUrl =>
                    {
                        // nyc3.digitaloceanspaces.com
                        if (string.IsNullOrWhiteSpace(endpointUrl))
                        {
                            return ValidationResult.Error("[red]You must specify a valid endpoint url or region.[/]");
                        }

                        if (!endpointUrl.EndsWith("digitaloceanspaces.com"))
                        {
                            return ValidationResult.Error("[red]You must specify a valid endpoint url or region.[/]");
                        }

                        return ValidationResult.Success();
                    }
                )
        );

        return endpointOrRegion.EndsWith("digitaloceanspaces.com")
            ? new Uri(endpointOrRegion).Host.Split('.')[0]
            : endpointOrRegion;
    }

    private static string GetLinodeRegion()
    {
        var endpointOrRegion = AnsiConsole.Prompt(
            new TextPrompt<string>("Please specify the Linode [green]endpoint[/] or [green]region[/]:")
                .DefaultValue("us-east-1")
                .ShowDefaultValue(true)
                .PromptStyle("blue")
                .Validate(
                    endpointUrl =>
                    {
                        // us-east-1.linodeobjects.com
                        if (string.IsNullOrWhiteSpace(endpointUrl))
                        {
                            return ValidationResult.Error("[red]You must specify a valid endpoint url or region.[/]");
                        }

                        if (!endpointUrl.EndsWith("linodeobjects.com"))
                        {
                            return ValidationResult.Error("[red]You must specify a valid endpoint url or region.[/]");
                        }

                        return ValidationResult.Success();
                    }
                )
        );

        return endpointOrRegion.EndsWith("linodeobjects.com")
            ? new Uri(endpointOrRegion).Host.Split('.')[0]
            : endpointOrRegion;
    }

    private static string GetGenericEndpoint()
    {
        var endpointOrRegion = AnsiConsole.Prompt(
            new TextPrompt<string>("Please specify the S3 compatible storage provider [green]endpoint[/]:")
                .PromptStyle("blue")
                .Validate(
                    endpointUrl =>
                    {
                        if (string.IsNullOrWhiteSpace(endpointUrl))
                        {
                            return ValidationResult.Error("[red]You must specify a valid endpoint url or region.[/]");
                        }

                        if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out _))
                        {
                            return ValidationResult.Error("[red]You must specify a valid endpoint url or region.[/]");
                        }

                        return ValidationResult.Success();
                    }
                )
        );

        return endpointOrRegion.EndsWith("linodeobjects.com")
            ? new Uri(endpointOrRegion).Host.Split('.')[0]
            : endpointOrRegion;
    }

    private static string GetBucket()
    {
        return AnsiConsole.Prompt(
            new TextPrompt<string>("Please specify the [green]bucket[/]:")
                .PromptStyle("blue")
                .Validate(
                    bucket =>
                    {
                        if (string.IsNullOrWhiteSpace(bucket))
                        {
                            return ValidationResult.Error("[red]You must specify a valid bucket.[/]");
                        }

                        return ValidationResult.Success();
                    }
                )
        );
    }

    private static string GetBasePath()
    {
        return AnsiConsole.Prompt(
            new TextPrompt<string>("Please specify the [green]base path[/]:")
                .PromptStyle("blue")
                .Validate(
                    path =>
                    {
                        if (string.IsNullOrWhiteSpace(path))
                        {
                            return ValidationResult.Error("[red]You must specify a valid path.[/]");
                        }

                        return ValidationResult.Success();
                    }
                )
        );
    }

    private static bool PromptLooksCorrect(string? endpointUrl, string bucket, string path)
    {
        AnsiConsole.WriteLine();
        if (endpointUrl != null)
        {
            AnsiConsole.MarkupLineInterpolated($"Endpoint set to: [green]{endpointUrl}[/]");
        }

        Uri.TryCreate(new Uri($"s3://{bucket}"), path, out var s3Path);

        AnsiConsole.MarkupLineInterpolated($"Path to upload set: [green]{s3Path}[/]");
        return AnsiConsole.Prompt(
            new ConfirmationPrompt("Does everything look correct?")
            {
                DefaultValue = true
            }
        );
    }

    private static BasicAWSCredentials PromptMissingCredentials()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[red]Error:[/] Could not find S3-compatible credentials.");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("To store credentials securely, consult the AWS CLI credentials documentation:");
        AnsiConsole.MarkupLine("- [green]https://docs.aws.amazon.com/cli/latest/userguide/cli-configure-files.html[/]");
        AnsiConsole.WriteLine();

        var accessKey = AnsiConsole.Prompt(
            new TextPrompt<string>("Please specify the [green]access key[/]:")
                .PromptStyle("blue")
                .Validate(
                    path =>
                    {
                        if (string.IsNullOrWhiteSpace(path))
                        {
                            return ValidationResult.Error("[red]You must specify a valid access key.[/]");
                        }

                        return ValidationResult.Success();
                    }
                )
        );

        var secretKey = AnsiConsole.Prompt(
            new TextPrompt<string>("Please specify the [green]secret key[/] (not stored):")
                .PromptStyle("darkorange3")
                .Secret()
                .Validate(
                    path =>
                    {
                        if (string.IsNullOrWhiteSpace(path))
                        {
                            return ValidationResult.Error("[red]You must specify a valid secret key.[/]");
                        }

                        return ValidationResult.Success();
                    }
                )
        );

        return new BasicAWSCredentials(accessKey, secretKey);
    }
}
