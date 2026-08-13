namespace FileSentry.Api.Contracts.Files;

public sealed record FileMetadataResponse(
    Guid FileId,
    string OriginalFileName,
    long SizeBytes,
    string Sha256,
    string DetectedMediaType,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
