namespace FileSentry.Api.Infrastructure.Options;

public sealed class ScannerWorkerOptions
{
    public const string SectionName = "ScannerWorker";

    public bool Enabled { get; set; } = true;

    public int MaximumAttempts { get; set; } = 3;

    public int InitialRetryDelaySeconds { get; set; } = 5;

    public int MaximumRetryDelaySeconds { get; set; } = 60;

    public int StuckJobTimeoutSeconds { get; set; } = 120;

    public int PollingIntervalMilliseconds { get; set; } = 1000;
}
