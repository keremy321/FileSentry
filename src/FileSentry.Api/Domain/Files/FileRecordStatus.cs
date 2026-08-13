namespace FileSentry.Api.Domain.Files;

public enum FileRecordStatus
{
    PendingScan = 0,
    Scanning = 1,
    Clean = 2,
    Infected = 3,
    ScanFailed = 4,
    Deleted = 5
}
