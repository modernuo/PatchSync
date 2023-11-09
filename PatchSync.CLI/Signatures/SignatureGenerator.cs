using System.IO.MemoryMappedFiles;
using PatchSync.SDK;
using PatchSync.SDK.Signatures;
using Spectre.Console;

namespace PatchSync.Signatures;

public static class SignatureGenerator
{
  public static ulong GenerateSignature(string filePath, string signaturePath, int chunkSize, ProgressTask? task)
  {
    using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open);
    using var mmStream = mmf.CreateViewStream();

    var fileSize = mmStream.Length;
    using var fileStream = File.Open(signaturePath, FileMode.Create);

    return SignatureFileHandler.CreateSignatureFile(mmStream, fileStream, chunkSize, callback: signatureResult =>
    {
      switch (signatureResult.Status)
      {
        case SignatureFileStatus.Started:
          {
            task!.MaxValue(fileSize).StartTask();
            break;
          }
        case SignatureFileStatus.InProgress:
          {
            task!.Increment(signatureResult.BytesProcessed);
            break;
          }
        case SignatureFileStatus.Completed:
          {
            task!.StopTask();
            break;
          }
        default:
          {
            throw new ArgumentOutOfRangeException(nameof(signatureResult));
          }
      };
    });
  }
}