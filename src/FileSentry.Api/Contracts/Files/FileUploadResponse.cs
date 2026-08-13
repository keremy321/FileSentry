namespace FileSentry.Api.Contracts.Files;

public sealed record FileUploadResponse(
    Guid FileId,
    string OriginalFileName,
    long SizeBytes,
    string Sha256,
    string DetectedMediaType,
    string Status,
    DateTimeOffset CreatedAtUtc);
