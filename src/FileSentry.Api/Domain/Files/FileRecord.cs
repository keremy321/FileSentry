namespace FileSentry.Api.Domain.Files;

public sealed class FileRecord
{
    public Guid Id { get; set; }

    public Guid OwnerId { get; set; }

    public string OriginalFileName { get; set; } = string.Empty;

    public string StorageName { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public string DetectedMediaType { get; set; } = string.Empty;

    public string? ClientMediaType { get; set; }

    public FileRecordStatus Status { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}
