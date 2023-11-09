using Spectre.Console;

namespace PatchSync.CLI.Commands;

public partial class UploadSignatures
{
  private static bool PromptExistingManifest()
  {
    return AnsiConsole.Prompt(
      new ConfirmationPrompt("Use newly created patch manifest at [green]patch manifest[/]:")
      {
        DefaultValue = true
      }
    );
  }
  
  private static string GetInputFolder()
  {
    return AnsiConsole.Prompt(
      new TextPrompt<string>("Please specify the folder with the [green]manifest[/] and [green]signatures[/]:")
        .PromptStyle("blue")
        .Validate(folder =>
        {
          if (!Directory.Exists(folder))
          {
            return ValidationResult.Error("[red]You must specify an existing folder.[/]");
          }

          var manifestPath = Path.Combine(folder, "manifest.json");

          if (!File.Exists(manifestPath))
          {
            return ValidationResult.Error($"[red]Cannot find manifest at {manifestPath}.[/]");
          }

          if (Directory.EnumerateFiles(folder, "*.sig", SearchOption.AllDirectories).Any())
          {
            return ValidationResult.Success();
          }
          
          return ValidationResult.Error("[red]You must specify a folder with signature files.[/]");
        }));
  }
  
  private static UploadProvider GetUploadProvider()
  {
    var value = AnsiConsole.Prompt(
      new SelectionPrompt<UploadProvider>()
        .Title("Select your [green]hosting provider[/]:")
        .AddChoices(Enum.GetValues<UploadProvider>())
    );
    
    AnsiConsole.MarkupLineInterpolated($"Select your [green]hosting provider[/]: [blue]{value}[/]");
    return value;
  }

  private static string GetCloudFlareAccountId()
  {
    return AnsiConsole.Prompt(
      new TextPrompt<string>("Please specify your [green]CloudFlare Account ID[/]:")
        .PromptStyle("blue")
        .Validate(accountId =>
        {
          if (string.IsNullOrWhiteSpace(accountId))
          {
            return ValidationResult.Error("[red]You must specify a valid account id.[/]");
          }

          return ValidationResult.Success();
        }));
  }

  private static string GetBackBlazeRegion()
  {
    var endpointOrRegion = AnsiConsole.Prompt(
      new TextPrompt<string>("Please specify your Backblaze [green]endpoint[/] or [green]region[/]:")
        .DefaultValue("us-west-000")
        .ShowDefaultValue(true)
        .PromptStyle("blue")
        .Validate(endpointUrl =>
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
        }));

    return endpointOrRegion.EndsWith("backblazeb2.com")
      ? new Uri(endpointOrRegion).Host.Split('.')[1]
      : endpointOrRegion;
  }
  
  private static string GetDigitalOceanRegion()
  {
    var endpointOrRegion = AnsiConsole.Prompt(
      new TextPrompt<string>("Please specify your Digital Ocean [green]endpoint[/] or [green]region[/]:")
        .DefaultValue("nyc3")
        .ShowDefaultValue(true)
        .PromptStyle("blue")
        .Validate(endpointUrl =>
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
        }));

    return endpointOrRegion.EndsWith("digitaloceanspaces.com")
      ? new Uri(endpointOrRegion).Host.Split('.')[0]
      : endpointOrRegion;
  }
  
  private static string GetLinodeRegion()
  {
    var endpointOrRegion = AnsiConsole.Prompt(
      new TextPrompt<string>("Please specify your Linode [green]endpoint[/] or [green]region[/]:")
        .DefaultValue("us-east-1")
        .ShowDefaultValue(true)
        .PromptStyle("blue")
        .Validate(endpointUrl =>
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
        }));

    return endpointOrRegion.EndsWith("linodeobjects.com")
      ? new Uri(endpointOrRegion).Host.Split('.')[0]
      : endpointOrRegion;
  }
  
  private static string GetGenericEndpoint()
  {
    var endpointOrRegion = AnsiConsole.Prompt(
      new TextPrompt<string>("Please specify your S3 compatible storage provider [green]endpoint[/]:")
        .PromptStyle("blue")
        .Validate(endpointUrl =>
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
        }));

    return endpointOrRegion.EndsWith("linodeobjects.com")
      ? new Uri(endpointOrRegion).Host.Split('.')[0]
      : endpointOrRegion;
  }
  
  private static string GetBucket()
  {
    return AnsiConsole.Prompt(
      new TextPrompt<string>("Please specify the [green]bucket[/]:")
        .PromptStyle("blue")
        .Validate(bucket =>
        {
          if (string.IsNullOrWhiteSpace(bucket))
          {
            return ValidationResult.Error("[red]You must specify a valid bucket.[/]");
          }

          return ValidationResult.Success();
        }));
  }
  
  private static string GetBasePath()
  {
    return AnsiConsole.Prompt(
      new TextPrompt<string>("Please specify the base [green]path[/]:")
        .PromptStyle("blue")
        .Validate(path =>
        {
          if (string.IsNullOrWhiteSpace(path))
          {
            return ValidationResult.Error("[red]You must specify a valid path.[/]");
          }

          return ValidationResult.Success();
        }));
  }
  
  private static bool PromptLooksCorrect(string? endpointUrl, string bucket, string path)
  {
    AnsiConsole.WriteLine("");
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
}