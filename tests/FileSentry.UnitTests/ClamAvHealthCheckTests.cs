using System.Net;
using System.Net.Sockets;
using System.Text;
using FileSentry.Api.Infrastructure.Health;
using FileSentry.Api.Infrastructure.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace FileSentry.UnitTests;

public sealed class ClamAvHealthCheckTests
{
    private static readonly byte[] ExpectedPingCommand = Encoding.ASCII.GetBytes("zPING\0");

    [Fact]
    public async Task CheckHealthAsync_ReturnsHealthyForExactPong()
    {
        HealthCheckResult result = await RunWithServerAsync(async stream =>
        {
            await stream.WriteAsync("PO"u8.ToArray());
            await stream.FlushAsync();
            await Task.Delay(10);
            await stream.WriteAsync("NG\0"u8.ToArray());
        });

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("PONG extra\0")]
    [InlineData("UNKNOWN\0")]
    [InlineData("pong\0")]
    public async Task CheckHealthAsync_ReturnsUnhealthyForInconclusiveResponse(string response)
    {
        HealthCheckResult result = await RunWithServerAsync(async stream =>
        {
            if (response.Length > 0)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            }
        });

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_ReturnsUnhealthyWhenConnectionFails()
    {
        int unusedPort;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        unusedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var healthCheck = CreateHealthCheck(unusedPort);

        HealthCheckResult result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_ReturnsUnhealthyOnInternalTimeout()
    {
        HealthCheckResult result = await RunWithServerAsync(
            stream => Task.Delay(Timeout.InfiniteTimeSpan),
            timeoutSeconds: 1);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("ClamAV health check timed out.", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_PropagatesCallerCancellation()
    {
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunWithServerAsync(
            stream => Task.Delay(Timeout.InfiniteTimeSpan),
            cancellationToken: cancellationSource.Token));
    }

    private static async Task<HealthCheckResult> RunWithServerAsync(
        Func<NetworkStream, Task> respondAsync,
        int timeoutSeconds = 5,
        CancellationToken cancellationToken = default)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var serverCancellationSource = new CancellationTokenSource();
        Task serverTask = RunServerAsync(listener, respondAsync, serverCancellationSource.Token);

        try
        {
            var healthCheck = CreateHealthCheck(port, timeoutSeconds);
            return await healthCheck.CheckHealthAsync(
                new HealthCheckContext(),
                cancellationToken);
        }
        finally
        {
            await serverCancellationSource.CancelAsync();
            listener.Stop();

            try
            {
                await serverTask;
            }
            catch (OperationCanceledException) when (serverCancellationSource.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task RunServerAsync(
        TcpListener listener,
        Func<NetworkStream, Task> respondAsync,
        CancellationToken cancellationToken)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using NetworkStream stream = client.GetStream();
        byte[] command = new byte[ExpectedPingCommand.Length];
        await stream.ReadExactlyAsync(command, cancellationToken);
        Assert.Equal(ExpectedPingCommand, command);
        await respondAsync(stream).WaitAsync(cancellationToken);
    }

    private static ClamAvHealthCheck CreateHealthCheck(int port, int timeoutSeconds = 5)
    {
        IOptions<ClamAvOptions> options = Options.Create(new ClamAvOptions
        {
            Port = port,
            TimeoutSeconds = timeoutSeconds
        });

        return new ClamAvHealthCheck(options);
    }
}
