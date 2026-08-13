using System.Net.Sockets;
using System.Text;
using FileSentry.Api.Infrastructure.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace FileSentry.Api.Infrastructure.Health;

public sealed class ClamAvHealthCheck(IOptions<ClamAvOptions> options) : IHealthCheck
{
    private const int MaximumResponseLength = 64;
    private static readonly byte[] PingCommand = Encoding.ASCII.GetBytes("zPING\0");

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        ClamAvOptions configuredOptions = options.Value;
        using var timeoutCancellationSource = new CancellationTokenSource(
            TimeSpan.FromSeconds(configuredOptions.TimeoutSeconds));
        using var linkedCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellationSource.Token);

        try
        {
            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(
                configuredOptions.Host,
                configuredOptions.Port,
                linkedCancellationSource.Token);

            await using NetworkStream stream = tcpClient.GetStream();
            await stream.WriteAsync(PingCommand, linkedCancellationSource.Token);
            await stream.FlushAsync(linkedCancellationSource.Token);

            byte[] responseBuffer = new byte[MaximumResponseLength];
            int responseLength = await ReadResponseAsync(
                stream,
                responseBuffer,
                linkedCancellationSource.Token);

            if (responseLength == 0)
            {
                return HealthCheckResult.Unhealthy("ClamAV returned an empty response.");
            }

            string response = Encoding.ASCII
                .GetString(responseBuffer, 0, responseLength)
                .TrimEnd('\0', '\r', '\n');

            return string.Equals(response, "PONG", StringComparison.Ordinal)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("ClamAV returned an unexpected response.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("ClamAV health check timed out.");
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            return HealthCheckResult.Unhealthy("ClamAV is unavailable.");
        }
    }

    private static async Task<int> ReadResponseAsync(
        NetworkStream stream,
        byte[] responseBuffer,
        CancellationToken cancellationToken)
    {
        int responseLength = 0;

        while (responseLength < responseBuffer.Length)
        {
            int bytesRead = await stream.ReadAsync(
                responseBuffer.AsMemory(responseLength),
                cancellationToken);

            if (bytesRead == 0)
            {
                break;
            }

            responseLength += bytesRead;
            if (HasResponseTerminator(responseBuffer, responseLength))
            {
                break;
            }
        }

        return responseLength;
    }

    private static bool HasResponseTerminator(byte[] responseBuffer, int responseLength)
    {
        for (int index = 0; index < responseLength; index++)
        {
            if (responseBuffer[index] is 0 or (byte)'\n')
            {
                return true;
            }
        }

        return false;
    }
}
