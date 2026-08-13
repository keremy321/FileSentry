namespace FileSentry.Api.Infrastructure.Scanning;

public interface IClamAvClient
{
    Task<ClamAvScanResult> ScanAsync(
        Stream content,
        CancellationToken cancellationToken);
}
