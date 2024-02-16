using System.IO.MemoryMappedFiles;
using PatchSync.Common;
using PatchSync.SDK.Signatures;
using Spectre.Console;

namespace PatchSync.Signatures;

public static class SignatureGenerator
{
    public static (ulong, byte[]) GenerateSignature(string filePath, string signaturePath, int chunkSize, ProgressTask? task)
    {
        var fi = new FileInfo(filePath);
        var fileSize = fi.Length;

        using var mmf = MemoryMappedFile.CreateFromFile(
            filePath,
            FileMode.Open,
            null,
            fileSize,
            MemoryMappedFileAccess.Read
        );
        using var mmStream = mmf.CreateViewStream(0, fileSize, MemoryMappedFileAccess.Read);
        using var fileStream = File.Open(signaturePath, FileMode.Create);

        return SignatureFileHandler.CreateSignatureFile(
            mmStream,
            fileStream,
            chunkSize,
            signatureResult =>
            {
                switch (signatureResult.ProcessingStatus)
                {
                    case FileProcessingStatus.Started:
                        {
                            task.MaxValue(fileSize).StartTask();
                            break;
                        }
                    case FileProcessingStatus.InProgress:
                        {
                            task!.Increment(signatureResult.BytesProcessed);
                            break;
                        }
                    case FileProcessingStatus.Completed:
                        {
                            task!.StopTask();
                            break;
                        }
                    default:
                        {
                            throw new ArgumentOutOfRangeException(nameof(signatureResult));
                        }
                }
            }
        );
    }
}
