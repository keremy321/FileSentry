using FileSentry.Api.Infrastructure.Scanning;
using FileSentry.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FileSentry.IntegrationTests;

[Collection(AuthenticationApiCollection.Name)]
public sealed class ClientSdkIntegrationTests(AuthenticationApiFactory factory)
{
    [Fact]
    public async Task SdkUpload_WaitsForClean_DownloadsByteIdentically_AndDeletes()
    {
        using WebApplicationFactory<Program> workerFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ScannerWorker:Enabled"] = "true",
                    ["ScannerWorker:PollingIntervalMilliseconds"] = "100"
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IClamAvClient>();
                services.AddSingleton<IClamAvClient, AlwaysCleanClamAvClient>();
            });
        });
        using HttpClient httpClient = workerFactory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            });
        using var client = new FileSentryClient(httpClient, new FileSentryClientOptions
        {
            BaseAddress = httpClient.BaseAddress!,
            ApiKey = factory.ServiceApiKey,
            PollingInterval = TimeSpan.FromMilliseconds(100),
            ScanTimeout = TimeSpan.FromSeconds(10)
        });
        byte[] expected = TestFileContent.Pdf();
        Guid? fileId = null;

        try
        {
            using var uploadContent = new MemoryStream(expected);
            FileUpload upload = await client.UploadAsync(uploadContent, "sdk-clean.pdf");
            fileId = upload.FileId;
            Assert.Equal(FileStatus.PendingScan, upload.Status);

            FileMetadata scanned = await client.WaitForScanAsync(upload.FileId);
            Assert.Equal(FileStatus.Clean, scanned.Status);

            await using Stream download = await client.DownloadAsync(upload.FileId);
            using var downloaded = new MemoryStream();
            await download.CopyToAsync(downloaded);
            Assert.Equal(expected, downloaded.ToArray());

            await client.DeleteAsync(upload.FileId);
            fileId = null;
        }
        finally
        {
            if (fileId is Guid remainingFileId)
            {
                await client.DeleteAsync(remainingFileId);
            }
        }
    }

    private sealed class AlwaysCleanClamAvClient : IClamAvClient
    {
        public async Task<ClamAvScanResult> ScanAsync(
            Stream content,
            CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[4096];
            while (await content.ReadAsync(buffer, cancellationToken) > 0)
            {
            }

            return ClamAvScanResult.Clean() with
            {
                ScannerVersion = "ClamAV SDK integration fake"
            };
        }
    }
}
