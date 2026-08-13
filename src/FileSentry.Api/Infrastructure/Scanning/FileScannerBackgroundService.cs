using FileSentry.Api.Application.Scanning;
using FileSentry.Api.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace FileSentry.Api.Infrastructure.Scanning;

public sealed class FileScannerBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<ScannerWorkerOptions> options,
    TimeProvider timeProvider,
    ILogger<FileScannerBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ScannerWorkerOptions configuredOptions = options.Value;
        if (!configuredOptions.Enabled)
        {
            logger.LogInformation("The file scanner worker is disabled by configuration.");
            return;
        }

        logger.LogInformation("The durable file scanner worker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<FileScanProcessor>();
                int prepared = await processor.PrepareDurableWorkAsync(stoppingToken);
                bool processed = await processor.ProcessNextAsync(stoppingToken);
                if (!processed && prepared == 0)
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(configuredOptions.PollingIntervalMilliseconds),
                        timeProvider,
                        stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The durable file scanner worker iteration failed.");
                await Task.Delay(
                    TimeSpan.FromMilliseconds(configuredOptions.PollingIntervalMilliseconds),
                    timeProvider,
                    stoppingToken);
            }
        }

        logger.LogInformation("The durable file scanner worker stopped.");
    }
}
