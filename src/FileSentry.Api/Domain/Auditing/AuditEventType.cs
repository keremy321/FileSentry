namespace FileSentry.Api.Domain.Auditing;

public enum AuditEventType
{
    UploadAccepted = 0,
    ScanStarted = 1,
    ScanClean = 2,
    MalwareDetected = 3,
    ScanFailed = 4,
    FileDownloaded = 5,
    FileDeleted = 6
}
