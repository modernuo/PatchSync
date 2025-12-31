using System.IO.Compression;
using PatchSync.CLI.Config;
using PatchSync.CLI.Prompts;
using PatchSync.CLI.Storage;
using Spectre.Console;

namespace PatchSync.CLI.Commands;

public static class UploadCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var parser = new ArgParser(args);

        if (parser.HasHelp)
        {
            ShowHelp();
            return 0;
        }

        try
        {
            var buildDir = parser.GetRequired("build-dir", "b");
            var endpoint = parser.Get("endpoint", "e");
            var bucket = parser.Get("bucket");
            var accessKey = parser.Get("access-key");
            var secretKey = parser.Get("secret-key");
            var region = parser.GetOrDefault("region", "us-east-1", "r");
            var prefix = parser.Get("prefix", "p");
            var publicUrl = parser.Get("public-url");
            var pathStyle = parser.GetBool("path-style", true);
            var compress = parser.GetBool("compress", true);
            var skipRaw = parser.GetBool("skip-raw");

            // Load config and merge with CLI args + credentials
            var config = await PatchSyncConfig.LoadOrDefaultAsync();
            var s3Config = CredentialManager.MergeWithConfig(
                config.S3, endpoint, bucket, accessKey, secretKey, region, prefix, publicUrl, pathStyle);

            // Check if we have all required credentials
            if (!s3Config.IsComplete)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Missing required S3 configuration: {s3Config.MissingFields}");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[grey]Credentials can be provided via:[/]");
                AnsiConsole.MarkupLine("  1. Command line arguments (--access-key, --secret-key)");
                AnsiConsole.MarkupLine($"  2. Environment variables ({CredentialManager.EnvironmentVariables.AccessKey}, {CredentialManager.EnvironmentVariables.SecretKey})");
                AnsiConsole.MarkupLine("  3. Encrypted credential store (run 'patchsync upload' interactively)");
                return 1;
            }

            return await ExecuteAsync(buildDir, s3Config, compress, skipRaw);
        }
        catch (ArgumentException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            AnsiConsole.WriteLine();
            ShowHelp();
            return 1;
        }
    }

    public static async Task<int> RunWizardAsync()
    {
        AnsiConsole.MarkupLine("[grey]Upload build artifacts to S3-compatible storage[/]\n");

        // Load existing config and credentials
        var config = await PatchSyncConfig.LoadOrDefaultAsync();
        var storedCreds = CredentialManager.LoadCredentials();
        var hasStoredCreds = storedCreds != null &&
                             !string.IsNullOrEmpty(storedCreds.AccessKey) &&
                             !string.IsNullOrEmpty(storedCreds.SecretKey);

        // Build directory - use file browser
        var useBrowser = await AnsiConsole.ConfirmAsync("Browse for build directory?");
        string buildDir;
        if (useBrowser)
        {
            buildDir = Browse.ForFolder("[green]Select build directory[/] (containing manifest.json)");
            // Validate it has manifest.json
            while (!File.Exists(Path.Combine(buildDir, "manifest.json")))
            {
                AnsiConsole.MarkupLine("[red]No manifest.json found in selected directory[/]");
                buildDir = Browse.ForFolder("[green]Select build directory[/] (containing manifest.json)");
            }
        }
        else
        {
            buildDir = AnsiConsole.Prompt(
                new TextPrompt<string>("[green]Build directory path:[/]")
                    .DefaultValue("./output")
                    .Validate(path =>
                    {
                        if (!Directory.Exists(path))
                            return ValidationResult.Error($"Directory not found: {path}");
                        if (!File.Exists(Path.Combine(path, "manifest.json")))
                            return ValidationResult.Error("No manifest.json found in directory");
                        return ValidationResult.Success();
                    }));
        }
        AnsiConsole.MarkupLine($"[blue]Build directory:[/] {buildDir}\n");

        // S3 Configuration
        S3Config s3Config;
        if (hasStoredCreds)
        {
            var credSource = storedCreds!.Endpoint != null
                ? $"[grey]{storedCreds.Endpoint}[/]"
                : "[grey]stored credentials[/]";

            var useExisting = await AnsiConsole.ConfirmAsync(
                $"Use saved S3 credentials? ({credSource})");

            if (useExisting)
            {
                // Merge stored credentials with config
                s3Config = new S3Config
                {
                    Endpoint = storedCreds.Endpoint ?? config.S3?.Endpoint,
                    Bucket = storedCreds.Bucket ?? config.S3?.Bucket,
                    Region = storedCreds.Region ?? config.S3?.Region ?? "us-east-1",
                    AccessKey = storedCreds.AccessKey,
                    SecretKey = storedCreds.SecretKey,
                    Prefix = storedCreds.Prefix ?? config.S3?.Prefix,
                    PublicUrl = storedCreds.PublicUrl ?? config.S3?.PublicUrl,
                    PathStyle = config.S3?.PathStyle ?? true
                };
            }
            else
            {
                s3Config = await PromptS3ConfigAsync();
            }
        }
        else
        {
            s3Config = await PromptS3ConfigAsync();
        }

        // Optional prefix
        var prefix = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]Upload prefix[/] (e.g., game/v1.0.0/):")
                .AllowEmpty()
                .DefaultValue(s3Config.Prefix ?? ""));

        s3Config.Prefix = string.IsNullOrEmpty(prefix) ? null : prefix;

        // Options
        var compress = await AnsiConsole.ConfirmAsync("Compress files for fallback downloads?");
        var skipRaw = await AnsiConsole.ConfirmAsync("Skip uploading raw files? (use if hosting separately)", false);

        // Offer to save credentials (with warning)
        if (!hasStoredCreds || !await AnsiConsole.ConfirmAsync("Keep using existing saved credentials?"))
        {
            var choices = new List<string>
            {
                "Don't save (enter each time)",
                "Show environment variable names (for CI/CD)"
            };

            // Only offer DPAPI storage on Windows
            if (CredentialManager.IsSecureStorageAvailable)
            {
                choices.Insert(1, "Save with DPAPI encryption (read security warning first)");
            }

            var saveChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[green]How would you like to handle credentials for future uploads?[/]")
                    .AddChoices(choices));

            if (saveChoice.Contains("DPAPI") && OperatingSystem.IsWindows())
            {
                var credentials = new S3Credentials
                {
                    Endpoint = s3Config.Endpoint,
                    Bucket = s3Config.Bucket,
                    Region = s3Config.Region,
                    AccessKey = s3Config.AccessKey,
                    SecretKey = s3Config.SecretKey,
                    Prefix = s3Config.Prefix,
                    PublicUrl = s3Config.PublicUrl
                };
                CredentialManager.SaveCredentials(credentials, showWarning: true);
            }
            else if (saveChoice.Contains("environment"))
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[yellow]Set these environment variables for CI/CD:[/]");
                AnsiConsole.MarkupLine($"  {CredentialManager.EnvironmentVariables.Endpoint}={s3Config.Endpoint}");
                AnsiConsole.MarkupLine($"  {CredentialManager.EnvironmentVariables.Bucket}={s3Config.Bucket}");
                AnsiConsole.MarkupLine($"  {CredentialManager.EnvironmentVariables.Region}={s3Config.Region}");
                AnsiConsole.MarkupLine($"  {CredentialManager.EnvironmentVariables.AccessKey}={s3Config.AccessKey}");
                AnsiConsole.MarkupLine($"  {CredentialManager.EnvironmentVariables.SecretKey}=<your-secret-key>");
                if (!string.IsNullOrEmpty(s3Config.Prefix))
                    AnsiConsole.MarkupLine($"  {CredentialManager.EnvironmentVariables.Prefix}={s3Config.Prefix}");
                if (!string.IsNullOrEmpty(s3Config.PublicUrl))
                    AnsiConsole.MarkupLine($"  {CredentialManager.EnvironmentVariables.PublicUrl}={s3Config.PublicUrl}");
                AnsiConsole.WriteLine();
            }
        }

        // Save non-sensitive config (endpoint, bucket, region, etc. but NOT secret key)
        if (await AnsiConsole.ConfirmAsync("Save non-sensitive S3 settings to config file?"))
        {
            config.S3 = new S3Config
            {
                Endpoint = s3Config.Endpoint,
                Bucket = s3Config.Bucket,
                Region = s3Config.Region,
                Prefix = s3Config.Prefix,
                PublicUrl = s3Config.PublicUrl,
                PathStyle = s3Config.PathStyle
                // Note: AccessKey and SecretKey intentionally not saved here
            };
            await config.SaveAsync(PatchSyncConfig.GetDefaultConfigPath());
            AnsiConsole.MarkupLine($"[grey]Config saved to: {PatchSyncConfig.GetDefaultConfigPath()}[/]");
        }

        AnsiConsole.WriteLine();

        return await ExecuteAsync(buildDir, s3Config, compress, skipRaw);
    }

    private static async Task<S3Config> PromptS3ConfigAsync()
    {
        var provider = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[green]Select S3 provider:[/]")
                .AddChoices(
                    "Amazon S3",
                    "Cloudflare R2",
                    "Backblaze B2",
                    "DigitalOcean Spaces",
                    "MinIO",
                    "Other S3-compatible"));

        var (defaultEndpoint, defaultRegion, pathStyle) = provider switch
        {
            "Amazon S3" => ("https://s3.amazonaws.com", "us-east-1", false),
            "Cloudflare R2" => ("https://<account-id>.r2.cloudflarestorage.com", "auto", true),
            "Backblaze B2" => ("https://s3.us-west-002.backblazeb2.com", "us-west-002", true),
            "DigitalOcean Spaces" => ("https://nyc3.digitaloceanspaces.com", "nyc3", true),
            "MinIO" => ("http://localhost:9000", "us-east-1", true),
            _ => ("https://s3.example.com", "us-east-1", true)
        };

        var endpoint = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]S3 Endpoint URL:[/]")
                .DefaultValue(defaultEndpoint)
                .Validate(url =>
                {
                    if (!Uri.TryCreate(url, UriKind.Absolute, out _))
                        return ValidationResult.Error("Invalid URL");
                    return ValidationResult.Success();
                }));

        var bucket = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]Bucket name:[/]")
                .Validate(b => !string.IsNullOrWhiteSpace(b)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Bucket name is required")));

        var region = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]Region:[/]")
                .DefaultValue(defaultRegion));

        var accessKey = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]Access Key ID:[/]")
                .Validate(k => !string.IsNullOrWhiteSpace(k)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Access key is required")));

        var secretKey = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]Secret Access Key:[/]")
                .Secret()
                .Validate(k => !string.IsNullOrWhiteSpace(k)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Secret key is required")));

        var publicUrl = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]Public CDN URL[/] (for manifest base URL, optional):")
                .AllowEmpty());

        return new S3Config
        {
            Endpoint = endpoint,
            Bucket = bucket,
            Region = region,
            AccessKey = accessKey,
            SecretKey = secretKey,
            PublicUrl = string.IsNullOrEmpty(publicUrl) ? null : publicUrl,
            PathStyle = pathStyle
        };
    }

    private static async Task<int> ExecuteAsync(
        string buildDir,
        S3Config s3Config,
        bool compress,
        bool skipRaw)
    {
        // Validate build directory
        if (!Directory.Exists(buildDir))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Build directory not found: {buildDir}");
            return 1;
        }

        var manifestPath = Path.Combine(buildDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] No manifest.json found in {buildDir}");
            return 1;
        }

        // Collect files to upload
        var filesToUpload = new List<(string LocalPath, string RemotePath, string Category)>();

        // Manifest
        filesToUpload.Add((manifestPath, "manifest.json", "Manifest"));

        // Signatures
        var sigDir = Path.Combine(buildDir, "signatures");
        if (Directory.Exists(sigDir))
        {
            foreach (var sigFile in Directory.GetFiles(sigDir, "*.sig", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(buildDir, sigFile).Replace('\\', '/');
                filesToUpload.Add((sigFile, relativePath, "Signature"));
            }
        }

        // Original files (for range queries) - need to find them from source
        // For now, we'll assume they're in a 'files' subdirectory or at the same level
        if (!skipRaw)
        {
            // Look for source directory reference in manifest or just scan for non-sig/non-manifest files
            var rawDir = Path.Combine(buildDir, "files");
            if (Directory.Exists(rawDir))
            {
                foreach (var file in Directory.GetFiles(rawDir, "*", SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(rawDir, file).Replace('\\', '/');
                    filesToUpload.Add((file, $"files/{relativePath}", "Raw"));
                }
            }
        }

        // Compressed files (generate if needed)
        var compressedDir = Path.Combine(buildDir, "compressed");
        if (compress)
        {
            var rawDir = Path.Combine(buildDir, "files");
            if (Directory.Exists(rawDir))
            {
                Directory.CreateDirectory(compressedDir);

                await AnsiConsole.Progress()
                    .StartAsync(async ctx =>
                    {
                        var task = ctx.AddTask("Compressing files...");
                        var files = Directory.GetFiles(rawDir, "*", SearchOption.AllDirectories);

                        for (int i = 0; i < files.Length; i++)
                        {
                            var file = files[i];
                            var relativePath = Path.GetRelativePath(rawDir, file);
                            var compressedPath = Path.Combine(compressedDir, relativePath + ".gz");

                            var dir = Path.GetDirectoryName(compressedPath);
                            if (!string.IsNullOrEmpty(dir))
                                Directory.CreateDirectory(dir);

                            await using var input = File.OpenRead(file);
                            await using var output = File.Create(compressedPath);
                            await using var gzip = new GZipStream(output, CompressionLevel.Optimal);
                            await input.CopyToAsync(gzip);

                            task.Value = (double)(i + 1) / files.Length * 100;
                        }
                    });
            }
        }

        if (Directory.Exists(compressedDir))
        {
            foreach (var file in Directory.GetFiles(compressedDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(compressedDir, file).Replace('\\', '/');
                filesToUpload.Add((file, $"compressed/{relativePath}", "Compressed"));
            }
        }

        // Calculate total size
        var totalSize = filesToUpload.Sum(f => new FileInfo(f.LocalPath).Length);

        AnsiConsole.MarkupLine($"[blue]Uploading to:[/] {s3Config.Endpoint}");
        AnsiConsole.MarkupLine($"[blue]Bucket:[/] {s3Config.Bucket}");
        if (!string.IsNullOrEmpty(s3Config.Prefix))
            AnsiConsole.MarkupLine($"[blue]Prefix:[/] {s3Config.Prefix}");
        AnsiConsole.MarkupLine($"[blue]Files:[/] {filesToUpload.Count}");
        AnsiConsole.MarkupLine($"[blue]Total size:[/] {FormatBytes(totalSize)}");
        AnsiConsole.WriteLine();

        // Group files by category for display
        var byCategory = filesToUpload.GroupBy(f => f.Category);
        foreach (var group in byCategory)
        {
            var categorySize = group.Sum(f => new FileInfo(f.LocalPath).Length);
            AnsiConsole.MarkupLine($"  {group.Key}: {group.Count()} files ({FormatBytes(categorySize)})");
        }
        AnsiConsole.WriteLine();

        // Upload
        try
        {
            using var client = new S3Client(s3Config);
            var uploadedBytes = 0L;
            var uploadedFiles = 0;

            await AnsiConsole.Progress()
                .AutoRefresh(true)
                .HideCompleted(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new TransferSpeedColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var overallTask = ctx.AddTask($"Uploading ({filesToUpload.Count} files)");
                    overallTask.MaxValue = totalSize;

                    foreach (var (localPath, remotePath, category) in filesToUpload)
                    {
                        var fileSize = new FileInfo(localPath).Length;
                        var fileName = Path.GetFileName(localPath);

                        var progress = new Progress<UploadProgress>(p =>
                        {
                            overallTask.Value = uploadedBytes + p.BytesUploaded;
                            overallTask.Description = $"[{category}] {fileName}";
                        });

                        await client.UploadFileAsync(localPath, remotePath, progress: progress);

                        uploadedBytes += fileSize;
                        uploadedFiles++;
                    }

                    overallTask.Value = totalSize;
                    overallTask.Description = "Complete";
                });

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[green]Upload complete![/]");
            AnsiConsole.MarkupLine($"  Files uploaded: {uploadedFiles}");
            AnsiConsole.MarkupLine($"  Total size: {FormatBytes(totalSize)}");

            if (!string.IsNullOrEmpty(s3Config.PublicUrl))
            {
                var manifestUrl = $"{s3Config.PublicUrl.TrimEnd('/')}/{s3Config.Prefix?.Trim('/') ?? ""}/manifest.json".Replace("//", "/").TrimStart('/');
                AnsiConsole.MarkupLine($"  Manifest URL: {s3Config.PublicUrl.TrimEnd('/')}/{manifestUrl}");
            }

            return 0;
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]Upload failed:[/] {ex.Message}");
            return 1;
        }
    }

    private static void ShowHelp()
    {
        AnsiConsole.MarkupLine("[bold]patchsync upload[/] - Upload build artifacts to S3-compatible storage");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Usage:[/]");
        AnsiConsole.MarkupLine("  patchsync upload -b <build-dir> [[options]]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Required:[/]");
        AnsiConsole.MarkupLine("  -b, --build-dir <PATH>   Build output directory (from 'build' command)");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]S3 Options:[/]");
        AnsiConsole.MarkupLine("  -e, --endpoint <URL>     S3 endpoint URL");
        AnsiConsole.MarkupLine("      --bucket <NAME>      Bucket name");
        AnsiConsole.MarkupLine("      --access-key <KEY>   Access key ID");
        AnsiConsole.MarkupLine("      --secret-key <KEY>   Secret access key");
        AnsiConsole.MarkupLine("  -r, --region <REGION>    Region (default: us-east-1)");
        AnsiConsole.MarkupLine("  -p, --prefix <PATH>      Upload prefix (e.g., game/v1.0.0/)");
        AnsiConsole.MarkupLine("      --public-url <URL>   Public CDN URL for manifest");
        AnsiConsole.MarkupLine("      --path-style         Use path-style URLs (default: true)");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Upload Options:[/]");
        AnsiConsole.MarkupLine("      --compress           Compress files for fallback (default: true)");
        AnsiConsole.MarkupLine("      --skip-raw           Skip uploading raw files");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Credential Sources (in priority order):[/]");
        AnsiConsole.MarkupLine("  1. Command line arguments (--access-key, --secret-key)");
        AnsiConsole.MarkupLine("  2. Environment variables:");
        AnsiConsole.MarkupLine($"     {CredentialManager.EnvironmentVariables.AccessKey}");
        AnsiConsole.MarkupLine($"     {CredentialManager.EnvironmentVariables.SecretKey}");
        AnsiConsole.MarkupLine("  3. DPAPI-encrypted credential store (~/.patchsync/credentials.protected)");
        AnsiConsole.MarkupLine("  4. Config file (~/.patchsync/config.json) - NOT RECOMMENDED for secrets");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Run 'patchsync upload' without args for interactive mode with credential setup.[/]");
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
        };
    }
}
