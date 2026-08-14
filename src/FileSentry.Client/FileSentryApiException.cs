using System.Net;

namespace FileSentry.Client;

/// <summary>Represents a non-success response from the FileSentry API.</summary>
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

    /// <summary>Gets the actual HTTP response status code.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>Gets the optional status value from the ProblemDetails body.</summary>
    public int? ProblemStatus { get; }

    /// <summary>Gets the optional stable machine-readable FileSentry error code.</summary>
    public string? Code { get; }

    /// <summary>Gets the optional ProblemDetails title.</summary>
    public string? Title { get; }

    /// <summary>Gets the optional bounded ProblemDetails detail.</summary>
    public string? Detail { get; }

    /// <summary>Gets the optional ProblemDetails type identifier.</summary>
    public string? Type { get; }

    /// <summary>Gets the optional ProblemDetails instance identifier.</summary>
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
