using System.IO.Hashing;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text.Json;
using PatchSync.CLI.Json;
using PatchSync.Common.Manifest;
using PatchSync.Common.Signatures;
using PatchSync.Common.Text;
using PatchSync.SDK;
using PatchSync.SDK.Signatures;
using Spectre.Console;

namespace PatchSync.CLI.Commands;

public partial class PatchInstallation : ICommand
{
    public string Name => "Patch Installation";

    public void ExecuteCommand(CLIContext cliContext)
    {
        var manifestFilePath = GetManifestFile();
        var manifestFileInfo = new FileInfo(manifestFilePath);

        PatchManifest? manifest;
        using (var manifestStream = File.Open(manifestFilePath, FileMode.Open, FileAccess.Read))
        {
            manifest = JsonSerializer.Deserialize(
                manifestStream,
                JsonSourceGenerationContext.Default.PatchManifest
            );
        }

        if (manifest == null)
        {
            throw new Exception("Failed to deserialize manifest.json");
        }

        var installationPath = GetInputFolder();

        var filesInInstallation = Directory
            .EnumerateFiles(installationPath, "*", SearchOption.AllDirectories)
            .Select(filePath => Path.GetRelativePath(installationPath, filePath))
            .ToHashSet();

        var patchFolder = Path.Combine(manifestFileInfo.DirectoryName, manifest.Date.ToString("yyyy-MM-dd-HH-mm-ss"));

        var hasher = new XxHash3();

        var commandsToExecute = manifest.Files.Where(
            entry =>
            {
                if (!File.Exists(Path.Combine(patchFolder, $"{entry.FilePath}.sig")))
                {
                    throw new Exception($"Missing patch file from manifest: {entry.FilePath}");
                }

                if (entry.Command is ManifestFileCommand.AlwaysFullUpdate)
                {
                    return true;
                }

                // File is missing
                if (!filesInInstallation.Contains(entry.FilePath))
                {
                    return entry.Command is not ManifestFileCommand.Delete;
                }

                if (entry.Command is ManifestFileCommand.UpdateIfMissing)
                {
                    return false;
                }

                var fullPath = Path.Combine(installationPath, entry.FilePath);

                // No hash indicates a full update
                if (string.IsNullOrWhiteSpace(entry.Hash))
                {
                    return true;
                }

                if (entry.Command is ManifestFileCommand.DeltaUpdate)
                {
                    using var file = File.Open(fullPath, FileMode.Open, FileAccess.Read);
                    hasher.Append(file);
                    var existingHash = hasher.GetCurrentHashAsUInt64().ToString();
                    hasher.Reset();

                    if (existingHash != entry.FastHash)
                    {
                        return true;
                    }
                }
                else if (entry.Command is ManifestFileCommand.Delete)
                {
                    return true;
                }

                if (entry.Command is ManifestFileCommand.UpdateIfFullHashMismatch)
                {
                    using var stream = File.Open(fullPath, FileMode.Open, FileAccess.Read);
                    Span<byte> fullHashBuffer = stackalloc byte[32];
                    SHA256.HashData(stream, fullHashBuffer);

                    var hash = fullHashBuffer.ToHexString();
                    if (hash != entry.Hash)
                    {
                        return true;
                    }
                }

                return false;
            }
        ).ToList();

        if (commandsToExecute.Count == 0)
        {
            AnsiConsole.MarkupLine("Installation is [green]up to date[/].");
            AnsiConsole.WriteLine();
            return;
        }

        var table = new Table();
        table.AddColumn("File");
        table.AddColumn("File");
        table.Border(TableBorder.Heavy);
        table.Collapse();

        var manifestFiles = commandsToExecute.OrderBy(entry => entry.FilePath, StringComparer.OrdinalIgnoreCase).ToArray();

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

        if (!PromptReadyToPatch())
        {
            return;
        }

        var tempFolder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempFolder);

        Span<byte> buffer = stackalloc byte[32];

