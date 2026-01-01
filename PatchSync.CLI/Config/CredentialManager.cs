using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace PatchSync.CLI.Config;

/// <summary>
/// Manages S3 credentials with DPAPI encryption on Windows.
///
/// SECURITY WARNING: DPAPI encryption protects credentials at rest (stolen hard drive,
/// copied files, other users on the machine). However, ANY PROGRAM RUNNING AS YOUR USER
/// can decrypt these credentials. This includes malware, malicious plugins, "cheat" programs,
/// or any code you execute. Do not rely on this for high-security scenarios.
///
/// For better security, use environment variables in CI/CD or enter credentials each session.
/// </summary>
public static class CredentialManager
{
    private const string CredentialsFileName = "credentials.protected";

    /// <summary>
    /// Environment variable names for credential fallback.
    /// </summary>
    public static class EnvironmentVariables
    {
        public const string Endpoint = "PATCHSYNC_S3_ENDPOINT";
        public const string Bucket = "PATCHSYNC_S3_BUCKET";
        public const string Region = "PATCHSYNC_S3_REGION";
        public const string AccessKey = "PATCHSYNC_S3_ACCESS_KEY";
        public const string SecretKey = "PATCHSYNC_S3_SECRET_KEY";
        public const string Prefix = "PATCHSYNC_S3_PREFIX";
        public const string PublicUrl = "PATCHSYNC_S3_PUBLIC_URL";
    }

