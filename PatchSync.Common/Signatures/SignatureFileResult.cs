namespace PatchSync.SDK;

public struct SignatureFileResult
{
  public SignatureFileStatus Status { get; private init; }
  public long BytesTotal { get; private init; }
  public long BytesProcessed { get; private init; }
  
  public static SignatureFileResult Started(long bytesTotal) => new()
  {
    Status = SignatureFileStatus.Started,
    BytesTotal = bytesTotal,
    BytesProcessed = 0
  };
  
  public static SignatureFileResult InProgress(long bytesTotal, long bytesProcessed) => new()
  {
    Status = SignatureFileStatus.InProgress,
    BytesProcessed = bytesProcessed
  };
  
  public static SignatureFileResult Completed() => new()
  {
    Status = SignatureFileStatus.Completed
  };
}