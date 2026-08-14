namespace FileSentry.Client;

/// <summary>Represents malformed, incomplete, or unsupported API response data.</summary>
/// <param name="message">The safe protocol failure message.</param>
public sealed class FileSentryProtocolException(string message) : FileSentryException(message);
