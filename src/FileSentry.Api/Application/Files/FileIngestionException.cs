namespace FileSentry.Api.Application.Files;

public sealed class FileIngestionException(
    int statusCode,
    string code,
    string title,
    string detail) : Exception(detail)
{
    public int StatusCode { get; } = statusCode;

    public string Code { get; } = code;

    public string Title { get; } = title;
}
