using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using FileSentry.Api.Domain.Scanning;
using FileSentry.Api.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace FileSentry.Api.Infrastructure.Scanning;

public sealed class ClamAvClient(IOptions<ClamAvOptions> options) : IClamAvClient
{
    private const int MaximumResponseLength = 1024;
    private const int MaximumVersionLength = 256;

    private static readonly byte[] InStreamCommand = Encoding.ASCII.GetBytes("zINSTREAM\0");
    private static readonly byte[] VersionCommand = Encoding.ASCII.GetBytes("zVERSION\0");

    public async Task<ClamAvScanResult> ScanAsync(
        Stream content,
        CancellationToken cancellationToken)
    {
        ClamAvScanResult result = await ScanCoreAsync(content, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (result.Result == ScanAttemptResult.Failed)
        {
            return result;
        }

        string? scannerVersion = await TryGetVersionAsync(cancellationToken);
        return result with { ScannerVersion = scannerVersion };
    }

    private async Task<ClamAvScanResult> ScanCoreAsync(
        Stream content,
        CancellationToken cancellationToken)
    {
        ClamAvOptions configuredOptions = options.Value;
        using var timeoutSource = new CancellationTokenSource(
            TimeSpan.FromSeconds(configuredOptions.ScanTimeoutSeconds));
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        byte[] contentBuffer = ArrayPool<byte>.Shared.Rent(
            configuredOptions.StreamChunkSizeBytes);

        try
        {
            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(
                configuredOptions.Host,
                configuredOptions.Port,
                linkedSource.Token);
            await using NetworkStream networkStream = tcpClient.GetStream();
            await networkStream.WriteAsync(InStreamCommand, linkedSource.Token);

            long totalBytes = 0;
            while (true)
            {
                int bytesRead;
                try
                {
                    bytesRead = await content.ReadAsync(
                        contentBuffer.AsMemory(0, configuredOptions.StreamChunkSizeBytes),
                        linkedSource.Token);
                }
                catch (IOException)
                {
                    return ClamAvScanResult.Failed(
                        ScanFailureCode.StorageError,
                        isRetryable: true);
                }

                if (bytesRead == 0)
                {
                    break;
                }

                if (totalBytes > configuredOptions.MaximumStreamSizeBytes - bytesRead)
                {
                    return ClamAvScanResult.Failed(
                        ScanFailureCode.ScannerSizeLimit,
                        isRetryable: false);
                }

                byte[] lengthPrefix = new byte[sizeof(uint)];
                BinaryPrimitives.WriteUInt32BigEndian(lengthPrefix, (uint)bytesRead);
                await networkStream.WriteAsync(lengthPrefix, linkedSource.Token);
                await networkStream.WriteAsync(
                    contentBuffer.AsMemory(0, bytesRead),
                    linkedSource.Token);
                totalBytes += bytesRead;
            }

            await networkStream.WriteAsync(new byte[sizeof(uint)], linkedSource.Token);
            await networkStream.FlushAsync(linkedSource.Token);
            string? response = await ReadTerminatedAsciiResponseAsync(
                networkStream,
                MaximumResponseLength,
                linkedSource.Token);
            return ParseResponse(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ClamAvScanResult.Failed(ScanFailureCode.Timeout, isRetryable: true);
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            return ClamAvScanResult.Failed(
                ScanFailureCode.ScannerUnavailable,
                isRetryable: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(contentBuffer, clearArray: true);
        }
    }

    private async Task<string?> TryGetVersionAsync(CancellationToken cancellationToken)
    {
        ClamAvOptions configuredOptions = options.Value;
        using var timeoutSource = new CancellationTokenSource(
            TimeSpan.FromSeconds(configuredOptions.TimeoutSeconds));
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(
                configuredOptions.Host,
                configuredOptions.Port,
                linkedSource.Token);
            await using NetworkStream stream = tcpClient.GetStream();
            await stream.WriteAsync(VersionCommand, linkedSource.Token);
            await stream.FlushAsync(linkedSource.Token);
            string? response = await ReadTerminatedAsciiResponseAsync(
                stream,
                MaximumVersionLength,
                linkedSource.Token);
            return string.IsNullOrWhiteSpace(response)
                ? null
                : SanitizeMetadata(response, MaximumVersionLength);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or SocketException or IOException)
        {
            return null;
        }
    }

    private static ClamAvScanResult ParseResponse(string? response)
    {
        if (string.Equals(response, "stream: OK", StringComparison.Ordinal))
        {
            return ClamAvScanResult.Clean();
        }

        const string prefix = "stream: ";
        const string foundSuffix = " FOUND";
        if (response is not null
            && response.StartsWith(prefix, StringComparison.Ordinal)
            && response.EndsWith(foundSuffix, StringComparison.Ordinal))
        {
            string detectionName = response[
                prefix.Length..^foundSuffix.Length];
            if (!string.IsNullOrWhiteSpace(detectionName))
            {
                return ClamAvScanResult.Infected(
                    SanitizeMetadata(detectionName, MaximumVersionLength));
            }
        }

        if (response?.EndsWith(" ERROR", StringComparison.Ordinal) == true)
        {
            bool sizeLimit = response.Contains(
                "size limit exceeded",
                StringComparison.OrdinalIgnoreCase);
            return ClamAvScanResult.Failed(
                sizeLimit ? ScanFailureCode.ScannerSizeLimit : ScanFailureCode.ScannerError,
                isRetryable: !sizeLimit);
        }

        return ClamAvScanResult.Failed(
            ScanFailureCode.MalformedResponse,
            isRetryable: true);
    }

    private static async Task<string?> ReadTerminatedAsciiResponseAsync(
        NetworkStream stream,
        int maximumLength,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[maximumLength];
        int length = 0;

        while (length < buffer.Length)
        {
            int bytesRead = await stream.ReadAsync(
                buffer.AsMemory(length),
                cancellationToken);
            if (bytesRead == 0)
            {
                return null;
            }

            int terminatorOffset = buffer
                .AsSpan(length, bytesRead)
                .IndexOfAny((byte)0, (byte)'\n');
            if (terminatorOffset >= 0)
            {
                int responseLength = length + terminatorOffset;
                ReadOnlySpan<byte> responseBytes = buffer.AsSpan(0, responseLength);
                if (responseBytes.ContainsAnyExceptInRange((byte)0x20, (byte)0x7E))
                {
                    return null;
                }

                return Encoding.ASCII.GetString(responseBytes).TrimEnd('\r');
            }

            length += bytesRead;
        }

        return null;
    }

    private static string SanitizeMetadata(string value, int maximumLength)
    {
        return new string(value
            .Select(character => char.IsControl(character) ? '_' : character)
            .Take(maximumLength)
            .ToArray());
    }
}
