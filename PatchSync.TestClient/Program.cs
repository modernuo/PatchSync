using System.Security.Cryptography;
using PatchSync.Common.Chunking;
using PatchSync.Common.Signatures;
using PatchSync.SDK.Assembly;
using PatchSync.SDK.Client;
using PatchSync.SDK.Containers.Handlers;
using PatchSync.SDK.Delta;
using PatchSync.SDK.Sources;
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

        // Handle compare-uop command
        if (command == "compare-uop" && args.Length >= 3)
        {
            var oldFile = args[1];
            var newFile = args[2];
            return CompareUopEntries(oldFile, newFile);
        }

        // Handle analyze-gaps command
        if (command == "analyze-gaps" && args.Length >= 2)
        {
            var uopFile = args[1];
            return AnalyzeUopGaps(uopFile);
        }

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

        // Handle diag command (needs 4 args)
        if (command == "diag")
        {
            if (args.Length < 4)
            {
                Console.WriteLine("Usage: PatchSync.TestClient diag <server-url> <local-path> <filename>");
                Console.WriteLine("Example: PatchSync.TestClient diag https://cdn.example.com C:\\Game map2LegacyMUL.uop");
                return 1;
            }
            var fileName = args[3];
            return await RunDiagnosticAsync(baseUri, localPath, fileName);
        }

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
        Console.WriteLine("  diag     Diagnostic: patch a single file with detailed output");
        Console.WriteLine("  analyze  Analyze delta between two local files");
        Console.WriteLine("  analyze-gaps  Analyze gaps between entries in UOP file");
        Console.WriteLine("  test-tar Test CDC behavior with TAR files");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  PatchSync.TestClient scan http://localhost:8080 C:\\Games\\MyGame");
        Console.WriteLine("  PatchSync.TestClient patch http://cdn.example.com C:\\Games\\MyGame");
        Console.WriteLine("  PatchSync.TestClient full http://localhost:8080 C:\\Games\\MyGame");
        Console.WriteLine("  PatchSync.TestClient analyze <old-file> <new-file>");
        Console.WriteLine("  PatchSync.TestClient test-tar [output-dir]");
        Console.WriteLine("  PatchSync.TestClient diag https://cdn.example.com C:\\Game map2LegacyMUL.uop");
        Console.WriteLine("  PatchSync.TestClient analyze-gaps map0LegacyMUL.uop");
        return 1;
    }

    static async Task<int> RunDiagnosticAsync(Uri baseUri, string localPath, string fileName)
    {
        Console.WriteLine($"=== DIAGNOSTIC: {fileName} ===");
        Console.WriteLine();

        using var storage = new HttpStorageProvider(baseUri);
        using var client = new PatchSyncClient(storage);

        // Step 1: Download manifest
        Console.Write("1. Downloading manifest... ");
        var manifest = await client.GetManifestAsync();
        Console.WriteLine($"OK ({manifest.Files.Count} files)");

        // Step 2: Find the file in manifest
        var fileEntry = manifest.Files.FirstOrDefault(f =>
            f.Path.Equals(fileName, StringComparison.OrdinalIgnoreCase));

        if (fileEntry == null)
        {
            Console.WriteLine($"   ERROR: File '{fileName}' not found in manifest");
            Console.WriteLine("   Available UOP files:");
            foreach (var f in manifest.Files.Where(x => x.Path.EndsWith(".uop", StringComparison.OrdinalIgnoreCase)).Take(10))
            {
                Console.WriteLine($"     - {f.Path}");
            }
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("2. File info from manifest:");
        Console.WriteLine($"   Path:     {fileEntry.Path}");
        Console.WriteLine($"   Size:     {FormatSize(fileEntry.Size)} ({fileEntry.Size:N0} bytes)");
        Console.WriteLine($"   Hash:     {fileEntry.Hash}");
        Console.WriteLine($"   Strategy: {fileEntry.Strategy}");
        if (!string.IsNullOrEmpty(fileEntry.VirtualSignatureUrl))
            Console.WriteLine($"   VSig URL: {fileEntry.VirtualSignatureUrl}");

        // Step 3: Check local file
        var localFilePath = Path.Combine(localPath, fileEntry.Path);
        Console.WriteLine();
        Console.WriteLine("3. Local file status:");
        if (File.Exists(localFilePath))
        {
            var localInfo = new FileInfo(localFilePath);
            Console.WriteLine($"   Path:   {localFilePath}");
            Console.WriteLine($"   Size:   {FormatSize(localInfo.Length)} ({localInfo.Length:N0} bytes)");

            // Compute hash
            await using var fs = File.OpenRead(localFilePath);
            var localHash = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
            Console.WriteLine($"   Hash:   {localHash}");
            Console.WriteLine($"   Match:  {(localHash == fileEntry.Hash ? "YES (up to date)" : "NO (needs update)")}");

            // Debug: show first 64 bytes of local file
            fs.Position = 0;
            var headerBytes = new byte[64];
            await fs.ReadAsync(headerBytes);
            Console.WriteLine($"   LocalFile[0:64]: {Convert.ToHexString(headerBytes)}");

            // Parse UOP header
            var nextBlockOffset = BitConverter.ToInt64(headerBytes, 12);
            Console.WriteLine($"   NextBlockOffset (from header): {nextBlockOffset} (0x{nextBlockOffset:X})");
        }
        else
        {
            Console.WriteLine($"   File does not exist: {localFilePath}");
        }

        // Step 4: Download and parse virtual signature
        if (string.IsNullOrEmpty(fileEntry.VirtualSignatureUrl))
        {
            Console.WriteLine();
            Console.WriteLine("4. No virtual signature URL - cannot continue diagnostic");
            return 1;
        }

        // Initialize container handler and chunker (used in multiple steps)
        var handler = new UopHandler();
        var chunker = new FastCDCChunker();
        var options = new ChunkingOptions();

        // Step 4: Download target signature from CDN
        Console.WriteLine();
        Console.Write("4. Downloading virtual signature from CDN... ");

        await using var vsigStream = await storage.GetAsync(fileEntry.VirtualSignatureUrl);
        var signature = VirtualSignatureFormat.Read(vsigStream);
        Console.WriteLine("OK");

        Console.WriteLine($"   Format:       {signature.ContainerFormat}");
        Console.WriteLine($"   Total size:   {FormatSize(signature.TotalSize)} ({signature.TotalSize:N0} bytes)");
        Console.WriteLine($"   Container hash: {Convert.ToHexString(signature.ContainerHash).ToLowerInvariant()}");
        Console.WriteLine($"   Entry count:  {signature.Entries.Count}");
        Console.WriteLine($"   HeaderTemplate size: {signature.Layout.HeaderTemplate.Length} bytes");

        // Debug: show first 64 bytes of HeaderTemplate
        Console.WriteLine($"   HeaderTemplate[0:64]: {Convert.ToHexString(signature.Layout.HeaderTemplate.Take(64).ToArray())}");

        var entriesWithChunks = signature.Entries.Count(e => e.HasChunks);
        Console.WriteLine($"   With CDC chunks: {entriesWithChunks}");

        // Step 5: Build local source and calculate delta
        Console.WriteLine();
        Console.WriteLine("5. Building delta plan...");

        var calculator = new VirtualDeltaCalculator();

        VirtualContainerSource? localSource = null;
        if (File.Exists(localFilePath))
        {
            Console.Write("   Building local container source... ");
            localSource = VirtualContainerSource.Build(localFilePath, handler, chunker, options);
            Console.WriteLine($"OK ({localSource.EntryCount} entries, {localSource.ChunkCount} chunks)");
        }
        else
        {
            Console.WriteLine("   No local file - will be full download");
        }

        var plan = calculator.Calculate(signature, localSource);

        Console.WriteLine();
        Console.WriteLine("6. Delta plan summary:");
        Console.WriteLine($"   Total entries:    {plan.Entries.Count}");
        Console.WriteLine($"   CopyLocal:        {plan.EntriesLocalMatch} entries");
        Console.WriteLine($"   DeltaChunks:      {plan.EntriesDeltaChunks} entries");
        Console.WriteLine($"   DownloadFull:     {plan.EntriesFullDownload} entries");
        Console.WriteLine();
        Console.WriteLine($"   Bytes to download: {FormatSize(plan.BytesToDownload)} ({plan.BytesToDownload:N0})");
        Console.WriteLine($"   Bytes to copy:     {FormatSize(plan.BytesToCopy)} ({plan.BytesToCopy:N0})");
        Console.WriteLine($"   Local reuse:       {plan.LocalReuseRatio:P1}");

        // Show first few entries of each type
        Console.WriteLine();
        Console.WriteLine("7. Sample entries by method:");

        var copyLocalEntries = plan.Entries.Where(e => e.Method == EntryMethod.CopyLocal).Take(3).ToList();
        if (copyLocalEntries.Count > 0)
        {
            Console.WriteLine("   CopyLocal entries:");
            foreach (var e in copyLocalEntries)
            {
                Console.WriteLine($"     [{e.EntryId}] offset={e.TargetOffset:N0} size={e.Size:N0} hdrSize={e.HeaderSize}");
                Console.WriteLine($"       LocalSource: offset={e.LocalSource?.Offset:N0}");
            }
        }

        // Verify CopyLocal entry hashes match
        if (File.Exists(localFilePath))
        {
            Console.WriteLine();
            Console.WriteLine("   Verifying CopyLocal entries hash integrity...");
            await using var localFs = File.OpenRead(localFilePath);
            var mismatchCount = 0;

            // Look up target StoredHash for each CopyLocal entry
            var targetHashByEntryId = signature.Entries.ToDictionary(e => e.EntryId, e => e.StoredHash);

            foreach (var e in plan.Entries.Where(ep => ep.Method == EntryMethod.CopyLocal))
            {
                if (!e.LocalSource.HasValue) continue;
                var src = e.LocalSource.Value;

                // Read local data at the source offset
                localFs.Position = src.Offset;
                var localData = new byte[src.Size];
                await localFs.ReadAsync(localData);

                // Compute hash of local data
                var localHash = SHA256.HashData(localData);
                var localHashHex = Convert.ToHexString(localHash).ToLowerInvariant();

                // Get expected hash and size from target signature
                var targetEntry = signature.Entries.FirstOrDefault(te => te.EntryId == e.EntryId);
                if (targetEntry != null)
                {
                    var expectedHashHex = Convert.ToHexString(targetEntry.StoredHash).ToLowerInvariant();

                    // Check for size mismatch first
                    if (src.Size != targetEntry.StoredSize)
                    {
                        Console.WriteLine($"     SIZE MISMATCH for [{e.EntryId}]:");
                        Console.WriteLine($"       LocalSource.Size: {src.Size:N0}");
                        Console.WriteLine($"       TargetEntry.StoredSize: {targetEntry.StoredSize:N0}");
                        Console.WriteLine($"       EntryPlan.Size: {e.Size:N0}");
                    }

                    if (localHashHex != expectedHashHex)
                    {
                        mismatchCount++;
                        if (mismatchCount <= 5)
                        {
                            Console.WriteLine($"     HASH MISMATCH for [{e.EntryId}]:");
                            Console.WriteLine($"       LocalSource.Offset: {src.Offset:N0}, LocalSource.Size: {src.Size:N0}");
                            Console.WriteLine($"       EntryPlan.Size (target): {e.Size:N0}");
                            Console.WriteLine($"       Local data hash:    {localHashHex}");
                            Console.WriteLine($"       Expected (target):  {expectedHashHex}");
                            Console.WriteLine($"       Local data[0:32]:   {Convert.ToHexString(localData.Take(32).ToArray())}");

                            // Also show what hash we indexed for this local entry
                            Console.WriteLine($"       Note: The hash lookup matched, so the indexed hash should equal expected");
                        }
                    }
                }
            }

            if (mismatchCount > 0)
            {
                Console.WriteLine($"     TOTAL HASH MISMATCHES: {mismatchCount}");
            }
            else
            {
                Console.WriteLine("     All CopyLocal entries have matching hashes.");
            }
        }

        var deltaEntries = plan.Entries.Where(e => e.Method == EntryMethod.DeltaChunks).Take(3).ToList();
        if (deltaEntries.Count > 0)
        {
            Console.WriteLine("   DeltaChunks entries:");
            foreach (var e in deltaEntries)
            {
                var localChunks = e.ChunkPlan?.Actions.OfType<CopyLocal>().Count() ?? 0;
                var remoteChunks = e.ChunkPlan?.Actions.OfType<DownloadRemote>().Count() ?? 0;
                Console.WriteLine($"     [{e.EntryId}] offset={e.TargetOffset:N0} size={e.Size:N0} hdrSize={e.HeaderSize}");
                Console.WriteLine($"       Chunks: {localChunks} local, {remoteChunks} remote");
            }
        }

        var downloadEntries = plan.Entries.Where(e => e.Method == EntryMethod.DownloadFull).Take(3).ToList();
        if (downloadEntries.Count > 0)
        {
            Console.WriteLine("   DownloadFull entries:");
            foreach (var e in downloadEntries)
            {
                Console.WriteLine($"     [{e.EntryId}] offset={e.TargetOffset:N0} size={e.Size:N0} hdrSize={e.HeaderSize}");
            }
        }

        // Step 8: Assemble the file
        Console.WriteLine();
        Console.WriteLine("8. Assembling file...");

        var targetPath = localFilePath + ".diag";
        var remotePath = fileEntry.Path; // Relative path for CDN

        var assemblyOptions = new AssemblyOptions
        {
            PreserveTempOnFailure = true,
            SkipVerification = true // We'll verify manually for better diagnostics
        };
        var assembler = new ContainerAssembler(storage, assemblyOptions);

        var progress = new Progress<ContainerAssemblyProgress>(p =>
        {
            var pct = p.Percentage * 100;
            Console.Write($"\r   Phase: {p.Phase,-12} | {pct,5:F1}% | {FormatSize(p.BytesDownloaded)}↓ {FormatSize(p.BytesCopied)}↔ | Entry {p.EntriesComplete}/{p.EntriesTotal}".PadRight(100));
        });

        try
        {
            await assembler.AssembleAsync(
                targetPath,
                remotePath,
                plan,
                File.Exists(localFilePath) ? localFilePath : null,
                progress);

            Console.WriteLine();
            Console.WriteLine("   Assembly completed!");
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"   Assembly exception: {ex.Message}");
            return 1;
        }

        // Step 9: Verify the assembled file
        Console.WriteLine();
        Console.WriteLine("9. Verifying assembled file...");

        var diagInfo = new FileInfo(targetPath);
        Console.WriteLine($"   File size: {FormatSize(diagInfo.Length)} ({diagInfo.Length:N0} bytes)");
        Console.WriteLine($"   Expected:  {FormatSize(fileEntry.Size)} ({fileEntry.Size:N0} bytes)");

        await using var diagFs = File.OpenRead(targetPath);
        var diagHash = Convert.ToHexString(await SHA256.HashDataAsync(diagFs)).ToLowerInvariant();
        Console.WriteLine($"   Hash:     {diagHash}");
        Console.WriteLine($"   Expected: {fileEntry.Hash}");

        var hashMatch = diagHash == fileEntry.Hash;
        Console.WriteLine($"   Match:    {(hashMatch ? "YES - SUCCESS!" : "NO - MISMATCH")}");

        if (!hashMatch)
        {
            // Find first differing byte
            Console.WriteLine();
            Console.WriteLine("10. Finding first byte difference...");

            diagFs.Position = 0;
            await using var expectedFs = await storage.GetAsync(remotePath);

            var buffer1 = new byte[65536];
            var buffer2 = new byte[65536];
            long offset = 0;
            bool foundDiff = false;
            int diffCount = 0;

            // Copy expected stream to memory for proper comparison (avoid partial reads from HTTP stream)
            var expectedMs = new MemoryStream();
            await expectedFs.CopyToAsync(expectedMs);
            expectedMs.Position = 0;

            while (!foundDiff || diffCount < 5)
            {
                var read1 = await diagFs.ReadAsync(buffer1);
                var read2 = await expectedMs.ReadAsync(buffer2.AsMemory(0, read1));

                if (read1 == 0) break;
                if (read2 != read1) break;

                for (int i = 0; i < read1; i++)
                {
                    if (buffer1[i] != buffer2[i])
                    {
                        diffCount++;
                        if (diffCount <= 5)
                        {
                            var diffOffset = offset + i;
                            Console.WriteLine($"   Diff #{diffCount} at offset {diffOffset:N0} (0x{diffOffset:X})");
                            Console.WriteLine($"     Got: 0x{buffer1[i]:X2}, Expected: 0x{buffer2[i]:X2}");

                            // Find which entry this belongs to
                            var entry = plan.Entries.FirstOrDefault(e =>
                                diffOffset >= e.HeaderOffset && diffOffset < e.HeaderOffset + e.TotalSize);

                            if (entry != null)
                            {
                                Console.WriteLine($"     Entry: [{entry.EntryId}] method={entry.Method}");
                                Console.WriteLine($"       HeaderOffset: {entry.HeaderOffset:N0}, TargetOffset: {entry.TargetOffset:N0}");
                                Console.WriteLine($"       HeaderSize: {entry.HeaderSize}, Size: {entry.Size:N0}");

                                var relativeOffset = diffOffset - entry.HeaderOffset;
                                if (relativeOffset < entry.HeaderSize)
                                {
                                    Console.WriteLine($"       Diff is in HEADER (relative offset {relativeOffset})");
                                }
                                else
                                {
                                    Console.WriteLine($"       Diff is in DATA (relative offset {relativeOffset - entry.HeaderSize})");
                                }

                                if (entry.LocalSource.HasValue)
                                {
                                    Console.WriteLine($"       LocalSource.Offset: {entry.LocalSource.Value.Offset:N0}");
                                }
                            }
                            else
                            {
                                Console.WriteLine($"     Not in any entry - likely container header/block table");
                                // Find nearby entries
                                var prevEntry = plan.Entries.Where(e => e.HeaderOffset + e.TotalSize <= diffOffset)
                                    .OrderByDescending(e => e.HeaderOffset + e.TotalSize)
                                    .FirstOrDefault();
                                var nextEntry = plan.Entries.Where(e => e.HeaderOffset > diffOffset)
                                    .OrderBy(e => e.HeaderOffset)
                                    .FirstOrDefault();
                                if (prevEntry != null)
                                    Console.WriteLine($"     Prev entry ends at: {prevEntry.HeaderOffset + prevEntry.TotalSize:N0}");
                                if (nextEntry != null)
                                    Console.WriteLine($"     Next entry starts at: {nextEntry.HeaderOffset:N0}");
                            }
                        }

                        if (!foundDiff) foundDiff = true;
                    }
                }

                offset += read1;
            }

            if (diffCount > 5)
            {
                Console.WriteLine($"   ... and {diffCount - 5} more differences");
            }

            if (!foundDiff)
            {
                Console.WriteLine("   No byte differences found (size mismatch?)");
            }

            // Keep the file for manual inspection
            Console.WriteLine();
            Console.WriteLine($"   Keeping diagnostic file for inspection: {targetPath}");
            return 1;
        }

        // Clean up on success - close file handle first
        await diagFs.DisposeAsync();
        Console.WriteLine();
        Console.Write("10. Cleaning up diagnostic file... ");
        File.Delete(targetPath);
        Console.WriteLine("OK");

        return 0;
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
        const int ProgressBarWidth = 120;

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

                // Log file completions and failures (per-file stats now included in message)
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

    static int CompareUopEntries(string oldFile, string newFile)
    {
        Console.WriteLine($"Comparing UOP entries:");
        Console.WriteLine($"  Old: {oldFile}");
        Console.WriteLine($"  New: {newFile}");
        Console.WriteLine();

        var handler = new UopHandler();

        using var oldStream = File.OpenRead(oldFile);
        using var newStream = File.OpenRead(newFile);

        var oldInfo = handler.Parse(oldStream);
        var newInfo = handler.Parse(newStream);

        Console.WriteLine($"Old file: {oldInfo.Entries.Count} entries, {FormatSize(oldInfo.TotalSize)}");
        Console.WriteLine($"New file: {newInfo.Entries.Count} entries, {FormatSize(newInfo.TotalSize)}");
        Console.WriteLine();

        // Build lookup by entry ID
        var oldEntries = oldInfo.Entries.ToDictionary(e => e.EntryId);
        var newEntries = newInfo.Entries.ToDictionary(e => e.EntryId);

        var onlyInOld = oldEntries.Keys.Except(newEntries.Keys).ToList();
        var onlyInNew = newEntries.Keys.Except(oldEntries.Keys).ToList();
        var inBoth = oldEntries.Keys.Intersect(newEntries.Keys).ToList();

        Console.WriteLine($"Entries only in OLD: {onlyInOld.Count}");
        Console.WriteLine($"Entries only in NEW: {onlyInNew.Count}");
        Console.WriteLine($"Entries in BOTH: {inBoth.Count}");
        Console.WriteLine();

        // Compare entries that exist in both
        int sameSize = 0, diffSize = 0;
        int sameDecomp = 0, diffDecomp = 0;
        var sizeDiffs = new List<(string Id, int OldSize, int NewSize, int OldDecomp, int NewDecomp)>();

        foreach (var id in inBoth)
        {
            var old = oldEntries[id];
            var @new = newEntries[id];

            if (old.StoredSize == @new.StoredSize)
                sameSize++;
            else
            {
                diffSize++;
                sizeDiffs.Add((id, old.StoredSize, @new.StoredSize, old.DecompressedSize, @new.DecompressedSize));
            }

            if (old.DecompressedSize == @new.DecompressedSize)
                sameDecomp++;
            else
                diffDecomp++;
        }

        Console.WriteLine($"Same stored size: {sameSize}");
        Console.WriteLine($"Different stored size: {diffSize}");
        Console.WriteLine($"Same decompressed size: {sameDecomp}");
        Console.WriteLine($"Different decompressed size: {diffDecomp}");
        Console.WriteLine();

        if (sizeDiffs.Count > 0)
        {
            Console.WriteLine($"Sample entries with different sizes (first 10):");
            foreach (var (id, oldSize, newSize, oldDecomp, newDecomp) in sizeDiffs.Take(10))
            {
                Console.WriteLine($"  [{id}]");
                Console.WriteLine($"    Stored: {oldSize} → {newSize} (diff: {newSize - oldSize:+#;-#;0})");
                Console.WriteLine($"    Decomp: {oldDecomp} → {newDecomp} (diff: {newDecomp - oldDecomp:+#;-#;0})");
            }
        }

        return 0;
    }

    static int AnalyzeUopGaps(string uopFile)
    {
        Console.WriteLine($"=== UOP GAP ANALYSIS ===");
        Console.WriteLine($"File: {uopFile}");
        Console.WriteLine();

        if (!File.Exists(uopFile))
        {
            Console.WriteLine($"Error: File not found: {uopFile}");
            return 1;
        }

        var handler = new UopHandler();
        using var stream = File.OpenRead(uopFile);
        var info = handler.Parse(stream);

        Console.WriteLine($"File size: {FormatSize(info.TotalSize)} ({info.TotalSize:N0} bytes)");
        Console.WriteLine($"Entries: {info.Entries.Count}");
        Console.WriteLine();

        // Calculate entry ranges
        var entryRanges = info.Entries
            .Select(e => new {
                Entry = e,
                HeaderOffset = e.Offset - e.HeaderSize, // Header starts before data offset
                EndOffset = e.Offset + e.StoredSize,    // End of data
                TotalSize = e.HeaderSize + e.StoredSize
            })
            .OrderBy(e => e.HeaderOffset)
            .ToList();

        // Find UOP header and block tables first
        stream.Position = 0;
        var fileHeader = new byte[28];
        stream.Read(fileHeader);

        // Parse header
        long firstBlockOffset = BitConverter.ToInt64(fileHeader, 12);
        uint blockCapacity = BitConverter.ToUInt32(fileHeader, 20);
        int fileCount = BitConverter.ToInt32(fileHeader, 24);

        Console.WriteLine("=== FILE STRUCTURE ===");
        Console.WriteLine($"Header: 0 - 28 bytes (28 bytes)");
        Console.WriteLine($"First block at: {firstBlockOffset} (0x{firstBlockOffset:X})");
        Console.WriteLine($"Block capacity: {blockCapacity}");
        Console.WriteLine($"File count: {fileCount}");
        Console.WriteLine();

        // Collect all block table locations
        var blockTables = new List<(long Offset, int Size, int EntryCount)>();
        long nextBlock = firstBlockOffset;
        var blockHeader = new byte[12];

        while (nextBlock != 0)
        {
            stream.Position = nextBlock;
            stream.Read(blockHeader);

            int entryCount = BitConverter.ToInt32(blockHeader, 0);
            long nextBlockPtr = BitConverter.ToInt64(blockHeader, 4);

            int blockSize = 12 + entryCount * 34; // 12-byte header + 34-byte entries
            blockTables.Add((nextBlock, blockSize, entryCount));

            nextBlock = nextBlockPtr;
        }

        Console.WriteLine($"=== BLOCK TABLES ({blockTables.Count}) ===");
        foreach (var (offset, size, entryCount) in blockTables)
        {
            Console.WriteLine($"  Block at {offset,10} (0x{offset:X8}): {entryCount} entries, {size} bytes");
        }
        Console.WriteLine();

        // Now find all gaps in the file
        var usedRanges = new List<(long Start, long End, string Type)>();

        // File header
        usedRanges.Add((0, 28, "FILE_HEADER"));

        // Block tables
        foreach (var (offset, size, _) in blockTables)
        {
            usedRanges.Add((offset, offset + size, "BLOCK_TABLE"));
        }

        // Entries (header + data)
        foreach (var e in entryRanges)
        {
            usedRanges.Add((e.HeaderOffset, e.EndOffset, $"ENTRY[{e.Entry.EntryId}]"));
        }

        // Sort by start offset
        usedRanges = usedRanges.OrderBy(r => r.Start).ToList();

        // Find gaps
        var gaps = new List<(long Start, long End, long Size, string Context)>();
        long currentPos = 0;

        foreach (var (start, end, type) in usedRanges)
        {
            if (start > currentPos)
            {
                // There's a gap
                var prevRange = usedRanges.LastOrDefault(r => r.End <= currentPos);
                var prevType = prevRange != default ? prevRange.Type : "FILE_START";
                gaps.Add((currentPos, start, start - currentPos, $"After {prevType}"));
            }
            currentPos = Math.Max(currentPos, end);
        }

        // Check for trailing data after last used range
        if (currentPos < info.TotalSize)
        {
            var lastRange = usedRanges.Last();
            gaps.Add((currentPos, info.TotalSize, info.TotalSize - currentPos, $"After {lastRange.Type}"));
        }

        Console.WriteLine($"=== GAPS FOUND: {gaps.Count} ===");

        long totalGapBytes = 0;
        foreach (var (start, end, size, context) in gaps)
        {
            totalGapBytes += size;

            // Read the gap data to analyze its content
            stream.Position = start;
            var gapData = new byte[Math.Min(size, 1024)]; // Read up to 1KB for analysis
            stream.Read(gapData);

            // Analyze content
            var allZeros = gapData.All(b => b == 0);
            var nonZeroCount = gapData.Count(b => b != 0);
            var uniqueBytes = gapData.Distinct().Count();

            string contentType;
            if (allZeros && size == gapData.Length)
            {
                contentType = "ALL_ZEROS (padding)";
            }
            else if ((double)nonZeroCount / gapData.Length < 0.05)
            {
                contentType = "MOSTLY_ZEROS (sparse data)";
            }
            else if (uniqueBytes < 10)
            {
                contentType = "REPETITIVE_DATA";
            }
            else
            {
                contentType = "BINARY_DATA";
            }

            Console.WriteLine($"  Gap: {start,10} - {end,10} ({FormatSize(size),10}) | {contentType}");
            Console.WriteLine($"        Context: {context}");

            // Show first 32 bytes as hex if not all zeros
            if (!allZeros)
            {
                Console.WriteLine($"        First 32 bytes: {Convert.ToHexString(gapData.Take(32).ToArray())}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"=== SUMMARY ===");
        Console.WriteLine($"Total file size:     {FormatSize(info.TotalSize)}");
        Console.WriteLine($"Entry data:          {FormatSize(entryRanges.Sum(e => (long)e.TotalSize))}");
        Console.WriteLine($"Block table data:    {FormatSize(blockTables.Sum(b => (long)b.Size))}");
        Console.WriteLine($"File header:         28 bytes");
        Console.WriteLine($"Gap data (unused):   {FormatSize(totalGapBytes)} ({(double)totalGapBytes / info.TotalSize:P1})");

        // Breakdown of gaps
        var zeroGaps = gaps.Where(g =>
        {
            stream.Position = g.Start;
            var data = new byte[Math.Min(g.Size, 4096)];
            stream.Read(data);
            return data.All(b => b == 0);
        }).Sum(g => g.Size);

        var nonZeroGaps = totalGapBytes - zeroGaps;

        Console.WriteLine();
        Console.WriteLine($"  Zero-filled gaps:    {FormatSize(zeroGaps)}");
        Console.WriteLine($"  Non-zero gaps:       {FormatSize(nonZeroGaps)}");

        if (nonZeroGaps > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Note: Non-zero gaps may contain:");
            Console.WriteLine("  - Leftover data from previous versions");
            Console.WriteLine("  - Alignment padding with garbage values");
            Console.WriteLine("  - Unused but preserved file structure");
        }

        return 0;
    }
}