        foreach (var command in commandsToExecute)
        {
            var file = Path.Combine(installationPath, command.FilePath);
            var remoteFile = Path.Combine(patchFolder, command.FilePath);

            if (!filesInInstallation.Contains(command.FilePath))
            {
                if (command.Command is not ManifestFileCommand.Delete and not ManifestFileCommand.NeverUpdate)
                {
                    File.Copy(remoteFile, file);
                }
                continue;
            }

            switch (command.Command)
            {
                case ManifestFileCommand.Delete:
                    {
                        File.Delete(file);
                        break;
                    }
                case ManifestFileCommand.AlwaysFullUpdate:
                    {
                        File.Delete(file);
                        File.Copy(remoteFile, file);
                        break;
                    }
                case ManifestFileCommand.UpdateIfFullHashMismatch:
                    {
                        var fi = new FileInfo(file);

                        var hasChanges = command.FileSize != fi.Length;
                        if (!hasChanges)
                        {
                            using var stream = File.Open(file, FileMode.Open, FileAccess.Read);
                            SHA256.HashData(stream, buffer);
                            hasChanges = command.Hash != buffer.ToHexString();
                        }

                        if (hasChanges)
                        {
                            File.Delete(file);
                            File.Copy(remoteFile, file);
                        }

                        break;
                    }
                case ManifestFileCommand.DeltaUpdate:
                    {
                        // May not be the same (different remote path, or URL)
                        var signatureFilePath = Path.Combine(patchFolder, $"{command.FilePath}.sig");

                        if (!DoLocalDeltaPatching(command, signatureFilePath, file, remoteFile, tempFolder, command.ChunkSize))
                        {
                            File.Delete(file);
                            File.Copy(remoteFile, file);
                        }

                        break;
                    }
            }
        }
    }

    private static bool DoLocalDeltaPatching(
        ManifestFileEntry command,
        string signatureFilePath,
        string installFilePath,
        string remoteFilePath,
        string tempFolderPath,
        int chunkSize
    )
    {
        SignatureFile sigFile;
        using (var sigFileStream = File.Open(signatureFilePath, FileMode.Open, FileAccess.Read))
        {
            sigFile = SignatureFileHandler.LoadSignature(command.FileSize, sigFileStream, chunkSize);
        }

        var tempFilePath = Path.Combine(tempFolderPath, command.FilePath);
        var hasher = SHA256.Create();

        using (var mmf = MemoryMappedFile.CreateFromFile(installFilePath, FileMode.Open))
        {
            using var installationFileStream = mmf.CreateViewStream();
            var deltas = FilePatcher.GetPatchDeltas(installationFileStream, sigFile);

            var anyLocal = false;
            var anyRemote = false;
            var anyLocalOutOfOrder = false;
            var remoteThreshold = false;

            // TODO: This should be calculated and added to the manifest. Also should include a link to a pre-zipped version of the file.
            var threshold = (long)(command.FileSize / 1.5); // 2/3rds are changes
            var remoteTotal = 0L;

            // We do not handle remaining data, since it is available in the sig file, and tacked onto the end.
            for (var i = 0; !(anyLocal && anyRemote || remoteThreshold) && i < deltas.Length; i++)
            {
                var delta = deltas[i];
                if (delta.Location is PatchSliceLocation.ExistingSlice)
                {
                    anyLocal = true;
                    if (!anyLocalOutOfOrder && delta.Offset != chunkSize * i)
                    {
                        anyLocalOutOfOrder = true;
                    }
                }

                if (delta.Location is PatchSliceLocation.RemoteSlice)
                {
                    anyRemote = true;
                    remoteTotal += chunkSize;
                    if (remoteTotal >= threshold)
                    {
                        remoteThreshold = true;
                    }
                }
            }

            // We should just download the entire file
            if (remoteThreshold)
            {
                return false;
            }

            // If we don't have any remote slices, and no out of order local slices, then check if the file is the same.
            // Check length, then SHA256 hash.
            if (!anyRemote && !anyLocalOutOfOrder && installationFileStream.Length == command.FileSize)
            {
                Span<byte> buffer = stackalloc byte[32];
                installationFileStream.Seek(0, SeekOrigin.Begin);
                SHA256.HashData(installationFileStream, buffer);
                return command.Hash != buffer.ToHexString();
            }

            // Generally would be piece-wise downloads from the web server
            using var remoteMmf = MemoryMappedFile.CreateFromFile(remoteFilePath, FileMode.Open);
            using var remoteFileStream = remoteMmf.CreateViewStream();

            // Build a temporary file

            Directory.CreateDirectory(Path.GetDirectoryName(tempFilePath)); // For nested paths

            using (var tempFile = File.Create(tempFilePath))
            {
                var chunk = new byte[chunkSize];
                foreach (var delta in deltas)
                {
                    var sourceStream = delta.Location is PatchSliceLocation.ExistingSlice
                        ? installationFileStream
                        : remoteFileStream;

                    sourceStream.Seek(delta.Offset, SeekOrigin.Begin);

                    var bytesRead = 0;
                    while (bytesRead < chunkSize)
                    {
                        int read = sourceStream.Read(chunk, bytesRead, chunkSize - bytesRead);
                        if (read == 0)
                        {
                            throw new IOException("Prematurely reached end of file.");
                        }

                        bytesRead += read;
                    }

                    tempFile.Write(chunk);
                    hasher.TransformBlock(chunk, 0, chunkSize, null, 0);
                }

                if (sigFile.RemainingData.Length > 0)
                {
                    tempFile.Write(sigFile.RemainingData);
                    hasher.TransformFinalBlock(sigFile.RemainingData, 0, sigFile.RemainingData.Length);
                }
                else
                {
                    hasher.TransformFinalBlock(chunk, 0, 0);
                }
            }
        }

        if (command.Hash == hasher.Hash.ToHexString())
        {
            File.Delete(installFilePath);
            File.Move(tempFilePath, installFilePath);
            Directory.Delete(tempFolderPath, true);
            return true;
        }

        Directory.Delete(tempFolderPath, true);
        return false;
    }
}
