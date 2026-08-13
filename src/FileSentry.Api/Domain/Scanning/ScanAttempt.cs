namespace FileSentry.Api.Domain.Scanning;

public sealed class ScanAttempt
{
    public Guid Id { get; set; }

    public Guid FileRecordId { get; set; }

    public int AttemptNumber { get; set; }

    public DateTimeOffset StartedAtUtc { get; set; }

    public DateTimeOffset? CompletedAtUtc { get; set; }

    public ScanAttemptResult Result { get; set; }

    public ScanFailureCode? FailureCode { get; set; }

    public bool IsRetryable { get; set; }

    public string? DetectionName { get; set; }

    public string? ScannerVersion { get; set; }
}
