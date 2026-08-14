# FileSentry.Client

`FileSentry.Client` is the streaming .NET 10 client for FileSentry's
service-authenticated `/api/v1/files` API. The package is currently prepared as
`1.1.0-preview.1`; it is not published yet.

An antivirus `Clean` result reduces risk. It does not prove that content is
harmless. Keep normal file-type, parser, sandboxing, and authorization controls in
the consuming application.

## Install

After the preview is published:

```powershell
dotnet add package FileSentry.Client --version 1.1.0-preview.1
```

Until then, repository examples use a project reference to
`src/FileSentry.Client`.

## Safe workflow

Always use this sequence:

```text
Upload -> Wait for terminal scan status -> continue only if Clean -> Download/process
```

```csharp
using FileSentry.Client;

string apiKey = configuration["FileSentry:ApiKey"]
    ?? throw new InvalidOperationException("FileSentry:ApiKey is required.");

var options = new FileSentryClientOptions
{
    BaseAddress = new Uri("https://filesentry.example/"),
    ApiKey = apiKey,
    PollingInterval = TimeSpan.FromSeconds(1),
    ScanTimeout = TimeSpan.FromMinutes(5)
};
options.Validate();

using var client = new FileSentryClient(options);
await using Stream uploadContent = File.OpenRead("document.pdf");
FileUpload upload = await client.UploadAsync(
    uploadContent,
    "document.pdf",
    cancellationToken);

FileMetadata result = await client.WaitForScanAsync(
    upload.FileId,
    cancellationToken);

if (result.Status == FileStatus.Clean)
{
    await using Stream cleanContent = await client.DownloadAsync(
        result.FileId,
        cancellationToken);
    // Process the stream while still treating the document format as untrusted.
}
```

`PendingScan` and `Scanning` are active states. `Clean`, `Infected`, `ScanFailed`,
and `Deleted` are terminal. Only exact `Clean` permits content use. Unknown status
values fail closed with `FileSentryProtocolException`.

## ASP.NET Core and dependency injection

Register the options once, validate them during startup, and use a typed
`HttpClient`. Automatic redirects are disabled so the custom authentication header
cannot be forwarded to another origin.

```csharp
using FileSentry.Client;

var options = new FileSentryClientOptions
{
    BaseAddress = new Uri(
        builder.Configuration["FileSentry:BaseUrl"]
            ?? throw new InvalidOperationException("FileSentry:BaseUrl is required.")),
    ApiKey = builder.Configuration["FileSentry:ApiKey"]
        ?? throw new InvalidOperationException("FileSentry:ApiKey is required.")
};
options.Validate();

builder.Services.AddSingleton(options);
builder.Services
    .AddHttpClient<IFileSentryClient, FileSentryClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false
    });
```

The repository's [console and ASP.NET Core examples](https://github.com/keremy321/FileSentry/tree/main/examples)
show complete project-reference integrations.

## Cancellation and polling

Every operation accepts a `CancellationToken`. Caller cancellation is reported as
`OperationCanceledException`. `WaitForScanAsync` polls using `PollingInterval` and
throws `FileSentryPollingTimeoutException` when `ScanTimeout` expires before a
terminal state. Uploads are not automatically retried because the input stream may
not be replayable.

## Stream and HTTP ownership

- `UploadAsync` reads but never disposes or rewinds the caller's stream. Keep it
  readable until the returned task completes.
- `DownloadAsync` returns a lazy response-owned stream. Dispose it with `using` or
  `await using`; disposal releases both the content stream and HTTP response.
- The options-only `FileSentryClient` constructor creates and owns its `HttpClient`;
  dispose the client.
- The constructor accepting `HttpClient` does not dispose that client or modify its
  default headers. Its caller or DI container owns the HTTP lifetime.
- Cancellation passed to `DownloadAsync` covers the request and response headers.
  Pass an appropriate token to subsequent stream reads as well.

## Errors and credentials

- `FileSentryApiException` represents a non-success API response and exposes the
  HTTP status plus bounded ProblemDetails fields when available.
- `FileSentryProtocolException` represents malformed, missing, or unknown success
  data.
- `FileSentryPollingTimeoutException` represents the configured polling timeout.
- Transport failures remain `HttpRequestException`; caller cancellation remains
  `OperationCanceledException`.

The SDK sends `X-Api-Key` on each request without changing shared default headers.
Load the key from an external secret provider, never source code. The SDK redacts
the configured key if an API error reflects it, but consumers should still avoid
logging arbitrary exception detail in sensitive environments.

The FileSentry server remains authoritative for validation, hashing, scanning,
ownership, and clean-download authorization.
