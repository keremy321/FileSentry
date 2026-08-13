namespace FileSentry.Api.Domain.Scanning;

public enum ScanFailureCode
{
    Timeout = 0,
    ScannerUnavailable = 1,
    MalformedResponse = 2,
    ScannerError = 3,
    ScannerSizeLimit = 4,
    FileMissing = 5,
    StorageError = 6,
    WorkerInterrupted = 7,
    Unknown = 8
}
