using System.Net;

namespace FileSentry.Client;

public sealed class FileSentryApiException : FileSentryException
{
    internal FileSentryApiException(
        HttpStatusCode statusCode,
        int? problemStatus,
        string? code,
        string? title,
        string? detail,
        string? type,
        string? instance)
        : base(CreateMessage(statusCode, code, title))
    {
        StatusCode = statusCode;
        ProblemStatus = problemStatus;
        Code = code;
        Title = title;
        Detail = detail;
        Type = type;
        Instance = instance;
    }

    public HttpStatusCode StatusCode { get; }

    public int? ProblemStatus { get; }

    public string? Code { get; }

    public string? Title { get; }

    public string? Detail { get; }

    public string? Type { get; }

    public string? Instance { get; }

    private static string CreateMessage(
        HttpStatusCode statusCode,
        string? code,
        string? title)
    {
        string safeTitle = string.IsNullOrWhiteSpace(title)
            ? "FileSentry API request failed"
            : title;
        return string.IsNullOrWhiteSpace(code)
            ? $"{safeTitle} (HTTP {(int)statusCode})."
            : $"{safeTitle} (HTTP {(int)statusCode}, code {code}).";
    }
}
