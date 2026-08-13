using FileSentry.Api.Domain.Scanning;

namespace FileSentry.Api.Infrastructure.Scanning;

public sealed record ClamAvScanResult(
    ScanAttemptResult Result,
    ScanFailureCode? FailureCode = null,
    bool IsRetryable = false,
    string? DetectionName = null,
    string? ScannerVersion = null)
{
    public static ClamAvScanResult Clean() => new(ScanAttemptResult.Clean);

    public static ClamAvScanResult Infected(string detectionName) => new(
        ScanAttemptResult.Infected,
        DetectionName: detectionName);

    public static ClamAvScanResult Failed(
        ScanFailureCode failureCode,
        bool isRetryable) => new(
            ScanAttemptResult.Failed,
            failureCode,
            isRetryable);
}
