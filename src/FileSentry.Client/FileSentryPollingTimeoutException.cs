namespace FileSentry.Client;

/// <summary>Represents scan polling that exceeded the configured timeout.</summary>
/// <param name="fileId">The file that did not reach a terminal status.</param>
/// <param name="timeout">The configured polling timeout.</param>
public sealed class FileSentryPollingTimeoutException(
    Guid fileId,
    TimeSpan timeout)
    : FileSentryException(
        $"File {fileId} did not reach a terminal scan state within {timeout}.")
{
    /// <summary>Gets the file that did not reach a terminal status.</summary>
    public Guid FileId { get; } = fileId;

    /// <summary>Gets the configured polling timeout.</summary>
    public TimeSpan Timeout { get; } = timeout;
}
