namespace PatchSync.Common;

public class FileProcessingResult
{
    public FileProcessingStatus ProcessingStatus { get; private set; }
    public long BytesTotal { get; private set; }
    public long BytesProcessed { get; private set; }

    public void Started(long bytesTotal)
    {
        ProcessingStatus = FileProcessingStatus.Started;
        BytesTotal = bytesTotal;
        BytesProcessed = 0;
    }

    public void InProgress(long bytesProcessed)
    {
        ProcessingStatus = FileProcessingStatus.InProgress;
        BytesProcessed = bytesProcessed;
    }

    public void Completed()
    {
        ProcessingStatus = FileProcessingStatus.Completed;
        BytesProcessed = BytesTotal;
    }
}
