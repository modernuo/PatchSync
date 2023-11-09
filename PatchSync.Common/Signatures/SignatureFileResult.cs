namespace PatchSync.SDK;

public record SignatureFileResult
{
  public SignatureFileStatus Status { get; private init; }
  public long BytesTotal { get; private init; }
  public long BytesProcessed { get; private init; }
  public string? Message { get; private init; }
  
  public static SignatureFileResult Started(long bytesTotal) => new()
  {
    Status = SignatureFileStatus.Started,
    BytesTotal = bytesTotal,
    BytesProcessed = 0
  };
  
  public static SignatureFileResult InProgress(long bytesTotal, long bytesProcessed, string? message = null) => new()
  {
    Status = SignatureFileStatus.InProgress,
    Message = message,
    BytesProcessed = bytesProcessed
  };
  
  public static SignatureFileResult Completed() => new()
  {
    Status = SignatureFileStatus.Completed
  };
  
  public static SignatureFileResult Error(string? message = null) => new()
  {
    Status = SignatureFileStatus.Error,
    Message = message
  };
  
  private SignatureFileResult()
  {
  }
}