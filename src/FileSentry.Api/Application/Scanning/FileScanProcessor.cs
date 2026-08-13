using FileSentry.Api.Domain.Scanning;
using FileSentry.Api.Infrastructure.Options;
using FileSentry.Api.Infrastructure.Scanning;
using FileSentry.Api.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace FileSentry.Api.Application.Scanning;

public sealed class FileScanProcessor(
    FileScanWorkflowService workflow,
    IClamAvClient clamAvClient,
    StoragePathProvider storagePaths,
    IOptions<ClamAvOptions> clamAvOptions,
    ILogger<FileScanProcessor> logger)
{
    public Task<int> PrepareDurableWorkAsync(CancellationToken cancellationToken) =>
        workflow.PrepareDurableWorkAsync(cancellationToken);

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        FileScanJob? job = await workflow.TryClaimNextAsync(cancellationToken);
        if (job is null)
        {
            return false;
        }

        ClamAvScanResult result;
        try
        {
            string quarantinePath = storagePaths.GetQuarantinePath(job.StorageName);
            await using var content = new FileStream(
                quarantinePath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    BufferSize = clamAvOptions.Value.StreamChunkSizeBytes,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
            if (content.Length != job.ExpectedSizeBytes)
            {
                result = ClamAvScanResult.Failed(
                    ScanFailureCode.StorageError,
                    isRetryable: false);
            }
            else
            {
                result = await clamAvClient.ScanAsync(content, cancellationToken);
                if (result.Result is not (
                        ScanAttemptResult.Clean
                        or ScanAttemptResult.Infected
                        or ScanAttemptResult.Failed))
                {
                    result = ClamAvScanResult.Failed(
                        ScanFailureCode.Unknown,
                        isRetryable: true);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            result = ClamAvScanResult.Failed(
                ScanFailureCode.FileMissing,
                isRetryable: false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogError(
                exception,
                "Quarantined content could not be opened for file record {FileRecordId}.",
                job.FileRecordId);
            result = ClamAvScanResult.Failed(
                ScanFailureCode.StorageError,
                isRetryable: true);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Unexpected scanner failure for file record {FileRecordId}.",
                job.FileRecordId);
            result = ClamAvScanResult.Failed(
                ScanFailureCode.Unknown,
                isRetryable: true);
        }

        await workflow.CompleteAndFinalizeAsync(job, result, cancellationToken);
        return true;
    }
}
