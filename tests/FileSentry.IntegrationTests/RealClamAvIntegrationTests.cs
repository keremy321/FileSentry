using System.Text;
using FileSentry.Api.Domain.Scanning;
using FileSentry.Api.Infrastructure.Options;
using FileSentry.Api.Infrastructure.Scanning;
using Microsoft.Extensions.Options;

namespace FileSentry.IntegrationTests;

[Collection(ClamAvContainerCollection.Name)]
public sealed class RealClamAvIntegrationTests(ClamAvContainerFixture fixture)
{
    [Fact]
    public async Task InStream_RealDaemon_ClassifiesCleanAndAssembledEicar()
    {
        var client = new ClamAvClient(Options.Create(new ClamAvOptions
        {
            Host = fixture.Host,
            Port = fixture.Port,
            TimeoutSeconds = 10,
            ScanTimeoutSeconds = 60,
            MaximumStreamSizeBytes = 10 * 1024 * 1024,
            StreamChunkSizeBytes = 64 * 1024
        }));

        ClamAvScanResult clean = await client.ScanAsync(
            new MemoryStream("FileSentry clean integration content"u8.ToArray()),
            default);
        ClamAvScanResult infected = await client.ScanAsync(
            new MemoryStream(AssembleEicarBytes()),
            default);

        Assert.Equal(ScanAttemptResult.Clean, clean.Result);
        Assert.NotNull(clean.ScannerVersion);
        Assert.Equal(ScanAttemptResult.Infected, infected.Result);
        Assert.Contains("Eicar", infected.DetectionName, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(infected.ScannerVersion);
    }

    private static byte[] AssembleEicarBytes()
    {
        string value = string.Concat(
            "X5O!P%@AP[4\\PZX54(P^)7CC)7}$",
            "EICAR-STANDARD-ANTIVIRUS-TEST-FILE",
            "!$H+H*");
        return Encoding.ASCII.GetBytes(value);
    }
}
