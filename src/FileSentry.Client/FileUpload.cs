namespace FileSentry.Client;

/// <summary>Describes a file accepted for asynchronous validation and scanning.</summary>
/// <param name="FileId">The server-generated file identifier.</param>
/// <param name="OriginalFileName">Server-sanitized display metadata; never use it as a path.</param>
/// <param name="SizeBytes">The actual streamed size in bytes.</param>
/// <param name="Sha256">The lowercase hexadecimal SHA-256 digest.</param>
/// <param name="DetectedMediaType">The server-detected media type.</param>
/// <param name="Status">The initial workflow status.</param>
/// <param name="CreatedAtUtc">The UTC acceptance timestamp.</param>
public sealed record FileUpload(
    Guid FileId,
    string OriginalFileName,
    long SizeBytes,
    string Sha256,
    string DetectedMediaType,
    FileStatus Status,
    DateTimeOffset CreatedAtUtc);
