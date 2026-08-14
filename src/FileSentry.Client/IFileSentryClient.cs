namespace FileSentry.Client;

public interface IFileSentryClient
{
    Task<FileUpload> UploadAsync(
        Stream content,
        string fileName,
        CancellationToken cancellationToken = default);

    Task<FileMetadata> GetFileAsync(
        Guid fileId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FileMetadata>> ListFilesAsync(
        CancellationToken cancellationToken = default);

    Task<FileMetadata> WaitForScanAsync(
        Guid fileId,
        CancellationToken cancellationToken = default);

    Task<Stream> DownloadAsync(
        Guid fileId,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        Guid fileId,
        CancellationToken cancellationToken = default);
}
