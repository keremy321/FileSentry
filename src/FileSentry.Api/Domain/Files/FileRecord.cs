using FileSentry.Api.Domain.Scanning;

namespace FileSentry.Api.Domain.Files;

public sealed class FileRecord
{
    public Guid Id { get; set; }

    public Guid OwnerId { get; set; }

    public string OriginalFileName { get; set; } = string.Empty;

    public string StorageName { get; set; } = string.Empty;

    public FileStorageState StorageState { get; set; }

    public long SizeBytes { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public string DetectedMediaType { get; set; } = string.Empty;

    public string? ClientMediaType { get; set; }

    public FileRecordStatus Status { get; set; }

    public int ScanAttemptCount { get; set; }

    public Guid? ActiveScanAttemptId { get; set; }

    public DateTimeOffset? ScanningStartedAtUtc { get; set; }

    public DateTimeOffset? NextScanAttemptAtUtc { get; set; }

    public DateTimeOffset? ScanCompletedAtUtc { get; set; }

    public string? DetectionName { get; set; }

    public string? LastScannerVersion { get; set; }

    public ScanFailureCode? LastScanFailureCode { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
