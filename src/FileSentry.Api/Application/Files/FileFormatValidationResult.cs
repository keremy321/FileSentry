namespace FileSentry.Api.Application.Files;

public sealed record FileFormatValidationResult(
    SupportedFileFormat? DetectedFormat,
    bool IsStructurallyValid,
    bool IsZipContainer = false);
