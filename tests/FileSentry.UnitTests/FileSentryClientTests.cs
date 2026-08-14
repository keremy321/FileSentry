using System.Net;
using System.Text;
using System.Text.Json;
using FileSentry.Client;

namespace FileSentry.UnitTests;

public sealed class FileSentryClientTests
{
    private const string ApiKey = "sdk-test-api-key-that-is-at-least-thirty-two-characters";
    private static readonly Guid FileId = Guid.Parse("de49c091-29a1-4f27-b6da-782bb3a62fa1");

    [Fact]
    public async Task UploadAsync_SendsPerRequestApiKeyAndStreamsMultipartWithoutOwningInput()
    {
        byte[] expectedContent = "%PDF-1.4\nstreamed\n%%EOF"u8.ToArray();
        var uploadStream = new AsyncOnlyReadStream(expectedContent);
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "https://filesentry.test/root/api/v1/files",
                request.RequestUri?.AbsoluteUri);
            Assert.Equal(ApiKey, Assert.Single(request.Headers.GetValues("X-Api-Key")));
            Assert.Equal(0, uploadStream.BytesRead);
            Assert.StartsWith(
                "multipart/form-data",
                request.Content?.Headers.ContentType?.MediaType,
                StringComparison.Ordinal);
            var multipartContent = Assert.IsType<MultipartFormDataContent>(request.Content);
            HttpContent filePart = Assert.Single(multipartContent);
            Assert.Equal("file", filePart.Headers.ContentDisposition?.Name);
            Assert.Equal("resume.pdf", filePart.Headers.ContentDisposition?.FileName);
            byte[] multipart = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            Assert.True(multipart.AsSpan().IndexOf(expectedContent) >= 0);
            return JsonResponse(
                HttpStatusCode.Accepted,
                UploadJson(FileStatus.PendingScan, expectedContent.Length));
        });
        using HttpClient httpClient = new(handler);
        using var client = CreateClient(httpClient);

        FileUpload upload = await client.UploadAsync(uploadStream, "resume.pdf");

        Assert.Equal(FileId, upload.FileId);
        Assert.Equal("resume.pdf", upload.OriginalFileName);
        Assert.Equal(expectedContent.Length, upload.SizeBytes);
        Assert.Equal(FileStatus.PendingScan, upload.Status);
        Assert.True(uploadStream.BytesRead > 0);
        Assert.False(uploadStream.IsDisposed);
        Assert.False(httpClient.DefaultRequestHeaders.Contains("X-Api-Key"));
    }

    [Fact]
    public async Task MetadataAndList_UseTypedContractModels()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            JsonResponse(HttpStatusCode.OK, MetadataJson(FileStatus.Scanning)),
            JsonResponse(
                HttpStatusCode.OK,
                $"[{MetadataJson(FileStatus.Clean)}]")
        ]);
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(ApiKey, Assert.Single(request.Headers.GetValues("X-Api-Key")));
            return Task.FromResult(responses.Dequeue());
        });
        using HttpClient httpClient = new(handler);
        using var client = CreateClient(httpClient);

        FileMetadata metadata = await client.GetFileAsync(FileId);
        IReadOnlyList<FileMetadata> files = await client.ListFilesAsync();

        Assert.Equal(FileStatus.Scanning, metadata.Status);
        FileMetadata listed = Assert.Single(files);
        Assert.Equal(FileId, listed.FileId);
        Assert.Equal(FileStatus.Clean, listed.Status);
        Assert.Equal("application/pdf", listed.DetectedMediaType);
    }

    [Fact]
    public async Task DownloadAsync_ReturnsLazyResponseOwnedStream()
    {
        byte[] expected = "clean download bytes"u8.ToArray();
        var responseStream = new TrackingReadStream(expected);
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.EndsWith($"/{FileId:D}/download", request.RequestUri?.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(responseStream)
            });
        });
        using HttpClient httpClient = new(handler);
        using var client = CreateClient(httpClient);

        Stream download = await client.DownloadAsync(FileId);
        Assert.Equal(0, responseStream.BytesRead);
        using var output = new MemoryStream();
        await download.CopyToAsync(output);
        Assert.Equal(expected, output.ToArray());
        Assert.False(responseStream.IsDisposed);

        await download.DisposeAsync();

        Assert.True(responseStream.IsDisposed);
    }

    [Fact]
    public async Task DeleteAsync_SendsDeleteAndRequiresNoContent()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.EndsWith($"/{FileId:D}", request.RequestUri?.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        using HttpClient httpClient = new(handler);
        using var client = CreateClient(httpClient);

        await client.DeleteAsync(FileId);
    }

    [Fact]
    public async Task Dispose_WithInjectedHttpClient_DoesNotDisposeCallerOwnedClient()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)));
        using HttpClient httpClient = new(handler);
        var client = CreateClient(httpClient);

        client.Dispose();
        using HttpResponseMessage response = await httpClient.GetAsync(
            "https://filesentry.test/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WaitForScanAsync_PollsActiveStatesUntilClean()
    {
        var statuses = new Queue<FileStatus>(
            [FileStatus.PendingScan, FileStatus.Scanning, FileStatus.Clean]);
        int requests = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(JsonResponse(
                HttpStatusCode.OK,
                MetadataJson(statuses.Dequeue())));
        });
        using HttpClient httpClient = new(handler);
        using var client = CreateClient(
            httpClient,
            pollingInterval: TimeSpan.FromMilliseconds(1));

        FileMetadata result = await client.WaitForScanAsync(FileId);

        Assert.Equal(FileStatus.Clean, result.Status);
        Assert.Equal(3, requests);
    }

    [Theory]
    [InlineData(FileStatus.Infected)]
    [InlineData(FileStatus.ScanFailed)]
    public async Task WaitForScanAsync_StopsAtNonCleanTerminalState(FileStatus terminalStatus)
    {
        int requests = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(JsonResponse(
                HttpStatusCode.OK,
                MetadataJson(terminalStatus)));
        });
        using HttpClient httpClient = new(handler);
        using var client = CreateClient(httpClient);

        FileMetadata result = await client.WaitForScanAsync(FileId);

        Assert.Equal(terminalStatus, result.Status);
        Assert.NotEqual(FileStatus.Clean, result.Status);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task WaitForScanAsync_CallerCancellationStopsPolling()
    {
        var responseDisposed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            JsonResponse(
                HttpStatusCode.OK,
                MetadataJson(FileStatus.PendingScan),
                () => responseDisposed.TrySetResult())));
        using HttpClient httpClient = new(handler);
        using var client = CreateClient(
            httpClient,
            pollingInterval: TimeSpan.FromMinutes(1),
            scanTimeout: TimeSpan.FromMinutes(2));
        using var cancellationSource = new CancellationTokenSource();

        Task<FileMetadata> wait = client.WaitForScanAsync(
            FileId,
            cancellationSource.Token);
        await responseDisposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellationSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public async Task WaitForScanAsync_ConfiguredTimeoutThrowsTypedException()
    {
        var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The timeout did not cancel the request.");
        });
        using HttpClient httpClient = new(handler);
        using var client = CreateClient(
            httpClient,
            pollingInterval: TimeSpan.FromMilliseconds(10),
            scanTimeout: TimeSpan.FromMilliseconds(50));

        FileSentryPollingTimeoutException exception =
            await Assert.ThrowsAsync<FileSentryPollingTimeoutException>(
                () => client.WaitForScanAsync(FileId));

        Assert.Equal(FileId, exception.FileId);
        Assert.Equal(TimeSpan.FromMilliseconds(50), exception.Timeout);
    }

    [Fact]
    public async Task ProblemDetails_MapsToTypedRedactedApiException()
    {
        string problemJson = JsonSerializer.Serialize(new
        {
            type = "https://errors.example/file-not-clean",
            title = "File not clean",
            status = 409,
            detail = $"The credential {ApiKey} must not be reflected.",
            instance = $"/api/v1/files/{FileId:D}/download",
            code = "FILE_NOT_CLEAN"
        });
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            JsonResponse(HttpStatusCode.Conflict, problemJson)));
        using HttpClient httpClient = new(handler);
        using var client = CreateClient(httpClient);

        FileSentryApiException exception = await Assert.ThrowsAsync<FileSentryApiException>(
            () => client.DownloadAsync(FileId));

        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
        Assert.Equal(409, exception.ProblemStatus);
        Assert.Equal("FILE_NOT_CLEAN", exception.Code);
        Assert.Equal("File not clean", exception.Title);
        Assert.Contains("[REDACTED]", exception.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, exception.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("unknown-status")]
    [InlineData("missing-file-id")]
    public async Task MalformedOrUnexpectedSuccessResponse_FailsSafely(string scenario)
    {
        string responseBody = scenario switch
        {
            "not-json" => "{not-json",
            "unknown-status" => MetadataJson(status: null, statusText: "FutureState"),
            "missing-file-id" => MetadataJson(FileStatus.Clean).Replace(
                FileId.ToString(),
                Guid.Empty.ToString(),
                StringComparison.Ordinal),
            _ => throw new InvalidOperationException()
        };
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            JsonResponse(HttpStatusCode.OK, responseBody)));
        using HttpClient httpClient = new(handler);
        using var client = CreateClient(httpClient);

        FileSentryProtocolException exception =
            await Assert.ThrowsAsync<FileSentryProtocolException>(
                () => client.GetFileAsync(FileId));

        Assert.DoesNotContain(ApiKey, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("key-with-a-line-break-that-is-long-enough\nunsafe")]
    public void InvalidApiKey_IsRejectedWithoutIncludingItInException(string apiKey)
    {
        var options = CreateOptions(apiKey: apiKey);

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new FileSentryClient(options));

        Assert.DoesNotContain(apiKey, exception.ToString(), StringComparison.Ordinal);
    }

    private static FileSentryClient CreateClient(
        HttpClient httpClient,
        TimeSpan? pollingInterval = null,
        TimeSpan? scanTimeout = null) =>
        new(httpClient, CreateOptions(pollingInterval, scanTimeout));

    private static FileSentryClientOptions CreateOptions(
        TimeSpan? pollingInterval = null,
        TimeSpan? scanTimeout = null,
        string apiKey = ApiKey) => new()
        {
            BaseAddress = new Uri("https://filesentry.test/root"),
            ApiKey = apiKey,
            PollingInterval = pollingInterval ?? TimeSpan.FromMilliseconds(10),
            ScanTimeout = scanTimeout ?? TimeSpan.FromSeconds(5)
        };

    private static HttpResponseMessage JsonResponse(
        HttpStatusCode statusCode,
        string json,
        Action? onDispose = null) => new(statusCode)
        {
            Content = onDispose is null
                ? new StringContent(json, Encoding.UTF8, "application/json")
                : new CallbackStringContent(json, onDispose)
        };

    private static string UploadJson(FileStatus status, int sizeBytes) => JsonSerializer.Serialize(new
    {
        fileId = FileId,
        originalFileName = "resume.pdf",
        sizeBytes,
        sha256 = new string('a', 64),
        detectedMediaType = "application/pdf",
        status = status.ToString(),
        createdAtUtc = DateTimeOffset.Parse("2026-08-14T10:00:00Z")
    });

    private static string MetadataJson(
        FileStatus? status,
        string? statusText = null) => JsonSerializer.Serialize(new
        {
            fileId = FileId,
            originalFileName = "resume.pdf",
            sizeBytes = 24,
            sha256 = new string('a', 64),
            detectedMediaType = "application/pdf",
            status = statusText ?? status?.ToString(),
            createdAtUtc = DateTimeOffset.Parse("2026-08-14T10:00:00Z"),
            updatedAtUtc = DateTimeOffset.Parse("2026-08-14T10:00:01Z")
        });

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class AsyncOnlyReadStream(byte[] content) : Stream
    {
        private int _position;

        public int BytesRead { get; private set; }

        public bool IsDisposed { get; private set; }

        public override bool CanRead => !IsDisposed;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => content.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("The SDK attempted a synchronous upload read.");

        public override int Read(Span<byte> buffer) =>
            throw new InvalidOperationException("The SDK attempted a synchronous upload read.");

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = Math.Min(buffer.Length, content.Length - _position);
            content.AsMemory(_position, read).CopyTo(buffer);
            _position += read;
            BytesRead += read;
            return ValueTask.FromResult(read);
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = Math.Min(count, content.Length - _position);
            content.AsSpan(_position, read).CopyTo(buffer.AsSpan(offset, read));
            _position += read;
            BytesRead += read;
            return Task.FromResult(read);
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class TrackingReadStream(byte[] content) : MemoryStream(content)
    {
        public int BytesRead { get; private set; }

        public bool IsDisposed { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = base.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int read = base.Read(buffer);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int read = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class CallbackStringContent(string content, Action onDispose)
        : StringContent(content, Encoding.UTF8, "application/json")
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                onDispose();
            }
        }
    }
}
