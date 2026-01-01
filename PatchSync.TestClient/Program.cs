using PatchSync.SDK.Client;
using PatchSync.SDK.Engine;
using PatchSync.SDK.Storage;

namespace PatchSync.TestClient;

/// <summary>
/// Test client demonstrating PatchSync SDK usage.
/// This is a simple console app showing how to integrate the SDK into a launcher.
///
/// The SDK uses a phased approach for safe patching:
/// 1. Plan: Calculate delta plans for all files
/// 2. Assemble: Download/copy chunks to temp files (parallel, safe)
/// 3. Commit: Atomically swap temp files to final locations
/// 4. Verify: Hash-verify, fallback to full download on failure
/// </summary>
class Program
{
    static async Task<int> Main(string[] args)
    {
        if (args.Length < 1)
        {
            ShowUsage();
            return 1;
        }

        var command = args[0].ToLowerInvariant();

        // Handle test-tar command (no required args)
        if (command == "test-tar")
        {
            var outputDir = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "patchsync_tar_test");
            try
            {
                await DeltaAnalyzer.TestTarBehaviorAsync(outputDir);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        }

        if (args.Length < 2)
        {
            ShowUsage();
            return 1;
        }

        // Handle analyze command separately (different args)
        if (command == "analyze")
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: PatchSync.TestClient analyze <old-file> <new-file>");
                return 1;
            }
            try
            {
                await DeltaAnalyzer.AnalyzeAsync(args[1], args[2]);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        }

        // All other commands need 3 args
        if (args.Length < 3)
        {
            ShowUsage();
            return 1;
        }

        var serverUrl = args[1];
        var localPath = args[2];

