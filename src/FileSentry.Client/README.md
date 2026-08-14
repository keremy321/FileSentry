# FileSentry.Client

`FileSentry.Client` is the .NET 10 client for the existing FileSentry `/api/v1/files`
service API. The package is in pre-publication development and is not yet available
from NuGet.

```csharp
using FileSentry.Client;

using var client = new FileSentryClient(new FileSentryClientOptions
{
    BaseAddress = new Uri("https://filesentry.example/"),
    ApiKey = configuration["FileSentry:ApiKey"]!,
    PollingInterval = TimeSpan.FromSeconds(1),
    ScanTimeout = TimeSpan.FromMinutes(5)
});

await using Stream uploadContent = File.OpenRead("cv.pdf");
FileUpload upload = await client.UploadAsync(uploadContent, "cv.pdf", cancellationToken);
FileMetadata result = await client.WaitForScanAsync(upload.FileId, cancellationToken);

if (result.Status == FileStatus.Clean)
{
    await using Stream clean = await client.DownloadAsync(
        upload.FileId,
        cancellationToken);
    // Process the stream without assuming the content is harmless.
}
```

The safe workflow is `Upload -> Wait for a terminal state -> continue only if
Clean -> Download`. `Infected`, `ScanFailed`, and `Deleted` are terminal but never
successful. An unknown state fails with `FileSentryProtocolException`.

The SDK streams uploads and downloads and attaches `X-Api-Key` per request. Keep
the key in an external secret provider. Do not log it or embed it in source. An
injected `HttpClient` can be supplied through the alternate constructor; its
default headers are not modified and its lifetime remains owned by the caller.
Configure an injected client's primary handler with automatic redirects disabled
so a custom authentication header cannot be forwarded to another origin. The
options-only constructor does this by default.

`IFileSentryClient` supports dependency injection and focused consumer tests.
`FileSentryClientOptions.Validate()` enables fail-fast application startup before a
client is first resolved. See the repository's
[runnable examples](https://github.com/keremy321/FileSentry/tree/main/examples) for
console and `HttpClientFactory` integration patterns.

API ProblemDetails responses produce `FileSentryApiException`, with status, stable
code, title, detail, type, and instance where available. Polling timeout and invalid
wire responses use dedicated exception types.

A FileSentry `Clean` result reduces risk; it does not guarantee that content is
harmless. The server remains authoritative for validation, scanning, ownership,
and download authorization.
