namespace FileSentry.Client;

/// <summary>Provides streaming access to the FileSentry file API.</summary>
public interface IFileSentryClient
{
    /// <summary>Uploads a file for validation and asynchronous scanning.</summary>
    /// <param name="content">The readable upload stream. The caller retains ownership.</param>
    /// <param name="fileName">The untrusted display filename sent as multipart metadata.</param>
    /// <param name="cancellationToken">Cancels the HTTP operation.</param>
    /// <returns>The accepted file metadata and initial status.</returns>
    Task<FileUpload> UploadAsync(
        Stream content,
        string fileName,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the current metadata for an owned file.</summary>
    /// <param name="fileId">The non-empty FileSentry file identifier.</param>
    /// <param name="cancellationToken">Cancels the HTTP operation.</param>
    /// <returns>The current file metadata.</returns>
    Task<FileMetadata> GetFileAsync(
        Guid fileId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists metadata for files owned by the configured service identity.</summary>
    /// <param name="cancellationToken">Cancels the HTTP operation.</param>
    /// <returns>A read-only snapshot of owned file metadata.</returns>
    Task<IReadOnlyList<FileMetadata>> ListFilesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Polls until the file reaches a known terminal status.</summary>
    /// <param name="fileId">The non-empty FileSentry file identifier.</param>
    /// <param name="cancellationToken">Cancels polling independently of the configured timeout.</param>
    /// <returns>Metadata whose status is clean, infected, scan failed, or deleted.</returns>
    /// <exception cref="FileSentryPollingTimeoutException">
    /// The configured scan timeout elapsed before a terminal status was observed.
    /// </exception>
    Task<FileMetadata> WaitForScanAsync(
        Guid fileId,
        CancellationToken cancellationToken = default);

    /// <summary>Opens a streaming download for an owned, conclusively clean file.</summary>
    /// <param name="fileId">The non-empty FileSentry file identifier.</param>
    /// <param name="cancellationToken">Cancels the request through response-stream acquisition.</param>
    /// <returns>
    /// A response-owned stream that must be disposed to release its HTTP response.
    /// </returns>
    Task<Stream> DownloadAsync(
        Guid fileId,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes an owned file and its remaining stored bytes.</summary>
    /// <param name="fileId">The non-empty FileSentry file identifier.</param>
    /// <param name="cancellationToken">Cancels the HTTP operation.</param>
    Task DeleteAsync(
        Guid fileId,
        CancellationToken cancellationToken = default);
}
