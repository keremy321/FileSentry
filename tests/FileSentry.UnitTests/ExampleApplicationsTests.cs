using FileSentry.AspNetExample;
using FileSentry.Client;
using FileSentry.ConsoleExample;

namespace FileSentry.UnitTests;

public sealed class ExampleApplicationsTests
{
    private const string ValidApiKey =
        "example-test-api-key-that-is-at-least-thirty-two-characters";
    private static readonly Guid FileId = Guid.Parse("da2f913a-7b6a-48a4-83f0-2f15047550fa");

    [Fact]
    public void ConsoleConfiguration_MissingApiKeyFailsClearly()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            ConsoleExampleConfiguration.Create("https://filesentry.test", apiKey: null));

        Assert.Contains(
            ConsoleExampleConfiguration.ApiKeyVariable,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AspNetConfiguration_InvalidKeyFailsWithoutEchoingCredential()
    {
        const string invalidKey = "invalid-short-key";

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            AspNetExampleConfiguration.Create(
                "https://filesentry.test",
                invalidKey,
                pollingIntervalSeconds: "1",
                scanTimeoutSeconds: "30"));

        Assert.Contains("configuration is invalid", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(invalidKey, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FileStatus.Infected)]
    [InlineData(FileStatus.ScanFailed)]
    [InlineData(FileStatus.Deleted)]
    public async Task AspNetForwarding_NonCleanResultNeverDownloads(FileStatus terminalStatus)
    {
        var fakeClient = new FakeFileSentryClient(terminalStatus);
        var service = new FileForwardingService(fakeClient);
        using var untrusted = new MemoryStream("untrusted"u8.ToArray());

        await using ForwardedFile result = await service.ForwardAsync(
            untrusted,
            "upload.pdf",
            default);

        Assert.Equal(terminalStatus, result.Metadata.Status);
        Assert.Null(result.CleanContent);
        Assert.Equal(0, fakeClient.DownloadCalls);
    }

    [Fact]
    public async Task AspNetForwarding_CleanResultReturnsDownloadStream()
    {
        var fakeClient = new FakeFileSentryClient(FileStatus.Clean);
        var service = new FileForwardingService(fakeClient);
        using var untrusted = new MemoryStream("untrusted"u8.ToArray());

        await using ForwardedFile result = await service.ForwardAsync(
            untrusted,
            "upload.pdf",
            default);

        Assert.Equal(FileStatus.Clean, result.Metadata.Status);
        Assert.NotNull(result.CleanContent);
        Assert.Equal(1, fakeClient.DownloadCalls);
    }

    private sealed class FakeFileSentryClient(FileStatus terminalStatus) : IFileSentryClient
    {
        public int DownloadCalls { get; private set; }

        public Task<FileUpload> UploadAsync(
            Stream content,
            string fileName,
            CancellationToken cancellationToken = default) => Task.FromResult(new FileUpload(
                FileId,
                fileName,
                content.Length,
                new string('a', 64),
                "application/pdf",
                FileStatus.PendingScan,
                DateTimeOffset.UtcNow));

        public Task<FileMetadata> GetFileAsync(
            Guid fileId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<FileMetadata>> ListFilesAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<FileMetadata> WaitForScanAsync(
            Guid fileId,
            CancellationToken cancellationToken = default)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Task.FromResult(new FileMetadata(
                fileId,
                "upload.pdf",
                9,
                new string('a', 64),
                "application/pdf",
                terminalStatus,
                now,
                now));
        }

        public Task<Stream> DownloadAsync(
            Guid fileId,
            CancellationToken cancellationToken = default)
        {
            DownloadCalls++;
            return Task.FromResult<Stream>(new MemoryStream("clean"u8.ToArray()));
        }

        public Task DeleteAsync(
            Guid fileId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
