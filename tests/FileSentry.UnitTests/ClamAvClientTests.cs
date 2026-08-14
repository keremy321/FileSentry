using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FileSentry.Api.Domain.Scanning;
using FileSentry.Api.Infrastructure.Options;
using FileSentry.Api.Infrastructure.Scanning;
using Microsoft.Extensions.Options;

namespace FileSentry.UnitTests;

public sealed class ClamAvClientTests
{
    private static readonly byte[] InStreamCommand = Encoding.ASCII.GetBytes("zINSTREAM\0");
    private static readonly byte[] VersionCommand = Encoding.ASCII.GetBytes("zVERSION\0");

    [Fact]
    public async Task ScanAsync_ExactCleanResponse_ReturnsCleanWithVersionAndStreamsBytes()
    {
        byte[] expectedContent = Enumerable.Range(0, 4096)
            .Select(index => (byte)(index % 251))
            .ToArray();
        byte[]? receivedContent = null;

        ClamAvScanResult result = await RunWithServerAsync(
            expectedContent,
            "stream: OK\0",
            bytes => receivedContent = bytes,
            "ClamAV 1.4.3/test\0");

        Assert.Equal(ScanAttemptResult.Clean, result.Result);
        Assert.Null(result.FailureCode);
        Assert.Equal("ClamAV 1.4.3/test", result.ScannerVersion);
        Assert.Equal(expectedContent, receivedContent);
    }

    [Fact]
    public async Task ScanAsync_FoundResponse_ReturnsInfectedWithDetectionName()
    {
        ClamAvScanResult result = await RunWithServerAsync(
            "assembled test content"u8.ToArray(),
            "stream: Eicar-Signature FOUND\0",
            onContent: null,
            "ClamAV 1.4.3/test\0");

        Assert.Equal(ScanAttemptResult.Infected, result.Result);
        Assert.Equal("Eicar-Signature", result.DetectionName);
        Assert.Equal("ClamAV 1.4.3/test", result.ScannerVersion);
    }

    [Theory]
    [InlineData("stream: MAYBE\0")]
    [InlineData("stream: OK extra\0")]
    [InlineData("stream:  FOUND\0")]
    [InlineData("garbage\0")]
    [InlineData("stream: OK")]
    public async Task ScanAsync_UnknownResponse_FailsClosed(string response)
    {
        ClamAvScanResult result = await RunWithServerAsync(
            "content"u8.ToArray(),
            response);

        Assert.Equal(ScanAttemptResult.Failed, result.Result);
        Assert.Equal(ScanFailureCode.MalformedResponse, result.FailureCode);
        Assert.True(result.IsRetryable);
    }

    [Theory]
    [InlineData("stream: INSTREAM size limit exceeded. ERROR\0", ScanFailureCode.ScannerSizeLimit, false)]
    [InlineData("stream: Temporary scanner problem ERROR\0", ScanFailureCode.ScannerError, true)]
    public async Task ScanAsync_ScannerError_IsNeverClean(
        string response,
        ScanFailureCode expectedFailure,
        bool expectedRetryable)
    {
        ClamAvScanResult result = await RunWithServerAsync(
            "content"u8.ToArray(),
            response);

        Assert.Equal(ScanAttemptResult.Failed, result.Result);
        Assert.Equal(expectedFailure, result.FailureCode);
        Assert.Equal(expectedRetryable, result.IsRetryable);
    }

    [Fact]
    public async Task ScanAsync_ResponseTimeout_ReturnsRetryableFailure()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task serverTask = Task.Run(async () =>
        {
            using TcpClient serverClient = await listener.AcceptTcpClientAsync();
            await using NetworkStream stream = serverClient.GetStream();
            await ReadInStreamContentAsync(stream);
            await Task.Delay(TimeSpan.FromSeconds(2));
        });
        ClamAvOptions timeoutOptions = CreateOptions(port);
        timeoutOptions.ScanTimeoutSeconds = 1;
        var client = new ClamAvClient(Options.Create(timeoutOptions));

        ClamAvScanResult result = await client.ScanAsync(
            new MemoryStream("content"u8.ToArray()),
            default);

