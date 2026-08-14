namespace FileSentry.Client;

public sealed record FileUpload(
    Guid FileId,
    string OriginalFileName,
    long SizeBytes,
    string Sha256,
    string DetectedMediaType,
    FileStatus Status,
    DateTimeOffset CreatedAtUtc);
