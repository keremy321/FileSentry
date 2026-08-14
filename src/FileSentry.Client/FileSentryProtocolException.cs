namespace FileSentry.Client;

public sealed class FileSentryProtocolException(string message) : FileSentryException(message);