        // Validate server URL
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var baseUri))
        {
            Console.WriteLine($"Error: Invalid server URL: {serverUrl}");
            return 1;
        }

        // Validate local path
        if (!Directory.Exists(localPath))
        {
            Console.WriteLine($"Error: Local path does not exist: {localPath}");
            Console.WriteLine("Create the directory first, or provide a valid path.");
            return 1;
        }

        Console.WriteLine($"PatchSync Test Client");
        Console.WriteLine($"Server: {baseUri}");
        Console.WriteLine($"Local:  {localPath}");
        Console.WriteLine();

        try
        {
            return command switch
            {
                "scan" => await RunScanAsync(baseUri, localPath),
                "patch" => await RunPatchAsync(baseUri, localPath),
                "verify" => await RunVerifyAsync(baseUri, localPath),
                "full" => await RunFullAsync(baseUri, localPath),
                _ => ShowUsage()
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"Error: {ex.Message}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"  Inner: {ex.InnerException.Message}");
            }
            return 1;
        }
    }

    static int ShowUsage()
    {
        Console.WriteLine("PatchSync Test Client - SDK Demo");
        Console.WriteLine();
        Console.WriteLine("Usage: PatchSync.TestClient <command> <server-url> <local-path>");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  scan     Check what files need updating (no downloads)");
        Console.WriteLine("  patch    Download and apply patches");
        Console.WriteLine("  verify   Verify all files match the manifest");
        Console.WriteLine("  full     Run scan, patch, and verify in sequence");
        Console.WriteLine("  analyze  Analyze delta between two local files");
        Console.WriteLine("  test-tar Test CDC behavior with TAR files");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  PatchSync.TestClient scan http://localhost:8080 C:\\Games\\MyGame");
        Console.WriteLine("  PatchSync.TestClient patch http://cdn.example.com C:\\Games\\MyGame");
        Console.WriteLine("  PatchSync.TestClient full http://localhost:8080 C:\\Games\\MyGame");
        Console.WriteLine("  PatchSync.TestClient analyze <old-file> <new-file>");
        Console.WriteLine("  PatchSync.TestClient test-tar [output-dir]");
        return 1;
    }

    static async Task<int> RunScanAsync(Uri baseUri, string localPath)
    {
        Console.WriteLine("=== SCAN ===");
        Console.WriteLine("Checking installation status...");
        Console.WriteLine();

        using var storage = new HttpStorageProvider(baseUri);
        using var client = new PatchSyncClient(storage);

        // Download manifest
        Console.Write("Downloading manifest... ");
        var manifest = await client.GetManifestAsync();
        Console.WriteLine($"OK ({manifest.Files.Count} files)");

        // Create progress handler
        var progress = new Progress<ScanProgress>(p =>
        {
            if (p.CurrentFile != null)
            {
                Console.Write($"\rScanning: {p.FilesScanned}/{p.FilesTotal} - {TruncatePath(p.CurrentFile, 40)}".PadRight(80));
            }
        });

        // Run scan
        var result = await client.ScanAsync(localPath, manifest, progress);
        Console.WriteLine();
        Console.WriteLine();

        // Display results
        Console.WriteLine("Scan Results:");
        Console.WriteLine($"  Up to date:   {result.UpToDate.Count,5} files");
        Console.WriteLine($"  Needs update: {result.NeedsUpdate.Count,5} files");
        Console.WriteLine($"  Missing:      {result.Missing.Count,5} files");
        Console.WriteLine($"  To delete:    {result.ToDelete.Count,5} files");
        Console.WriteLine();

        if (result.IsUpToDate)
        {
            Console.WriteLine("Installation is up to date!");
            return 0;
        }

        // Show strategy breakdown with delta estimates
        var breakdown = result.GetStrategyBreakdown();
        Console.WriteLine();
        Console.WriteLine("Download estimate:");
        Console.WriteLine($"  Worst case (no delta):  {FormatSize(breakdown.TotalWorstCaseBytes)}");
        Console.WriteLine($"  Estimated (with delta): {FormatSize(breakdown.TotalEstimatedBytes)}");
        Console.WriteLine($"  Estimated savings:      {FormatSize(breakdown.EstimatedSavings)} ({breakdown.SavingsPercentage:P0})");
        if (breakdown.TotalCoalesceOverhead > 0)
        {
            Console.WriteLine($"  Range coalesce overhead: ~{FormatSize(breakdown.TotalCoalesceOverhead)} (included in estimate)");
        }
        Console.WriteLine();
        Console.WriteLine("By strategy:");
        if (breakdown.DeltaFileCount > 0)
        {
            var deltaInfo = breakdown.DeltaCoalesceOverhead > 0
                ? $"~{FormatSize(breakdown.DeltaEstimatedBytes)} (incl. ~{FormatSize(breakdown.DeltaCoalesceOverhead)} coalesce)"
                : $"~{FormatSize(breakdown.DeltaEstimatedBytes)}";
            Console.WriteLine($"  Delta (CDC):        {breakdown.DeltaFileCount,4} files, {deltaInfo}");
        }
        if (breakdown.VirtualDeltaFileCount > 0)
        {
            var vdInfo = breakdown.VirtualDeltaCoalesceOverhead > 0
                ? $"~{FormatSize(breakdown.VirtualDeltaEstimatedBytes)} (incl. ~{FormatSize(breakdown.VirtualDeltaCoalesceOverhead)} coalesce)"
                : $"~{FormatSize(breakdown.VirtualDeltaEstimatedBytes)}";
            Console.WriteLine($"  VirtualDelta (UOP): {breakdown.VirtualDeltaFileCount,4} files, {vdInfo}");
        }
        if (breakdown.FullDownloadFileCount > 0)
            Console.WriteLine($"  Full download:      {breakdown.FullDownloadFileCount,4} files, {FormatSize(breakdown.FullDownloadBytes)}");

        // List files needing update
        if (result.NeedsUpdate.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Files needing update:");
            foreach (var file in result.NeedsUpdate.Take(10))
            {
                Console.WriteLine($"  {file.Path} ({FormatSize(file.Size)})");
            }
            if (result.NeedsUpdate.Count > 10)
            {
                Console.WriteLine($"  ... and {result.NeedsUpdate.Count - 10} more");
            }
        }

        if (result.Missing.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Missing files:");
            foreach (var file in result.Missing.Take(10))
            {
                Console.WriteLine($"  {file.Path} ({FormatSize(file.Size)})");
            }
            if (result.Missing.Count > 10)
            {
                Console.WriteLine($"  ... and {result.Missing.Count - 10} more");
            }
        }

        return result.IsUpToDate ? 0 : 2;
    }

    static async Task<int> RunPatchAsync(Uri baseUri, string localPath)
    {
        Console.WriteLine("=== PATCH (Phased) ===");
        Console.WriteLine("Using safe phased patching:");
        Console.WriteLine("  1. Plan     - Calculate delta plans");
        Console.WriteLine("  2. Assemble - Download to temp files (parallel)");
        Console.WriteLine("  3. Commit   - Atomic swap to final locations");
        Console.WriteLine("  4. Verify   - Hash check with fallback");
        Console.WriteLine();

        using var storage = new HttpStorageProvider(baseUri);
        using var client = new PatchSyncClient(storage, options: new PatchClientOptions
        {
            MaxConcurrency = Environment.ProcessorCount,
            VerifyAfterPatch = true
        });

        // Download manifest
        Console.Write("Downloading manifest... ");
        var manifest = await client.GetManifestAsync();
        Console.WriteLine($"OK ({manifest.Files.Count} files)");

        var startTime = DateTime.UtcNow;
        var lastReportTime = DateTime.UtcNow;
        var lastPhase = PatchPhase.Starting;
        var consoleLock = new object();
        var progressBarActive = false;
        const int ProgressBarWidth = 110;

        // Helper to clear progress bar and print a line
        void ClearProgressAndPrint(string message)
        {
            if (progressBarActive)
            {
                // Move to start of line, clear it, then print
                Console.Write($"\r{new string(' ', ProgressBarWidth)}\r");
            }
            Console.WriteLine(message);
            progressBarActive = false;
        }

        // Helper to write progress bar (overwrites current line)
        void WriteProgressBar(string progressLine)
        {
            Console.Write($"\r{progressLine.PadRight(ProgressBarWidth)}");
            Console.Out.Flush();
            progressBarActive = true;
        }

        // Create progress handler
        var progress = new Progress<PatchProgress>(p =>
        {
            var now = DateTime.UtcNow;

            lock (consoleLock)
            {
                // Always report phase changes
                if (p.Phase != lastPhase)
                {
                    if (progressBarActive)
                    {
                        ClearProgressAndPrint(""); // Clear and newline
                    }
                    else
                    {
                        Console.WriteLine();
                    }

                    lastPhase = p.Phase;

                    switch (p.Phase)
                    {
                        case PatchPhase.Starting:
                            Console.WriteLine($"[PLAN] Calculating delta plans for {p.FilesTotal} files...");
                            break;
                        case PatchPhase.Processing:
                            Console.WriteLine($"[ASSEMBLE] Downloading and assembling files...");
                            break;
                        case PatchPhase.Verifying:
                            Console.WriteLine($"[VERIFY] Verifying file integrity...");
                            break;
                    }
                }

                // Log file completions and failures
                if (p.CurrentFile != null && (p.CurrentFile.StartsWith("Completed:") || p.CurrentFile.StartsWith("FAILED:")))
                {
                    var prefix = p.CurrentFile.StartsWith("FAILED:") ? "  [ERROR] " : "  ";
                    ClearProgressAndPrint($"{prefix}{p.CurrentFile}");
                    return;
                }

                if ((now - lastReportTime).TotalMilliseconds < 100 && p.Phase != PatchPhase.Complete)
                    return;
                lastReportTime = now;

                var elapsed = now - startTime;
                var speed = elapsed.TotalSeconds > 0 ? p.BytesComplete / elapsed.TotalSeconds : 0;

                switch (p.Phase)
                {
                    case PatchPhase.Starting:
                        if (p.CurrentFile != null)
                        {
                            var planPct = p.FilesTotal > 0 ? (double)p.FilesComplete / p.FilesTotal * 100 : 0;
                            WriteProgressBar($"  Planning: {p.FilesComplete}/{p.FilesTotal} ({planPct:F0}%) - {TruncatePath(p.CurrentFile, 35)}");
                        }
                        break;

                    case PatchPhase.Processing:
                        // Use file-based percentage for more accurate progress (byte totals can exceed estimates)
                        var filePct = p.FilesTotal > 0 ? (double)p.FilesComplete / p.FilesTotal : 0;
                        var bar = BuildProgressBar(filePct, 20);
                        var fileInfo = p.CurrentFile != null ? TruncatePath(p.CurrentFile, 20) : "";
                        WriteProgressBar($"  {bar} {filePct * 100,5:F1}% | {FormatSize((long)speed)}/s | {FormatSize(p.BytesDownloaded)}↓ {FormatSize(p.BytesCopied)}↔ | {p.FilesComplete}/{p.FilesTotal} | {fileInfo}");
                        break;

                    case PatchPhase.Verifying:
                        var verifyPct = p.FilesTotal > 0 ? (double)p.FilesComplete / p.FilesTotal * 100 : 0;
                        WriteProgressBar($"  Verified: {p.FilesComplete}/{p.FilesTotal} ({verifyPct:F0}%)");
                        break;

                    case PatchPhase.Complete:
                        ClearProgressAndPrint("");
                        Console.WriteLine("[COMMIT] Changes applied successfully!");
                        Console.WriteLine();
                        Console.WriteLine($"Patch complete!");
                        Console.WriteLine($"  Files processed: {p.FilesComplete}");
                        Console.WriteLine($"  Time elapsed:    {elapsed:mm\\:ss\\.f}");
                        Console.WriteLine($"  Average speed:   {FormatSize((long)speed)}/s");
                        Console.WriteLine();
                        Console.WriteLine("Download statistics:");
                        Console.WriteLine($"  Downloaded:      {FormatSize(p.BytesDownloaded)}");
                        Console.WriteLine($"  Copied locally:  {FormatSize(p.BytesCopied)}");
                        Console.WriteLine($"  Total processed: {FormatSize(p.BytesComplete)}");
                        if (p.BytesComplete > 0)
                        {
                            Console.WriteLine($"  Local reuse:     {p.LocalReusePercentage:P1}");
                            Console.WriteLine($"  Bandwidth saved: {FormatSize(p.BytesSaved)}");
                        }
                        break;
                }
            }
        });

        // Run patch
        await client.PatchAsync(localPath, manifest, progress);

        return 0;
    }

    static async Task<int> RunVerifyAsync(Uri baseUri, string localPath)
    {
        Console.WriteLine("=== VERIFY ===");
        Console.WriteLine("Verifying installation...");
        Console.WriteLine();

        using var storage = new HttpStorageProvider(baseUri);
        using var client = new PatchSyncClient(storage);

        // Download manifest
        Console.Write("Downloading manifest... ");
        var manifest = await client.GetManifestAsync();
        Console.WriteLine($"OK ({manifest.Files.Count} files)");

        // Create progress handler
        var progress = new Progress<VerifyProgress>(p =>
        {
            if (p.CurrentFile != null)
            {
                var bar = BuildProgressBar(p.Percentage, 20);
                Console.Write($"\r{bar} {p.FilesVerified}/{p.FilesTotal} | Pass: {p.FilesPassed} Fail: {p.FilesFailed} | {TruncatePath(p.CurrentFile, 30)}".PadRight(100));
            }
        });

        // Run verify
        var result = await client.VerifyAsync(localPath, manifest, progress);
        Console.WriteLine();
        Console.WriteLine();

        // Display results
        Console.WriteLine("Verification Results:");
        Console.WriteLine($"  Passed: {result.Passed.Count,5} files");
        Console.WriteLine($"  Failed: {result.Failed.Count,5} files");
        Console.WriteLine();

        if (result.Success)
        {
            Console.WriteLine("All files verified successfully!");
            return 0;
        }

        Console.WriteLine("Failed files:");
        foreach (var file in result.Failed.Take(20))
        {
            var status = file.Status switch
            {
                FileStatusType.Missing => "MISSING",
                FileStatusType.NeedsUpdate => "MISMATCH",
                _ => file.Status.ToString().ToUpper()
            };
            Console.WriteLine($"  [{status}] {file.Path}");
        }
        if (result.Failed.Count > 20)
        {
            Console.WriteLine($"  ... and {result.Failed.Count - 20} more");
        }

        return 1;
    }

    static async Task<int> RunFullAsync(Uri baseUri, string localPath)
    {
        Console.WriteLine("=== FULL UPDATE ===");
        Console.WriteLine();

        // Step 1: Scan
        var scanResult = await RunScanAsync(baseUri, localPath);
        Console.WriteLine();

        if (scanResult == 0)
        {
            Console.WriteLine("No updates needed, skipping patch.");
        }
        else
        {
            // Step 2: Patch
            Console.WriteLine(new string('-', 60));
            Console.WriteLine();
            var patchResult = await RunPatchAsync(baseUri, localPath);
            if (patchResult != 0)
            {
                Console.WriteLine("Patch failed!");
                return patchResult;
            }
        }

        // Step 3: Verify
        Console.WriteLine();
        Console.WriteLine(new string('-', 60));
        Console.WriteLine();
        var verifyResult = await RunVerifyAsync(baseUri, localPath);

        Console.WriteLine();
        Console.WriteLine(new string('=', 60));
        Console.WriteLine();
        if (verifyResult == 0)
        {
            Console.WriteLine("Update complete! Installation verified successfully.");
        }
        else
        {
            Console.WriteLine("Update complete but verification failed. You may need to re-run.");
        }

        return verifyResult;
    }

    static string BuildProgressBar(double percentage, int width)
    {
        var filled = (int)Math.Round(percentage * width);
        if (percentage >= 1.0) filled = width;
        var empty = width - filled;
        return $"[{new string('█', filled)}{new string('░', empty)}]";
    }

    static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    static string TruncatePath(string path, int maxLength)
    {
        if (path.Length <= maxLength) return path;
        return "..." + path[^(maxLength - 3)..];
    }
}
