using System.CommandLine;
using System.CommandLine.Parsing;
using PatchSync.CLI.Workspace;
using Spectre.Console;

namespace PatchSync.CLI.Commands;

/// <summary>
/// Builds the command-line interface using System.CommandLine 2.0.
/// </summary>
public static class CliBuilder
{
    /// <summary>
    /// Global workspace option available to all commands.
    /// </summary>
    public static readonly Option<string?> WorkspaceOption = new("--workspace", "-w")
    {
        Description = "Path to workspace directory"
    };

    /// <summary>
    /// Builds the root command with all subcommands.
    /// </summary>
    public static RootCommand Build()
    {
        var rootCommand = new RootCommand("PatchSync - Delta patching tool for game updates")
        {
            WorkspaceOption
        };

        // Add subcommands
        rootCommand.Add(BuildInitCommand());
        rootCommand.Add(BuildBuildCommand());
        rootCommand.Add(BuildPatchCommand());
        rootCommand.Add(BuildVerifyCommand());
        rootCommand.Add(BuildUploadCommand());
        rootCommand.Add(BuildStatusCommand());
        rootCommand.Add(BuildInfoCommand());
#if FONT_PREVIEW
        rootCommand.Add(BuildFontsCommand());
#endif

        // Root command handler (no subcommand = interactive mode)
        rootCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var workspace = parseResult.GetValue(WorkspaceOption);
            await HandleInteractiveMode(workspace);
            return 0;
        });

        return rootCommand;
    }

    /// <summary>
    /// Parses and invokes the command.
    /// </summary>
    public static async Task<int> InvokeAsync(string[] args)
    {
        var rootCommand = Build();
        var parseResult = rootCommand.Parse(args);
        return await parseResult.InvokeAsync();
    }

    private static async Task HandleInteractiveMode(string? workspacePath)
    {
        if (!string.IsNullOrEmpty(workspacePath))
        {
            var manager = WorkspaceManager.ForPath(workspacePath);
            if (!manager.Exists)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] No workspace found at '{workspacePath}'");
                return;
            }
            await WorkspaceMenu.ShowWorkspaceMenuAsync(manager);
            return;
        }

        await WorkspaceMenu.ShowMainMenuAsync();
    }

    #region Init Command

    private static Command BuildInitCommand()
    {
        var pathOption = new Option<string?>("--path", "-p")
        {
            Description = "Directory to initialize (default: current directory)"
        };

        var nameOption = new Option<string?>("--name", "-n")
        {
            Description = "Project name"
        };

        var channelsOption = new Option<string?>("--channels")
        {
            Description = "Comma-separated channel names (default: prod,beta,dev)"
        };

        var command = new Command("init", "Initialize a new PatchSync workspace")
        {
            pathOption,
            nameOption,
            channelsOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var path = parseResult.GetValue(pathOption);
            var name = parseResult.GetValue(nameOption);
            var channels = parseResult.GetValue(channelsOption);

            var args = BuildArgs(
                ("path", path),
                ("name", name),
                ("channels", channels));
            return await InitCommand.RunAsync(args);
        });

        return command;
    }

    #endregion

    #region Build Command

    private static Command BuildBuildCommand()
    {
        var inputOption = new Option<string?>("--input", "-i")
        {
            Description = "Input directory containing files to process"
        };

        var outputOption = new Option<string?>("--output", "-o")
        {
            Description = "Output directory for signatures and manifest"
        };

        var versionOption = new Option<string?>("--version", "-v")
        {
            Description = "Version string for the manifest"
        };

        var channelOption = new Option<string?>("--channel", "-c")
        {
            Description = "Channel to build for (workspace mode)"
        };

        var baseUrlOption = new Option<string?>("--base-url", "-u")
        {
            Description = "Base URL for file downloads"
        };

        var minChunkOption = new Option<int?>("--min-chunk")
        {
            Description = "Minimum chunk size in bytes"
        };

        var avgChunkOption = new Option<int?>("--avg-chunk")
        {
            Description = "Average chunk size in bytes"
        };

        var maxChunkOption = new Option<int?>("--max-chunk")
        {
            Description = "Maximum chunk size in bytes"
        };

        var algorithmOption = new Option<string?>("--algorithm")
        {
            Description = "Chunking algorithm (fastcdc-v1)"
        };

        var stagedOption = new Option<bool>("--staged")
        {
            Description = "Mark version as staged for publishing"
        };

        var command = new Command("build", "Generate signatures and manifest for a directory")
        {
            inputOption,
            outputOption,
            versionOption,
            channelOption,
            baseUrlOption,
            minChunkOption,
            avgChunkOption,
            maxChunkOption,
            algorithmOption,
            stagedOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputOption);
            var output = parseResult.GetValue(outputOption);
            var version = parseResult.GetValue(versionOption);
            var channel = parseResult.GetValue(channelOption);
            var baseUrl = parseResult.GetValue(baseUrlOption);
            var minChunk = parseResult.GetValue(minChunkOption);
            var avgChunk = parseResult.GetValue(avgChunkOption);
            var maxChunk = parseResult.GetValue(maxChunkOption);
            var algorithm = parseResult.GetValue(algorithmOption);
            var staged = parseResult.GetValue(stagedOption);

            var args = new List<string>();
            if (input != null) { args.Add("-i"); args.Add(input); }
            if (output != null) { args.Add("-o"); args.Add(output); }
            if (version != null) { args.Add("-v"); args.Add(version); }
            if (channel != null) { args.Add("-c"); args.Add(channel); }
            if (baseUrl != null) { args.Add("-u"); args.Add(baseUrl); }
            if (minChunk != null) { args.Add("--min-chunk"); args.Add(minChunk.Value.ToString()); }
            if (avgChunk != null) { args.Add("--avg-chunk"); args.Add(avgChunk.Value.ToString()); }
            if (maxChunk != null) { args.Add("--max-chunk"); args.Add(maxChunk.Value.ToString()); }
            if (algorithm != null) { args.Add("--algorithm"); args.Add(algorithm); }
            if (staged) { args.Add("--staged"); }

            return await BuildCommand.RunAsync(args.ToArray());
        });

        return command;
    }

    #endregion

    #region Patch Command

    private static Command BuildPatchCommand()
    {
        var urlOption = new Option<string?>("--url", "-u")
        {
            Description = "Base URL for patch server"
        };

        var pathOption = new Option<string?>("--path", "-p")
        {
            Description = "Local installation path to patch"
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Show what would be done without making changes"
        };

        var command = new Command("patch", "Apply delta patches to update local files")
        {
            urlOption,
            pathOption,
            dryRunOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var url = parseResult.GetValue(urlOption);
            var path = parseResult.GetValue(pathOption);
            var dryRun = parseResult.GetValue(dryRunOption);

            var args = BuildArgs(
                ("url", url),
                ("path", path),
                ("dry-run", dryRun ? "true" : null));
            return await PatchCommand.RunAsync(args);
        });

        return command;
    }

    #endregion

    #region Verify Command

    private static Command BuildVerifyCommand()
    {
        var urlOption = new Option<string?>("--url", "-u")
        {
            Description = "Base URL for manifest"
        };

        var pathOption = new Option<string?>("--path", "-p")
        {
            Description = "Local installation path to verify"
        };

        var command = new Command("verify", "Verify local files against manifest")
        {
            urlOption,
            pathOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var url = parseResult.GetValue(urlOption);
            var path = parseResult.GetValue(pathOption);

            var args = BuildArgs(
                ("url", url),
                ("path", path));
            return await VerifyCommand.RunAsync(args);
        });

        return command;
    }

    #endregion

    #region Upload Command

    private static Command BuildUploadCommand()
    {
        var buildDirOption = new Option<string?>("--build-dir", "-b")
        {
            Description = "Build output directory to upload"
        };

        var endpointOption = new Option<string?>("--endpoint", "-e")
        {
            Description = "S3-compatible endpoint URL"
        };

        var bucketOption = new Option<string?>("--bucket")
        {
            Description = "Target bucket name"
        };

        var prefixOption = new Option<string?>("--prefix")
        {
            Description = "Key prefix for uploaded files"
        };

        var regionOption = new Option<string?>("--region")
        {
            Description = "AWS region"
        };

        var accessKeyOption = new Option<string?>("--access-key")
        {
            Description = "AWS access key ID"
        };

        var secretKeyOption = new Option<string?>("--secret-key")
        {
            Description = "AWS secret access key"
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Show what would be uploaded without uploading"
        };

        var command = new Command("upload", "Upload build artifacts to S3-compatible storage")
        {
            buildDirOption,
            endpointOption,
            bucketOption,
            prefixOption,
            regionOption,
            accessKeyOption,
            secretKeyOption,
            dryRunOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var buildDir = parseResult.GetValue(buildDirOption);
            var endpoint = parseResult.GetValue(endpointOption);
            var bucket = parseResult.GetValue(bucketOption);
            var prefix = parseResult.GetValue(prefixOption);
            var region = parseResult.GetValue(regionOption);
            var accessKey = parseResult.GetValue(accessKeyOption);
            var secretKey = parseResult.GetValue(secretKeyOption);
            var dryRun = parseResult.GetValue(dryRunOption);

            var args = new List<string>();
            if (buildDir != null) { args.Add("-b"); args.Add(buildDir); }
            if (endpoint != null) { args.Add("-e"); args.Add(endpoint); }
            if (bucket != null) { args.Add("--bucket"); args.Add(bucket); }
            if (prefix != null) { args.Add("--prefix"); args.Add(prefix); }
            if (region != null) { args.Add("--region"); args.Add(region); }
            if (accessKey != null) { args.Add("--access-key"); args.Add(accessKey); }
            if (secretKey != null) { args.Add("--secret-key"); args.Add(secretKey); }
            if (dryRun) { args.Add("--dry-run"); }

            return await UploadCommand.RunAsync(args.ToArray());
        });

        return command;
    }

    #endregion

    #region Status Command

    private static Command BuildStatusCommand()
    {
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Output status as JSON"
        };

        var channelOption = new Option<string?>("--channel", "-c")
        {
            Description = "Show status for specific channel"
        };

        var command = new Command("status", "Show workspace status and pending operations")
        {
            jsonOption,
            channelOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            var channel = parseResult.GetValue(channelOption);

            var args = BuildArgs(
                ("json", json ? "true" : null),
                ("channel", channel));
            return await StatusCommand.RunAsync(args);
        });

        return command;
    }

    #endregion

    #region Info Command

    private static Command BuildInfoCommand()
    {
        var fileArg = new Argument<string?>("file")
        {
            Description = "Path to manifest or signature file to inspect",
            Arity = ArgumentArity.ZeroOrOne
        };

        var command = new Command("info", "Display information about manifest or signature files")
        {
            fileArg
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var file = parseResult.GetValue(fileArg);
            var args = file != null ? new[] { file } : Array.Empty<string>();
            return await InfoCommand.RunAsync(args);
        });

        return command;
    }

    #endregion

#if FONT_PREVIEW
    #region Fonts Command

    private static Command BuildFontsCommand()
    {
        var textOption = new Option<string?>("--text", "-t")
        {
            Description = "Text to preview (default: PatchSync)"
        };

        var command = new Command("fonts", "Interactive title style preview (↑/↓ to cycle)")
        {
            textOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var text = parseResult.GetValue(textOption);

            var args = new List<string>();
            if (text != null) { args.Add("--text"); args.Add(text); }

            return await FontPreviewCommand.RunAsync(args.ToArray());
        });

        return command;
    }

    #endregion
#endif

    #region Helpers

    private static string[] BuildArgs(params (string name, string? value)[] options)
    {
        var args = new List<string>();
        foreach (var (name, value) in options)
        {
            if (value != null)
            {
                args.Add($"--{name}");
                args.Add(value);
            }
        }
        return args.ToArray();
    }

    #endregion
}
