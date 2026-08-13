namespace FileSentry.Api.Domain.Scanning;

public enum ScanAttemptResult
{
    InProgress = 0,
    Clean = 1,
    Infected = 2,
    Failed = 3
}
