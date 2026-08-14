namespace FileSentry.Client;

/// <summary>Describes the current persisted state of an owned file.</summary>
/// <param name="FileId">The server-generated file identifier.</param>
/// <param name="OriginalFileName">Server-sanitized display metadata; never use it as a path.</param>
/// <param name="SizeBytes">The actual streamed size in bytes.</param>
/// <param name="Sha256">The lowercase hexadecimal SHA-256 digest.</param>
/// <param name="DetectedMediaType">The server-detected media type.</param>
/// <param name="Status">The current workflow status.</param>
/// <param name="CreatedAtUtc">The UTC acceptance timestamp.</param>
/// <param name="UpdatedAtUtc">The UTC timestamp of the latest persisted update.</param>
public sealed record FileMetadata(
    Guid FileId,
    string OriginalFileName,
    long SizeBytes,
    string Sha256,
    string DetectedMediaType,
    FileStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
