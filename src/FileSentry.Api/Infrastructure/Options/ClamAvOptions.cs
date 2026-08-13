namespace FileSentry.Api.Infrastructure.Options;

public sealed class ClamAvOptions
{
    public const string SectionName = "ClamAV";

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 3310;

    public int TimeoutSeconds { get; set; } = 5;

    public int ScanTimeoutSeconds { get; set; } = 30;

    public long MaximumStreamSizeBytes { get; set; } = 10 * 1024 * 1024;

    public int StreamChunkSizeBytes { get; set; } = 64 * 1024;
}
