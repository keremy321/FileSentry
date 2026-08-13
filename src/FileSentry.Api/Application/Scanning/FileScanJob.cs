namespace FileSentry.Api.Application.Scanning;

public sealed record FileScanJob(
    Guid FileRecordId,
    Guid ScanAttemptId,
    int AttemptNumber,
    string StorageName,
    long ExpectedSizeBytes,
    string CorrelationId);
