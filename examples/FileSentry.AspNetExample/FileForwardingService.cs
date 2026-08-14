using FileSentry.Client;

namespace FileSentry.AspNetExample;

internal sealed class FileForwardingService(IFileSentryClient fileSentryClient)
{
    public async Task<ForwardedFile> ForwardAsync(
        Stream untrustedContent,
        string originalFileName,
        CancellationToken cancellationToken)
    {
        FileUpload upload = await fileSentryClient.UploadAsync(
            untrustedContent,
            originalFileName,
            cancellationToken);
        FileMetadata result = await fileSentryClient.WaitForScanAsync(
            upload.FileId,
            cancellationToken);
        if (result.Status != FileStatus.Clean)
        {
            return new ForwardedFile(result, cleanContent: null);
        }

        Stream cleanContent = await fileSentryClient.DownloadAsync(
            result.FileId,
            cancellationToken);
        return new ForwardedFile(result, cleanContent);
    }
}

internal sealed class ForwardedFile(
    FileMetadata metadata,
    Stream? cleanContent) : IAsyncDisposable
{
    private bool _disposed;

    public FileMetadata Metadata { get; } = metadata;

    public Stream? CleanContent { get; } = cleanContent;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (CleanContent is not null)
        {
            await CleanContent.DisposeAsync();
        }
    }
}
