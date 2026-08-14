using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FileSentry.Client.Internal;

namespace FileSentry.Client;

public sealed class FileSentryClient : IFileSentryClient, IDisposable
{
    private const string ApiKeyHeaderName = "X-Api-Key";
    private const int MaximumProblemDetailsBytes = 64 * 1024;
    private const string FilesPath = "api/v1/files";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Uri _baseAddress;
    private readonly string _apiKey;
    private readonly TimeSpan _pollingInterval;
    private readonly TimeSpan _scanTimeout;
    private bool _disposed;

    public FileSentryClient(FileSentryClientOptions options)
        : this(
            new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false
            }),
            options,
            ownsHttpClient: true)
    {
    }

    public FileSentryClient(HttpClient httpClient, FileSentryClientOptions options)
        : this(httpClient, options, ownsHttpClient: false)
    {
    }

    private FileSentryClient(
        HttpClient httpClient,
        FileSentryClientOptions options,
        bool ownsHttpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ValidatedFileSentryClientOptions validatedOptions = options.ValidateAndNormalize();
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _baseAddress = validatedOptions.BaseAddress;
        _apiKey = validatedOptions.ApiKey;
        _pollingInterval = validatedOptions.PollingInterval;
        _scanTimeout = validatedOptions.ScanTimeout;
    }

    public async Task<FileUpload> UploadAsync(
        Stream content,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("The upload stream must be readable.", nameof(content));
        }

        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.Any(character => character is '\r' or '\n'))
        {
            throw new ArgumentException(
                "The upload filename must be non-empty and contain no line breaks.",
                nameof(fileName));
        }

        using var request = CreateRequest(HttpMethod.Post, FilesPath);
        using var multipart = new MultipartFormDataContent();
        using var streamContent = new StreamContent(new NonDisposingReadStream(content));
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        multipart.Add(streamContent, "file", fileName);
        request.Content = multipart;

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureStatusAsync(
            response,
            HttpStatusCode.Accepted,
            cancellationToken).ConfigureAwait(false);
        FileUploadWireModel wireModel = await ReadRequiredJsonAsync<FileUploadWireModel>(
            response.Content,
            cancellationToken).ConfigureAwait(false);
        return MapUpload(wireModel);
    }

    public async Task<FileMetadata> GetFileAsync(
        Guid fileId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureFileId(fileId);
        using var request = CreateRequest(HttpMethod.Get, GetFilePath(fileId));
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureStatusAsync(response, HttpStatusCode.OK, cancellationToken)
            .ConfigureAwait(false);
        FileMetadataWireModel wireModel = await ReadRequiredJsonAsync<FileMetadataWireModel>(
            response.Content,
            cancellationToken).ConfigureAwait(false);
        return MapMetadata(wireModel);
    }

    public async Task<IReadOnlyList<FileMetadata>> ListFilesAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var request = CreateRequest(HttpMethod.Get, FilesPath);
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureStatusAsync(response, HttpStatusCode.OK, cancellationToken)
            .ConfigureAwait(false);
        FileMetadataWireModel?[] wireModels =
            await ReadRequiredJsonAsync<FileMetadataWireModel?[]>(
                response.Content,
                cancellationToken).ConfigureAwait(false);
        return wireModels.Select(model => model is null
            ? throw UnexpectedResponse()
            : MapMetadata(model)).ToArray();
    }

    public async Task<FileMetadata> WaitForScanAsync(
        Guid fileId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureFileId(fileId);
        using var timeoutSource = new CancellationTokenSource(_scanTimeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            while (true)
            {
                FileMetadata metadata = await GetFileAsync(fileId, linkedSource.Token)
                    .ConfigureAwait(false);
                if (IsTerminal(metadata.Status))
                {
                    return metadata;
                }

                await Task.Delay(_pollingInterval, linkedSource.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested
                && timeoutSource.IsCancellationRequested)
        {
            throw new FileSentryPollingTimeoutException(fileId, _scanTimeout);
        }
    }

    public async Task<Stream> DownloadAsync(
        Guid fileId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureFileId(fileId);
        using var request = CreateRequest(
            HttpMethod.Get,
            $"{GetFilePath(fileId)}/download");
        HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureStatusAsync(response, HttpStatusCode.OK, cancellationToken)
                .ConfigureAwait(false);
            Stream content = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            return new ResponseOwnedStream(content, response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async Task DeleteAsync(
        Guid fileId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureFileId(fileId);
        using var request = CreateRequest(HttpMethod.Delete, GetFilePath(fileId));
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureStatusAsync(response, HttpStatusCode.NoContent, cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static bool IsTerminal(FileStatus status) => status is
        FileStatus.Clean
        or FileStatus.Infected
        or FileStatus.ScanFailed
        or FileStatus.Deleted;

    private static string GetFilePath(Guid fileId) => $"{FilesPath}/{fileId:D}";

    private static void EnsureFileId(Guid fileId)
    {
        if (fileId == Guid.Empty)
        {
            throw new ArgumentException("The file ID must not be empty.", nameof(fileId));
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
    {
        var request = new HttpRequestMessage(method, new Uri(_baseAddress, relativePath));
        request.Headers.Add(ApiKeyHeaderName, _apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task EnsureStatusAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == expectedStatus)
        {
            return;
        }

        throw await CreateApiExceptionAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FileSentryApiException> CreateApiExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        ProblemDetailsWireModel? problem = null;
        try
        {
            if (response.Content.Headers.ContentLength is not > MaximumProblemDetailsBytes)
            {
                byte[] body = await ReadBoundedAsync(
                    response.Content,
                    MaximumProblemDetailsBytes,
                    cancellationToken).ConfigureAwait(false);
                if (body.Length > 0)
                {
                    problem = JsonSerializer.Deserialize<ProblemDetailsWireModel>(body, JsonOptions);
                }
            }
        }
        catch (JsonException)
        {
        }

        return new FileSentryApiException(
            response.StatusCode,
            problem?.Status,
            RedactCredential(problem?.Code),
            RedactCredential(problem?.Title),
            RedactCredential(problem?.Detail),
            RedactCredential(problem?.Type),
            RedactCredential(problem?.Instance));
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (output.Length <= maximumBytes)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > maximumBytes)
            {
                return [];
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }

        return [];
    }

    private static async Task<T> ReadRequiredJsonAsync<T>(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        try
        {
            T? value = await content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return value ?? throw UnexpectedResponse();
        }
        catch (JsonException)
        {
            throw UnexpectedResponse();
        }
        catch (NotSupportedException)
        {
            throw UnexpectedResponse();
        }
    }

    private static FileUpload MapUpload(FileUploadWireModel model)
    {
        ValidateCommonResponse(
            model.FileId,
            model.OriginalFileName,
            model.SizeBytes,
            model.Sha256,
            model.DetectedMediaType,
            model.CreatedAtUtc);
        return new FileUpload(
            model.FileId,
            model.OriginalFileName!,
            model.SizeBytes,
            model.Sha256!,
            model.DetectedMediaType!,
            ParseStatus(model.Status),
            model.CreatedAtUtc);
    }

    private static FileMetadata MapMetadata(FileMetadataWireModel model)
    {
        ValidateCommonResponse(
            model.FileId,
            model.OriginalFileName,
            model.SizeBytes,
            model.Sha256,
            model.DetectedMediaType,
            model.CreatedAtUtc);
        if (model.UpdatedAtUtc == default)
        {
            throw UnexpectedResponse();
        }

        return new FileMetadata(
            model.FileId,
            model.OriginalFileName!,
            model.SizeBytes,
            model.Sha256!,
            model.DetectedMediaType!,
            ParseStatus(model.Status),
            model.CreatedAtUtc,
            model.UpdatedAtUtc);
    }

    private static void ValidateCommonResponse(
        Guid fileId,
        string? originalFileName,
        long sizeBytes,
        string? sha256,
        string? detectedMediaType,
        DateTimeOffset createdAtUtc)
    {
        if (fileId == Guid.Empty
            || originalFileName is null
            || sizeBytes < 0
            || string.IsNullOrWhiteSpace(sha256)
            || string.IsNullOrWhiteSpace(detectedMediaType)
            || createdAtUtc == default)
        {
            throw UnexpectedResponse();
        }
    }

    private static FileStatus ParseStatus(string? status) => status switch
    {
        nameof(FileStatus.PendingScan) => FileStatus.PendingScan,
        nameof(FileStatus.Scanning) => FileStatus.Scanning,
        nameof(FileStatus.Clean) => FileStatus.Clean,
        nameof(FileStatus.Infected) => FileStatus.Infected,
        nameof(FileStatus.ScanFailed) => FileStatus.ScanFailed,
        nameof(FileStatus.Deleted) => FileStatus.Deleted,
        _ => throw UnexpectedResponse("The API returned an unsupported file status.")
    };

    private string? RedactCredential(string? value) => string.IsNullOrEmpty(value)
        ? value
        : value.Replace(_apiKey, "[REDACTED]", StringComparison.Ordinal);

    private static FileSentryProtocolException UnexpectedResponse(
        string message = "The FileSentry API returned a malformed or unexpected response.") =>
        new(message);

    private sealed record FileUploadWireModel(
        Guid FileId,
        string? OriginalFileName,
        long SizeBytes,
        string? Sha256,
        string? DetectedMediaType,
        string? Status,
        DateTimeOffset CreatedAtUtc);

    private sealed record FileMetadataWireModel(
        Guid FileId,
        string? OriginalFileName,
        long SizeBytes,
        string? Sha256,
        string? DetectedMediaType,
        string? Status,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc);

    private sealed record ProblemDetailsWireModel(
        string? Type,
        string? Title,
        int? Status,
        string? Detail,
        string? Instance,
        string? Code);
}