    /// <summary>
    /// Gets the path to the encrypted credentials file.
    /// </summary>
    public static string GetCredentialsPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".patchsync",
            CredentialsFileName);
    }

    /// <summary>
    /// Loads S3 credentials from (in priority order):
    /// 1. Environment variables
    /// 2. DPAPI-encrypted file (Windows only)
    /// Returns null if no credentials found.
    /// </summary>
    public static S3Credentials? LoadCredentials()
    {
        // Try environment variables first (cross-platform)
        var envCreds = LoadFromEnvironment();
        if (envCreds != null)
            return envCreds;

        // Try encrypted file (Windows only)
        if (OperatingSystem.IsWindows())
        {
            return LoadFromEncryptedFile();
        }

        return null;
    }

    /// <summary>
    /// Loads credentials from environment variables.
    /// </summary>
    public static S3Credentials? LoadFromEnvironment()
    {
        var accessKey = Environment.GetEnvironmentVariable(EnvironmentVariables.AccessKey);
        var secretKey = Environment.GetEnvironmentVariable(EnvironmentVariables.SecretKey);

        if (string.IsNullOrEmpty(accessKey) || string.IsNullOrEmpty(secretKey))
            return null;

        return new S3Credentials
        {
            Endpoint = Environment.GetEnvironmentVariable(EnvironmentVariables.Endpoint),
            Bucket = Environment.GetEnvironmentVariable(EnvironmentVariables.Bucket),
            Region = Environment.GetEnvironmentVariable(EnvironmentVariables.Region) ?? "us-east-1",
            AccessKey = accessKey,
            SecretKey = secretKey,
            Prefix = Environment.GetEnvironmentVariable(EnvironmentVariables.Prefix),
            PublicUrl = Environment.GetEnvironmentVariable(EnvironmentVariables.PublicUrl)
        };
    }

    /// <summary>
    /// Loads credentials from DPAPI-encrypted file.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static S3Credentials? LoadFromEncryptedFile()
    {
        var path = GetCredentialsPath();
        if (!File.Exists(path))
            return null;

        try
        {
            var encryptedBytes = File.ReadAllBytes(path);
            var decryptedBytes = ProtectedData.Unprotect(
                encryptedBytes,
                optionalEntropy: null,
                scope: DataProtectionScope.CurrentUser);

            var json = Encoding.UTF8.GetString(decryptedBytes);
            return JsonSerializer.Deserialize(json, CredentialJsonContext.Default.S3Credentials);
        }
        catch (CryptographicException)
        {
            // File was encrypted by different user or corrupted
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Saves credentials to DPAPI-encrypted file.
    /// Shows security warning before saving.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void SaveCredentials(S3Credentials credentials, bool showWarning = true)
    {
        if (showWarning)
        {
            ShowSecurityWarning();

            var proceed = AnsiConsole.Confirm(
                "Do you understand and want to save credentials anyway?", false);

            if (!proceed)
            {
                AnsiConsole.MarkupLine("[yellow]Credentials not saved.[/]");
                return;
            }
        }

        var path = GetCredentialsPath();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(credentials, CredentialJsonContext.Default.S3Credentials);
        var plainBytes = Encoding.UTF8.GetBytes(json);
        var encryptedBytes = ProtectedData.Protect(
            plainBytes,
            optionalEntropy: null,
            scope: DataProtectionScope.CurrentUser);

        File.WriteAllBytes(path, encryptedBytes);

        AnsiConsole.MarkupLine($"[green]Credentials saved to:[/] {path}");
    }

    /// <summary>
    /// Deletes stored credentials.
    /// </summary>
    public static bool DeleteCredentials()
    {
        var path = GetCredentialsPath();
        if (File.Exists(path))
        {
            File.Delete(path);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Checks if credentials are stored (Windows only).
    /// </summary>
    public static bool HasStoredCredentials()
    {
        return OperatingSystem.IsWindows() && File.Exists(GetCredentialsPath());
    }

    /// <summary>
    /// Checks if DPAPI credential storage is available on this platform.
    /// </summary>
    public static bool IsSecureStorageAvailable => OperatingSystem.IsWindows();

    /// <summary>
    /// Shows security warning about DPAPI limitations.
    /// </summary>
    public static void ShowSecurityWarning()
    {
        var panel = new Panel(
            "[yellow]SECURITY WARNING[/]\n\n" +
            "Credentials will be encrypted using Windows DPAPI (Data Protection API).\n" +
            "This protects against:\n" +
            "  [green]✓[/] Other users on this machine\n" +
            "  [green]✓[/] Stolen/copied files\n" +
            "  [green]✓[/] Files moved to another machine\n\n" +
            "[red]This does NOT protect against:[/]\n" +
            "  [red]✗[/] Malware running as your user\n" +
            "  [red]✗[/] Malicious plugins or mods\n" +
            "  [red]✗[/] Any program you execute\n" +
            "  [red]✗[/] \"Cheat\" programs or untrusted tools\n\n" +
            "[grey]For CI/CD, use environment variables instead:\n" +
            $"  {EnvironmentVariables.AccessKey}\n" +
            $"  {EnvironmentVariables.SecretKey}[/]")
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Yellow)
        };

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Merges credentials with config, preferring explicit values.
    /// Priority: CLI args > Environment > Encrypted file > Config file
    /// </summary>
    public static S3Config MergeWithConfig(
        S3Config? config,
        string? endpoint = null,
        string? bucket = null,
        string? accessKey = null,
        string? secretKey = null,
        string? region = null,
        string? prefix = null,
        string? publicUrl = null,
        bool? pathStyle = null)
    {
        // Load stored credentials as base
        var stored = LoadCredentials();

        return new S3Config
        {
            // Priority: CLI arg > stored > config
            Endpoint = endpoint ?? stored?.Endpoint ?? config?.Endpoint,
            Bucket = bucket ?? stored?.Bucket ?? config?.Bucket,
            AccessKey = accessKey ?? stored?.AccessKey ?? config?.AccessKey,
            SecretKey = secretKey ?? stored?.SecretKey ?? config?.SecretKey,
            Region = region ?? stored?.Region ?? config?.Region ?? "us-east-1",
            Prefix = prefix ?? stored?.Prefix ?? config?.Prefix,
            PublicUrl = publicUrl ?? stored?.PublicUrl ?? config?.PublicUrl,
            PathStyle = pathStyle ?? config?.PathStyle ?? true
        };
    }
}

/// <summary>
/// S3 credentials for secure storage.
/// Separate from S3Config to avoid accidentally serializing secrets to plaintext config.
/// </summary>
public sealed class S3Credentials
{
    public string? Endpoint { get; set; }
    public string? Bucket { get; set; }
    public string? Region { get; set; }
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
    public string? Prefix { get; set; }
    public string? PublicUrl { get; set; }

    /// <summary>Cloudflare API token for cache purging</summary>
    public string? CloudflareToken { get; set; }
}

/// <summary>
/// AOT-compatible JSON context for credentials.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(S3Credentials))]
internal partial class CredentialJsonContext : JsonSerializerContext;
