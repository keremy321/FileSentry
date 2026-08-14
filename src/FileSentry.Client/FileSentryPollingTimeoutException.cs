namespace FileSentry.Client;

public sealed class FileSentryPollingTimeoutException(
    Guid fileId,
    TimeSpan timeout)
    : FileSentryException(
        $"File {fileId} did not reach a terminal scan state within {timeout}.")
{
    public Guid FileId { get; } = fileId;

    public TimeSpan Timeout { get; } = timeout;
}