        Assert.Equal(ScanAttemptResult.Failed, result.Result);
        Assert.Equal(ScanFailureCode.Timeout, result.FailureCode);
        Assert.True(result.IsRetryable);
        listener.Stop();
        await serverTask;
    }

    [Fact]
    public async Task ScanAsync_CallerCancellation_PropagatesCancellation()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requestReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task serverTask = Task.Run(async () =>
        {
            using TcpClient serverClient = await listener.AcceptTcpClientAsync();
            await using NetworkStream stream = serverClient.GetStream();
            await ReadInStreamContentAsync(stream);
            requestReceived.SetResult();
            byte[] buffer = new byte[1];
            _ = await stream.ReadAsync(buffer);
        });
        var client = new ClamAvClient(Options.Create(CreateOptions(port)));
        using var cancellationSource = new CancellationTokenSource();

        Task<ClamAvScanResult> scanTask = client.ScanAsync(
            new MemoryStream("content"u8.ToArray()),
            cancellationSource.Token);
        await requestReceived.Task;
        await cancellationSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanTask);
        listener.Stop();
        await serverTask;
    }

    [Fact]
    public async Task ScanAsync_UnavailableDaemon_ReturnsRetryableFailure()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int unusedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var client = new ClamAvClient(Options.Create(CreateOptions(unusedPort)));

        ClamAvScanResult result = await client.ScanAsync(
            new MemoryStream("content"u8.ToArray()),
            default);

        Assert.Equal(ScanAttemptResult.Failed, result.Result);
        Assert.Equal(ScanFailureCode.ScannerUnavailable, result.FailureCode);
        Assert.True(result.IsRetryable);
    }

    [Fact]
    public async Task ScanAsync_LocalStreamLimitExceeded_FailsWithoutCleanClassification()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task serverTask = Task.Run(async () =>
        {
            using TcpClient serverClient = await listener.AcceptTcpClientAsync();
            await using NetworkStream stream = serverClient.GetStream();
            byte[] buffer = new byte[64];
            while (await stream.ReadAsync(buffer) > 0)
            {
            }
        });
        ClamAvOptions configuredOptions = CreateOptions(port);
        configuredOptions.MaximumStreamSizeBytes = 4;
        configuredOptions.StreamChunkSizeBytes = 4;
        var client = new ClamAvClient(Options.Create(configuredOptions));

        ClamAvScanResult result = await client.ScanAsync(
            new MemoryStream("12345"u8.ToArray()),
            default);

        Assert.Equal(ScanAttemptResult.Failed, result.Result);
        Assert.Equal(ScanFailureCode.ScannerSizeLimit, result.FailureCode);
        Assert.False(result.IsRetryable);
        listener.Stop();
        await serverTask;
    }

    private static async Task<ClamAvScanResult> RunWithServerAsync(
        byte[] content,
        string scanResponse,
        Action<byte[]>? onContent = null,
        string? versionResponse = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task serverTask = RunServerAsync();

        async Task RunServerAsync()
        {
            Task<TcpClient>? versionAcceptTask = null;
            using (TcpClient scanClient = await listener.AcceptTcpClientAsync())
            await using (NetworkStream stream = scanClient.GetStream())
            {
                byte[] received = await ReadInStreamContentAsync(stream);
                onContent?.Invoke(received);
                if (versionResponse is not null)
                {
                    // The client opens VERSION immediately after reading the scan response.
                    versionAcceptTask = listener.AcceptTcpClientAsync();
                }

                await stream.WriteAsync(Encoding.ASCII.GetBytes(scanResponse));
            }

            if (versionResponse is not null && versionAcceptTask is not null)
            {
                using TcpClient versionClient = await versionAcceptTask;
                await using NetworkStream stream = versionClient.GetStream();
                byte[] command = new byte[VersionCommand.Length];
                await stream.ReadExactlyAsync(command);
                Assert.Equal(VersionCommand, command);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(versionResponse));
            }
        }

        var client = new ClamAvClient(Options.Create(CreateOptions(port)));

        ClamAvScanResult result = await client.ScanAsync(
            new MemoryStream(content),
            default);

        listener.Stop();
        await serverTask;
        return result;
    }

    private static async Task<byte[]> ReadInStreamContentAsync(NetworkStream stream)
    {
        byte[] command = new byte[InStreamCommand.Length];
        await stream.ReadExactlyAsync(command);
        Assert.Equal(InStreamCommand, command);
        using var content = new MemoryStream();
        byte[] lengthPrefix = new byte[sizeof(uint)];

        while (true)
        {
            await stream.ReadExactlyAsync(lengthPrefix);
            uint chunkLength = BinaryPrimitives.ReadUInt32BigEndian(lengthPrefix);
            if (chunkLength == 0)
            {
                return content.ToArray();
            }

            byte[] chunk = new byte[checked((int)chunkLength)];
            await stream.ReadExactlyAsync(chunk);
            await content.WriteAsync(chunk);
        }
    }

    private static ClamAvOptions CreateOptions(int port) => new()
    {
        Host = IPAddress.Loopback.ToString(),
        Port = port,
        TimeoutSeconds = 1,
        ScanTimeoutSeconds = 5,
        MaximumStreamSizeBytes = 10 * 1024 * 1024,
        StreamChunkSizeBytes = 1024
    };
}
