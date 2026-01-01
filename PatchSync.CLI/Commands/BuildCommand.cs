using System.Diagnostics;
using System.Text.Json;
using PatchSync.CLI.Build;
using PatchSync.CLI.Wizard;
using PatchSync.CLI.Wizard.Steps;
using PatchSync.CLI.Wizard.Themes;
using PatchSync.CLI.Workspace;
using PatchSync.Common.Chunking;
using PatchSync.Common.Manifest;
using Spectre.Console;

namespace PatchSync.CLI.Commands;

public static class BuildCommand
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
            // Check if workspace mode (channel specified) or standalone mode (input/output specified)
            var channel = parser.Get("channel", "c");
            var version = parser.Get("version", "v");

            if (!string.IsNullOrEmpty(channel))
            {
                // Workspace mode
                if (string.IsNullOrEmpty(version))
                    throw new ArgumentException("Version is required when using workspace mode (-c channel -v version)");

                var inputPath = parser.Get("input", "i");
                var notes = parser.Get("notes");
                var tags = parser.Get("tags");
                var force = parser.Has("force");
                var dryRun = parser.Has("dry-run");

                var algorithm = parser.GetOrDefault("algorithm", "fastcdc-v1", "a");
                var minChunk = parser.GetInt("min-chunk", 0);
                var avgChunk = parser.GetInt("avg-chunk", 0);
                var maxChunk = parser.GetInt("max-chunk", 0);

                return await ExecuteWorkspaceModeAsync(
                    channel, version, inputPath, notes, tags,
                    force, dryRun, algorithm, minChunk, avgChunk, maxChunk);
            }
            else
            {
                // Standalone mode (legacy)
                var inputPath = parser.GetRequired("input", "i");
                var outputPath = parser.GetRequired("output", "o");
                version = parser.GetRequired("version", "v");
                var baseUrl = parser.GetOrDefault("base-url", "", "u");
                var fallbackUrl = parser.Get("fallback-url");
                var minDeltaSize = parser.GetLong("min-delta-size", 64 * 1024);
                var algorithm = parser.GetOrDefault("algorithm", "fastcdc-v1", "a");
                var minChunk = parser.GetInt("min-chunk", 4096);
                var avgChunk = parser.GetInt("avg-chunk", 16384);
                var maxChunk = parser.GetInt("max-chunk", 65536);

                return await ExecuteStandaloneModeAsync(
                    inputPath, outputPath, version, baseUrl, fallbackUrl,
                    minDeltaSize, algorithm, minChunk, avgChunk, maxChunk);
            }
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
        // Check for workspace - automatically detect mode
        var manager = WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());
        if (manager != null && manager.Exists)
        {
            return await RunWorkspaceWizardAsync(manager);
        }

        // No workspace - standalone mode
        return await RunStandaloneWizardAsync();
    }

    private static async Task<int> RunWorkspaceWizardAsync(WorkspaceManager manager)
    {
        var config = await manager.LoadConfigAsync();
        var channelChoices = config.Channels.Keys.ToList();

        // Store workspace context for validation
        WorkspaceManager? ctxManager = manager;
        WorkspaceConfig? ctxConfig = config;
        string? currentChannel = null;

        var wizard = new WizardRunner("Build Version", new BoxTheme())
            .AddStep(new SelectionStep(
                key: "channel",
                displayName: "Channel",
                prompt: "Select release channel",
                channelChoices))
            .AddStep(new CustomStep(
                key: "version",
                displayName: "Version",
                executor: (ctx, theme) =>
                {
                    currentChannel = ctx.Get<string>("channel");
                    var channelCfg = ctxConfig!.Channels[currentChannel];

                    // Show version pattern hint
                    if (!string.IsNullOrEmpty(channelCfg.VersionPattern) && channelCfg.VersionPattern != ".*")
                    {
                        AnsiConsole.MarkupLine($"[grey]Pattern:[/] {channelCfg.VersionPattern}");
                        AnsiConsole.WriteLine();
                    }

                    var result = WizardPrompt.Text(
                        "Version",
                        theme,
                        defaultValue: null,
                        allowEmpty: false,
                        validator: v =>
                        {
                            if (string.IsNullOrWhiteSpace(v))
                                return ValidationResult.Error("Version is required");
                            if (!ctxManager!.ValidateVersionPattern(currentChannel, v))
                                return ValidationResult.Error($"Doesn't match pattern: {channelCfg.VersionPattern}");
                            return ValidationResult.Success();
                        });

                    return Task.FromResult(result.ToObjectResult());
                }))
            .AddStep(new CustomStep(
                key: "overwrite",
                displayName: "Confirm Overwrite",
                executor: (ctx, theme) =>
                {
                    var channel = ctx.Get<string>("channel");
                    var version = ctx.Get<string>("version");

                    if (!ctxManager!.VersionExists(channel, version))
                    {
                        // Version doesn't exist, skip confirmation
                        return Task.FromResult(WizardResult<object?>.Success(true));
                    }

                    AnsiConsole.MarkupLine($"[yellow]Version {version} already exists in {channel}.[/]");
                    AnsiConsole.WriteLine();

                    var result = WizardPrompt.Confirm(
                        "Overwrite existing version?",
                        theme,
                        defaultValue: false);

                    if (result.IsSuccess && !result.Value)
                    {
                        // User said no - cancel the wizard
                        return Task.FromResult(WizardResult<object?>.Cancel);
                    }

                    return Task.FromResult(result.ToObjectResult());
                },
                skipCondition: ctx =>
                {
                    // We need to always evaluate this step to check version existence
                    return false;
                }))
            .AddStep(new FolderBrowseStep(
                key: "inputPath",
                displayName: "Input Directory",
                prompt: "Select input directory (containing files to package)",
                startPathFactory: _ => config.Defaults?.InputPath))
            .AddStep(new TextStep(
                key: "notes",
                displayName: "Release Notes",
                prompt: "Release notes (optional)",
                allowEmpty: true))
            .AddStep(new TextStep(
                key: "tags",
                displayName: "Tags",
                prompt: "Tags (comma-separated, optional)",
                allowEmpty: true));

        if (!await wizard.RunAsync())
        {
            return 0; // User cancelled
        }

        // Extract values
        var ctx = wizard.Context;
        var channel = ctx.Get<string>("channel");
        var version = ctx.Get<string>("version");
        var inputPath = ctx.Get<string>("inputPath");
        var notes = ctx.GetOrDefault<string>("notes");
        var tags = ctx.GetOrDefault<string>("tags");

        return await ExecuteWorkspaceModeAsync(
            channel, version, inputPath, notes, tags,
            force: true, // Already confirmed overwrite in wizard
            dryRun: false,
            algorithmOverride: null, minChunkOverride: 0, avgChunkOverride: 0, maxChunkOverride: 0,
            existingManager: manager);
    }

    private static async Task<int> RunStandaloneWizardAsync()
    {
        var registry = ChunkerRegistry.Default;

        var wizard = new WizardRunner("Build Signatures", new BoxTheme())
            .AddStep(new FolderBrowseStep(
                key: "inputPath",
                displayName: "Input Directory",
                prompt: "Select input directory (containing files to package)"))
            .AddStep(new FolderBrowseStep(
                key: "outputPath",
                displayName: "Output Directory",
                prompt: "Select output directory for manifest and signatures",
                allowNew: true))
            .AddStep(new TextStep(
                key: "version",
                displayName: "Version",
                prompt: "Version (e.g., 1.0.0)",
                validator: v => string.IsNullOrWhiteSpace(v)
                    ? ValidationResult.Error("Version is required")
                    : ValidationResult.Success()))
            .AddStep(new TextStep(
                key: "baseUrl",
                displayName: "Base URL",
                prompt: "Base URL where files will be hosted (optional)",
                allowEmpty: true))
            .AddStep(new SelectionStep(
                key: "algorithm",
                displayName: "Algorithm",
                prompt: "Select chunking algorithm",
                registry.SupportedAlgorithms))
            .AddStep(new ConfirmStep(
                key: "configureAdvanced",
                displayName: "Advanced Options",
                question: "Configure advanced chunking options?",
                defaultValue: false))
            .AddStep(ConditionalStep.WhenTrue("configureAdvanced",
                new TextStep(
                    key: "minChunk",
                    displayName: "Min Chunk Size",
                    prompt: "Minimum chunk size (bytes)",
                    defaultValue: "4096",
                    validator: v => int.TryParse(v, out var n) && n > 0
                        ? ValidationResult.Success()
                        : ValidationResult.Error("Must be a positive integer"))))
            .AddStep(ConditionalStep.WhenTrue("configureAdvanced",
                new TextStep(
                    key: "avgChunk",
                    displayName: "Avg Chunk Size",
                    prompt: "Average chunk size (bytes)",
                    defaultValue: "16384",
                    validator: v => int.TryParse(v, out var n) && n > 0
                        ? ValidationResult.Success()
                        : ValidationResult.Error("Must be a positive integer"))))
            .AddStep(ConditionalStep.WhenTrue("configureAdvanced",
                new TextStep(
                    key: "maxChunk",
                    displayName: "Max Chunk Size",
                    prompt: "Maximum chunk size (bytes)",
                    defaultValue: "65536",
                    validator: v => int.TryParse(v, out var n) && n > 0
                        ? ValidationResult.Success()
                        : ValidationResult.Error("Must be a positive integer"))));

        if (!await wizard.RunAsync())
        {
            return 0; // User cancelled
        }

        // Extract values
        var ctx = wizard.Context;
        var inputPath = ctx.Get<string>("inputPath");
        var outputPath = ctx.Get<string>("outputPath");
        var version = ctx.Get<string>("version");
        var baseUrl = ctx.GetOrDefault<string>("baseUrl") ?? "";
        var algorithm = ctx.Get<string>("algorithm");

        // Parse advanced options
        var configureAdvanced = ctx.GetOrDefault<bool>("configureAdvanced");
        int minChunk = 4096;
        int avgChunk = 16384;
        int maxChunk = 65536;
        long minDeltaSize = 64 * 1024;

        if (configureAdvanced)
        {
            if (ctx.TryGet<string>("minChunk", out var minStr) && int.TryParse(minStr, out var min))
                minChunk = min;
            if (ctx.TryGet<string>("avgChunk", out var avgStr) && int.TryParse(avgStr, out var avg))
                avgChunk = avg;
            if (ctx.TryGet<string>("maxChunk", out var maxStr) && int.TryParse(maxStr, out var max))
                maxChunk = max;
        }

        return await ExecuteStandaloneModeAsync(
            inputPath, outputPath, version, baseUrl, null,
            minDeltaSize, algorithm, minChunk, avgChunk, maxChunk);
    }

    private static async Task<int> ExecuteWorkspaceModeAsync(
        string channel,
        string version,
        string? inputPathOverride,
        string? notes,
        string? tags,
        bool force,
        bool dryRun,
        string? algorithmOverride,
        int minChunkOverride,
        int avgChunkOverride,
        int maxChunkOverride,
        WorkspaceManager? existingManager = null)
    {
        // Use existing manager or find workspace
        var manager = existingManager ?? WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());
        if (manager == null || !manager.Exists)
        {
            AnsiConsole.MarkupLine("[red]No workspace found.[/]");
            AnsiConsole.MarkupLine("[grey]Run 'patchsync init' to create a workspace, or use standalone mode with -i -o -v.[/]");
            return 1;
        }

        var config = await manager.LoadConfigAsync();

        // Validate channel
        if (!config.Channels.TryGetValue(channel, out var channelConfig))
        {
            AnsiConsole.MarkupLine($"[red]Channel not found:[/] {channel}");
            AnsiConsole.MarkupLine($"[grey]Available channels:[/] {string.Join(", ", config.Channels.Keys)}");
            return 1;
        }

        // Validate version pattern
        if (!manager.ValidateVersionPattern(channel, version))
        {
            AnsiConsole.MarkupLine($"[red]Version doesn't match channel pattern:[/] {channelConfig.VersionPattern}");
            return 1;
        }

        // Check if version exists
        var versionPath = manager.GetVersionPath(channel, version);
        if (manager.VersionExists(channel, version) && !force)
        {
            AnsiConsole.MarkupLine($"[red]Version already exists:[/] {version}");
            AnsiConsole.MarkupLine("[grey]Use --force to overwrite.[/]");
            return 1;
        }

        // Resolve input path
        var inputPath = inputPathOverride ?? config.Defaults?.InputPath;
        if (string.IsNullOrEmpty(inputPath))
        {
            AnsiConsole.MarkupLine("[red]No input path specified and no default in workspace config.[/]");
            return 1;
        }

        if (!Directory.Exists(inputPath))
        {
            AnsiConsole.MarkupLine($"[red]Input directory not found:[/] {inputPath}");
            return 1;
        }

        // Resolve chunking options
        var chunkingDefaults = config.Defaults?.Chunking ?? new ChunkingDefaults();
        var algorithm = algorithmOverride ?? chunkingDefaults.Algorithm;
        var minChunk = minChunkOverride > 0 ? minChunkOverride : chunkingDefaults.MinChunkSize;
        var avgChunk = avgChunkOverride > 0 ? avgChunkOverride : chunkingDefaults.AvgChunkSize;
        var maxChunk = maxChunkOverride > 0 ? maxChunkOverride : chunkingDefaults.MaxChunkSize;
        var minDeltaSize = chunkingDefaults.MinDeltaSize;

        if (dryRun)
        {
            AnsiConsole.MarkupLine("[yellow]Dry run - no files will be created[/]");
            AnsiConsole.MarkupLine($"  Channel: {channel}");
            AnsiConsole.MarkupLine($"  Version: {version}");
            AnsiConsole.MarkupLine($"  Input: {inputPath}");
            AnsiConsole.MarkupLine($"  Output: {versionPath}");
            AnsiConsole.MarkupLine($"  Algorithm: {algorithm}");
            return 0;
        }

        // Acquire build lock
        manager.CreateVersionStructure(channel, version);
        using var buildLock = BuildLock.TryAcquire(versionPath);
        if (buildLock == null)
        {
            var holder = BuildLock.GetLockHolder(versionPath);
            AnsiConsole.MarkupLine($"[red]Build in progress by another process.[/]");
            if (holder != null)
            {
                AnsiConsole.MarkupLine($"[grey]Locked by: {holder.MachineName} (PID {holder.ProcessId}) at {holder.LockedAt}[/]");
            }
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();

        // Get chunker
        var registry = ChunkerRegistry.Default;
        if (!registry.TryGetChunker(algorithm, out var chunker))
        {
            AnsiConsole.MarkupLine($"[red]Unknown algorithm:[/] {algorithm}");
            return 1;
        }

        var options = new ChunkingOptions
        {
            MinSize = minChunk,
            AverageSize = avgChunk,
            MaxSize = maxChunk
        };

        try
        {
            options.Validate();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Invalid chunking options:[/] {ex.Message}");
            return 1;
        }

        // Setup output paths
        var sigDir = manager.GetSignaturesPath(channel, version);
        Directory.CreateDirectory(sigDir);

        var parallelBuilder = new ParallelSignatureBuilder(chunker!, options);
        var buildOptions = new ParallelBuildOptions
        {
            MinDeltaSize = minDeltaSize,
            GenerateSignatures = true,
            SignatureOutputDirectory = sigDir
        };

        var workerCount = parallelBuilder.MaxConcurrency;
        AnsiConsole.MarkupLine($"[blue]Building:[/] {channelConfig.DisplayName} v{version}");
        AnsiConsole.MarkupLine($"[blue]Input:[/] {inputPath}");
        AnsiConsole.MarkupLine($"[blue]Algorithm:[/] {algorithm}");
        AnsiConsole.MarkupLine($"[blue]Workers:[/] {workerCount}");
        AnsiConsole.WriteLine();

        GameManifest manifest = null!;
        await AnsiConsole.Live(new Text("Starting parallel build..."))
            .AutoClear(false)
            .StartAsync(async ctx =>
            {
                var display = new LiveBuildProgress(ctx);

                manifest = await parallelBuilder.BuildAsync(
                    inputPath,
                    version,
                    "", // Base URL will be set at publish time
                    buildOptions,
                    display); // Pass directly - implements IProgress<T> synchronously
            });

        stopwatch.Stop();

        // Write manifest
        var manifestPath = manager.GetManifestPath(channel, version);
        await using (var manifestStream = File.Create(manifestPath))
        {
            await JsonSerializer.SerializeAsync(
                manifestStream,
                manifest,
                ManifestJsonContext.Default.GameManifest);
        }

        // Calculate signature stats
        var sigFiles = Directory.GetFiles(sigDir, "*.sig");
        var sigTotalSize = sigFiles.Sum(f => new FileInfo(f).Length);

        // Create version metadata
        var metadata = new VersionMetadata
        {
            Version = version,
            Channel = channel,
            Status = VersionStatus.Staged,
            Build = new BuildInfo
            {
                BuiltAt = DateTime.UtcNow,
                BuiltBy = Environment.MachineName,
                BuildNumber = Environment.GetEnvironmentVariable("PATCHSYNC_BUILD_NUMBER"),
                Duration = stopwatch.Elapsed.TotalSeconds,
                PatchSyncVersion = "2.0.0"
            },
            Input = new InputInfo
            {
                Path = inputPath,
                FileCount = manifest.Files.Count,
                TotalSize = manifest.Files.Sum(f => f.Size)
            },
            Output = new OutputInfo
            {
                SignatureCount = sigFiles.Length,
                SignatureTotalSize = sigTotalSize
            },
            Chunking = new ChunkingInfo
            {
                Algorithm = algorithm,
                MinChunkSize = minChunk,
                AvgChunkSize = avgChunk,
                MaxChunkSize = maxChunk
            },
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes,
            Tags = string.IsNullOrWhiteSpace(tags)
                ? null
                : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
        };

        await manager.SaveVersionMetadataAsync(channel, version, metadata);

        // Update channel state
        var channelState = await manager.LoadChannelStateAsync(channel) ?? new ChannelState
        {
            ChannelId = channel,
            DisplayName = channelConfig.DisplayName
        };

        channelState.History.Insert(0, new VersionHistoryEntry
        {
            Version = version,
            Status = VersionStatus.Staged,
            BuiltAt = metadata.Build.BuiltAt,
            FileCount = metadata.Input.FileCount,
            TotalSize = metadata.Input.TotalSize
        });

        await manager.SaveChannelStateAsync(channel, channelState);

        // Update workspace state
        var state = await manager.LoadStateAsync();

        // Remove any existing pending publish for this channel (new build replaces old)
        state.PendingPublish.RemoveAll(p => p.Channel == channel);

        state.PendingPublish.Add(new PendingPublishInfo
        {
            Channel = channel,
            Version = version,
            StagedAt = DateTime.UtcNow
        });
        state.RecentOperations.Insert(0, new RecentOperationInfo
        {
            Type = "build",
            Channel = channel,
            Version = version,
            Timestamp = DateTime.UtcNow,
            Status = "success"
        });
        // Keep only last 20 operations
        if (state.RecentOperations.Count > 20)
            state.RecentOperations = state.RecentOperations.Take(20).ToList();

        await manager.SaveStateAsync(state);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]:check_mark_button: Build complete![/]");

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Property")
            .AddColumn("Value");

        table.AddRow("Channel", $"{channelConfig.DisplayName} ({channel})");
        table.AddRow("Version", version);
        table.AddRow("Status", "[yellow]Staged[/]");
        table.AddRow("Files", manifest.Files.Count.ToString());
        table.AddRow("Total Size", FormatBytes(manifest.Files.Sum(f => f.Size)));
        table.AddRow("Signatures", sigFiles.Length.ToString());
        table.AddRow("Duration", $"{stopwatch.Elapsed.TotalSeconds:F1}s");

        AnsiConsole.Write(table);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Next:[/] Run [blue]patchsync publish -c {0}[/] to upload to CDN", channel);

        return 0;
    }

    private static async Task<int> ExecuteStandaloneModeAsync(
        string inputPath,
        string outputPath,
        string version,
        string baseUrl,
        string? fallbackUrl,
        long minDeltaSize,
        string algorithm,
        int minChunk,
        int avgChunk,
        int maxChunk)
    {
        // Validate paths
        if (!Directory.Exists(inputPath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Input directory not found: {inputPath}");
            return 1;
        }

        // Get chunker
        var registry = ChunkerRegistry.Default;
        if (!registry.TryGetChunker(algorithm, out var chunker))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Unknown algorithm: {algorithm}");
            AnsiConsole.MarkupLine($"Available algorithms: {string.Join(", ", registry.SupportedAlgorithms)}");
            return 1;
        }

        var options = new ChunkingOptions
        {
            MinSize = minChunk,
            AverageSize = avgChunk,
            MaxSize = maxChunk
        };

        try
        {
            options.Validate();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Invalid chunking options: {ex.Message}");
            return 1;
        }

        // Create output directories
        Directory.CreateDirectory(outputPath);
        var sigDir = Path.Combine(outputPath, "signatures");
        Directory.CreateDirectory(sigDir);

        var parallelBuilder = new ParallelSignatureBuilder(chunker!, options);
        var buildOptions = new ParallelBuildOptions
        {
            MinDeltaSize = minDeltaSize,
            GenerateSignatures = true,
            SignatureOutputDirectory = sigDir,
            FallbackUrl = fallbackUrl
        };

        var workerCount = parallelBuilder.MaxConcurrency;
        AnsiConsole.MarkupLine($"[blue]Building manifest for:[/] {inputPath}");
        AnsiConsole.MarkupLine($"[blue]Output directory:[/] {outputPath}");
        AnsiConsole.MarkupLine($"[blue]Version:[/] {version}");
        AnsiConsole.MarkupLine($"[blue]Algorithm:[/] {algorithm}");
        AnsiConsole.MarkupLine($"[blue]Workers:[/] {workerCount}");
        AnsiConsole.WriteLine();

        GameManifest manifest = null!;
        await AnsiConsole.Live(new Text("Starting parallel build..."))
            .AutoClear(false)
            .StartAsync(async ctx =>
            {
                var display = new LiveBuildProgress(ctx);

                manifest = await parallelBuilder.BuildAsync(
                    inputPath,
                    version,
                    baseUrl,
                    buildOptions,
                    display); // Pass directly - implements IProgress<T> synchronously
            });

        // Write manifest
        var manifestPath = Path.Combine(outputPath, "manifest.json");
        await using var manifestStream = File.Create(manifestPath);
        await JsonSerializer.SerializeAsync(
            manifestStream,
            manifest,
            ManifestJsonContext.Default.GameManifest);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]Build complete![/]");
        AnsiConsole.MarkupLine($"  Files processed: {manifest.Files.Count}");
        AnsiConsole.MarkupLine($"  Total size: {FormatBytes(manifest.Files.Sum(f => f.Size))}");
        AnsiConsole.MarkupLine($"  Manifest: {manifestPath}");
        AnsiConsole.MarkupLine($"  Signatures: {sigDir}");

        return 0;
    }

    private static void ShowHelp()
    {
        AnsiConsole.MarkupLine("[bold]patchsync build[/] - Generate signatures and manifest");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Workspace Mode:[/]");
        AnsiConsole.MarkupLine("  patchsync build -c <channel> -v <version> [[options]]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  -c, --channel <NAME>      Target channel (prod, beta, dev)");
        AnsiConsole.MarkupLine("  -v, --version <VERSION>   Version string");
        AnsiConsole.MarkupLine("  -i, --input <PATH>        Input directory (overrides workspace default)");
        AnsiConsole.MarkupLine("      --notes <TEXT>        Release notes");
        AnsiConsole.MarkupLine("      --tags <LIST>         Comma-separated tags");
        AnsiConsole.MarkupLine("      --force               Overwrite existing version");
        AnsiConsole.MarkupLine("      --dry-run             Show what would be built");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Standalone Mode:[/]");
        AnsiConsole.MarkupLine("  patchsync build -i <input> -o <output> -v <version> [[options]]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  -i, --input <PATH>        Input directory containing files");
        AnsiConsole.MarkupLine("  -o, --output <PATH>       Output directory for manifest/signatures");
        AnsiConsole.MarkupLine("  -v, --version <VERSION>   Version string");
        AnsiConsole.MarkupLine("  -u, --base-url <URL>      Base URL where files will be hosted");
        AnsiConsole.MarkupLine("      --fallback-url <URL>  Fallback URL for full archive");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Chunking Options:[/]");
        AnsiConsole.MarkupLine("  -a, --algorithm <NAME>    Chunking algorithm (default: fastcdc-v1)");
        AnsiConsole.MarkupLine("      --min-chunk <N>       Minimum chunk size (default: 4096)");
        AnsiConsole.MarkupLine("      --avg-chunk <N>       Average chunk size (default: 16384)");
        AnsiConsole.MarkupLine("      --max-chunk <N>       Maximum chunk size (default: 65536)");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Examples:[/]");
        AnsiConsole.MarkupLine("  patchsync build -c prod -v 1.0.0");
        AnsiConsole.MarkupLine("  patchsync build -c beta -v 1.1.0-beta.1 --notes \"New features\"");
        AnsiConsole.MarkupLine("  patchsync build -i ./game -o ./output -v 1.0.0");
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
