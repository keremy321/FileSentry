namespace FileSentry.Client;

/// <summary>Provides the base type for FileSentry-specific failures.</summary>
public abstract class FileSentryException : Exception
{
    /// <summary>Initializes the exception with a safe failure message.</summary>
    /// <param name="message">The safe failure message.</param>
    protected FileSentryException(string message)
        : base(message)
    {
    }
}
