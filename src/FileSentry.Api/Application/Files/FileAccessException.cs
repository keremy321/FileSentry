namespace FileSentry.Api.Application.Files;

public sealed class FileAccessException(
    int statusCode,
    string code,
    string title,
    string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public string Code { get; } = code;

    public string Title { get; } = title;
}
