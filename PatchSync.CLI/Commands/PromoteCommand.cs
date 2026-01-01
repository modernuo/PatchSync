using System.Text.Json;
using PatchSync.CLI.Workspace;
using PatchSync.Common.Manifest;
using Spectre.Console;

namespace PatchSync.CLI.Commands;

/// <summary>
/// Command for promoting a version from one channel to another.
/// Copies all artifacts with metadata link for traceability.
/// </summary>
public static class PromoteCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var parser = new ArgParser(args);

        if (parser.HasHelp)
        {
            ShowHelp();
            return 0;
        }

        var source = parser.FirstPositional;
        var target = parser.Positional.Count > 1 ? parser.Positional[1] : null;
        var asVersion = parser.Get("as");
        var force = parser.GetBool("force", false, "f");

        var workspace = WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());
        if (workspace == null || !workspace.Exists)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No workspace found. Run from a workspace directory.");
            return 1;
        }

        await workspace.LoadConfigAsync();

        // Interactive mode if source not specified
        if (string.IsNullOrEmpty(source))
        {
            return await InteractivePromoteAsync(workspace, force);
        }

        // Parse source (channel:version format)
        var (sourceChannel, sourceVersion) = ParseVersionSpec(source);
        if (sourceChannel == null || sourceVersion == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Invalid source format. Use channel:version (e.g., beta:1.0.0)");
            return 1;
        }

        // Target channel required
        if (string.IsNullOrEmpty(target))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Target channel required.");
            ShowHelp();
            return 1;
        }

        // Validate channels exist
        if (!workspace.Config!.Channels.ContainsKey(sourceChannel))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Source channel not found: {sourceChannel}");
            return 1;
        }

        if (!workspace.Config.Channels.ContainsKey(target))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Target channel not found: {target}");
            return 1;
        }

        // Validate source version exists
        if (!workspace.VersionExists(sourceChannel, sourceVersion))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Version not found: {sourceChannel}/{sourceVersion}");
            return 1;
        }

        var targetVersion = asVersion ?? sourceVersion;

        // Check if target already exists
        if (workspace.VersionExists(target, targetVersion))
        {
            if (!force)
            {
                var overwrite = AnsiConsole.Confirm(
                    $"[yellow]Version {targetVersion} already exists in {target}. Overwrite?[/]",
                    false);
                if (!overwrite)
                {
                    AnsiConsole.MarkupLine("[dim]Cancelled.[/]");
                    return 0;
                }
            }
        }

        // Confirm promotion
        if (!force)
        {
            AnsiConsole.MarkupLine($"[bold]Promote version:[/]");
            AnsiConsole.MarkupLine($"  From: [green]{sourceChannel}/{sourceVersion}[/]");
            AnsiConsole.MarkupLine($"  To:   [green]{target}/{targetVersion}[/]");
            AnsiConsole.WriteLine();

            var confirm = AnsiConsole.Confirm("Proceed with promotion?", true);
            if (!confirm)
            {
                AnsiConsole.MarkupLine("[dim]Cancelled.[/]");
                return 0;
            }
        }

        return await PerformPromotionAsync(
            workspace,
            sourceChannel,
            sourceVersion,
            target,
            targetVersion);
    }

    private static async Task<int> InteractivePromoteAsync(WorkspaceManager workspace, bool force)
    {
        var channels = workspace.Config!.Channels.Keys.ToList();
        if (channels.Count < 2)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] At least two channels required for promotion.");
            return 1;
        }

        // Select source channel
        var sourceChannel = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select [green]source channel[/]:")
                .AddChoices(channels));

        // Select source version
        var versions = workspace.GetVersions(sourceChannel).ToList();
        if (versions.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] No versions in channel '{sourceChannel}'.");
            return 1;
        }

        var sourceVersion = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select [green]version to promote[/]:")
                .PageSize(15)
                .AddChoices(versions));

        // Select target channel
        var targetChannels = channels.Where(c => c != sourceChannel).ToList();
        var targetChannel = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select [green]target channel[/]:")
                .AddChoices(targetChannels));

        // Ask for target version name
        var targetVersion = AnsiConsole.Prompt(
            new TextPrompt<string>("Target version name:")
                .DefaultValue(sourceVersion)
                .AllowEmpty());

        if (string.IsNullOrWhiteSpace(targetVersion))
            targetVersion = sourceVersion;

        // Check if target exists
        if (workspace.VersionExists(targetChannel, targetVersion))
        {
            if (!force)
            {
                var overwrite = AnsiConsole.Confirm(
                    $"[yellow]Version {targetVersion} already exists. Overwrite?[/]",
                    false);
                if (!overwrite)
                {
                    AnsiConsole.MarkupLine("[dim]Cancelled.[/]");
                    return 0;
                }
            }
        }

        return await PerformPromotionAsync(
            workspace,
            sourceChannel,
            sourceVersion,
            targetChannel,
            targetVersion);
    }

    private static async Task<int> PerformPromotionAsync(
        WorkspaceManager workspace,
        string sourceChannel,
        string sourceVersion,
        string targetChannel,
        string targetVersion)
    {
        try
        {
            await AnsiConsole.Status()
                .StartAsync($"Promoting {sourceChannel}/{sourceVersion} to {targetChannel}/{targetVersion}...",
                    async ctx =>
                    {
                        // Load source metadata
                        ctx.Status("Loading source version metadata...");
                        var sourceMetadata = await workspace.LoadVersionMetadataAsync(sourceChannel, sourceVersion);
                        if (sourceMetadata == null)
                        {
                            throw new WorkspaceException("Failed to load source version metadata");
                        }

                        // Create target version directory structure
                        ctx.Status("Creating target version structure...");
                        workspace.CreateVersionStructure(targetChannel, targetVersion);

                        var sourcePath = workspace.GetVersionPath(sourceChannel, sourceVersion);
                        var targetPath = workspace.GetVersionPath(targetChannel, targetVersion);

                        // Copy all files from source to target
                        ctx.Status("Copying version files...");
                        await CopyDirectoryAsync(sourcePath, targetPath);

                        // Update manifest with new version string if different
                        ctx.Status("Updating manifest...");
                        await UpdateManifestVersionAsync(
                            workspace.GetManifestPath(targetChannel, targetVersion),
                            targetVersion);

                        // Create new metadata with promotion info
                        ctx.Status("Creating version metadata...");
                        var targetMetadata = new VersionMetadata
                        {
                            Version = targetVersion,
                            Channel = targetChannel,
                            Build = new BuildInfo
                            {
                                BuiltAt = sourceMetadata.Build.BuiltAt,
                                BuiltBy = sourceMetadata.Build.BuiltBy,
                                BuildNumber = sourceMetadata.Build.BuildNumber,
                                Duration = sourceMetadata.Build.Duration,
                                PatchSyncVersion = sourceMetadata.Build.PatchSyncVersion
                            },
                            Input = sourceMetadata.Input,
                            Output = sourceMetadata.Output,
                            Chunking = sourceMetadata.Chunking,
                            Status = VersionStatus.Staged,
                            Comparison = sourceMetadata.Comparison,
                            Notes = sourceMetadata.Notes,
                            Tags = sourceMetadata.Tags,
                            BasedOn = sourceMetadata.BasedOn,
                            PromotedFrom = new PromotedFromInfo
                            {
                                SourceChannel = sourceChannel,
                                SourceVersion = sourceVersion,
                                PromotedAt = DateTime.UtcNow,
                                PromotedBy = Environment.MachineName
                            },
                            FileOverrides = sourceMetadata.FileOverrides
                        };

                        await workspace.SaveVersionMetadataAsync(targetChannel, targetVersion, targetMetadata);

                        // Update channel state
                        ctx.Status("Updating channel state...");
                        var existingChannelState = await workspace.LoadChannelStateAsync(targetChannel);
                        var channelConfig = workspace.Config!.Channels[targetChannel];

                        var channelState = existingChannelState ?? new ChannelState
                        {
                            ChannelId = targetChannel,
                            DisplayName = channelConfig.DisplayName ?? targetChannel
                        };

                        // Add to history
                        channelState.History.Add(new VersionHistoryEntry
                        {
                            Version = targetVersion,
                            Status = VersionStatus.Staged,
                            BuiltAt = sourceMetadata.Build.BuiltAt,
                            FileCount = sourceMetadata.Input.FileCount,
                            TotalSize = sourceMetadata.Input.TotalSize
                        });

                        await workspace.SaveChannelStateAsync(targetChannel, channelState);
                    });

            AnsiConsole.MarkupLine($"[green]Successfully promoted:[/]");
            AnsiConsole.MarkupLine($"  {sourceChannel}/{sourceVersion} -> {targetChannel}/{targetVersion}");

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error during promotion:[/] {ex.Message}");
            return 1;
        }
    }

    private static async Task CopyDirectoryAsync(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        // Copy all files
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var fileName = Path.GetFileName(file);
            var targetFile = Path.Combine(targetDir, fileName);
            File.Copy(file, targetFile, overwrite: true);
        }

        // Copy subdirectories recursively
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(dir);
            await CopyDirectoryAsync(dir, Path.Combine(targetDir, dirName));
        }
    }

    private static async Task UpdateManifestVersionAsync(string manifestPath, string newVersion)
    {
        if (!File.Exists(manifestPath))
            return;

        await using var readStream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync(
            readStream,
            ManifestJsonContext.Default.GameManifest);

        if (manifest == null)
            return;

        // Check if version is already correct
        if (manifest.Version == newVersion)
            return;

        // Create updated manifest with new version
        var updatedManifest = new GameManifest
        {
            Version = newVersion,
            BuildDate = manifest.BuildDate,
            SupportedAlgorithms = manifest.SupportedAlgorithms,
            PreferredAlgorithm = manifest.PreferredAlgorithm,
            FallbackUrl = manifest.FallbackUrl,
            BaseUrl = manifest.BaseUrl,
            Files = manifest.Files,
            Metadata = manifest.Metadata
        };

        // Close read stream before writing
        await readStream.DisposeAsync();

        var tempPath = manifestPath + ".tmp";
        await using (var writeStream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(
                writeStream,
                updatedManifest,
                ManifestJsonContext.Default.GameManifest);
        }

        File.Move(tempPath, manifestPath, overwrite: true);
    }

    private static (string? channel, string? version) ParseVersionSpec(string spec)
    {
        var colonIndex = spec.IndexOf(':');
        if (colonIndex <= 0 || colonIndex >= spec.Length - 1)
            return (null, null);

        return (spec[..colonIndex], spec[(colonIndex + 1)..]);
    }

    private static void ShowHelp()
    {
        AnsiConsole.MarkupLine("[bold]patchsync promote[/] - Promote a version to another channel");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Usage:[/]");
        AnsiConsole.MarkupLine("  patchsync promote <source> <target> [options]");
        AnsiConsole.MarkupLine("  patchsync promote                          (interactive mode)");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Arguments:[/]");
        AnsiConsole.MarkupLine("  source    Source version (channel:version format)");
        AnsiConsole.MarkupLine("  target    Target channel name");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Options:[/]");
        AnsiConsole.MarkupLine("      --as       Target version string (default: same as source)");
        AnsiConsole.MarkupLine("  -f, --force    Skip confirmation prompts");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Examples:[/]");
        AnsiConsole.MarkupLine("  patchsync promote beta:1.0.0 prod");
        AnsiConsole.MarkupLine("  patchsync promote beta:2.0.0-rc1 prod --as 2.0.0");
        AnsiConsole.MarkupLine("  patchsync promote dev:latest beta --force");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Notes:[/]");
        AnsiConsole.MarkupLine("  - All build artifacts are copied to the target channel");
        AnsiConsole.MarkupLine("  - Promotion metadata is recorded for traceability");
        AnsiConsole.MarkupLine("  - The promoted version starts with Staged status");
    }
}
