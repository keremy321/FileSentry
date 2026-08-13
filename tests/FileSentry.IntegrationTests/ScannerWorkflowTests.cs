using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using FileSentry.Api.Application.Scanning;
using FileSentry.Api.Domain.Files;
using FileSentry.Api.Domain.Scanning;
using FileSentry.Api.Infrastructure.Options;
using FileSentry.Api.Infrastructure.Scanning;
using FileSentry.Api.Infrastructure.Storage;
using FileSentry.Api.Persistence;
using FileSentry.Api.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FileSentry.IntegrationTests;

[Collection(AuthenticationApiCollection.Name)]
public sealed class ScannerWorkflowTests(AuthenticationApiFactory factory)
{
    [Fact]
    public async Task ProcessNext_CleanResult_PersistsAttemptAndPromotesFile()
    {
        byte[] content = "known clean content"u8.ToArray();
        Guid fileRecordId = await CreatePendingFileAsync(content);
        var scanner = new QueueClamAvClient(ClamAvScanResult.Clean() with
        {
            ScannerVersion = "ClamAV test-version"
        });
        using ProcessorHarness harness = CreateHarness(scanner);

        Assert.True(await harness.Processor.ProcessNextAsync(default));

        (FileRecord record, List<ScanAttempt> attempts) = await LoadRecordAsync(fileRecordId);
        Assert.Equal(FileRecordStatus.Clean, record.Status);
        Assert.Equal(FileStorageState.Clean, record.StorageState);
        Assert.Equal("ClamAV test-version", record.LastScannerVersion);
        Assert.Null(record.ActiveScanAttemptId);
        Assert.Single(attempts);
        Assert.Equal(ScanAttemptResult.Clean, attempts[0].Result);
        Assert.Equal("ClamAV test-version", attempts[0].ScannerVersion);
        Assert.False(File.Exists(Path.Combine(factory.QuarantineRootPath, record.StorageName)));
        Assert.Equal(
            content,
            await File.ReadAllBytesAsync(Path.Combine(factory.CleanRootPath, record.StorageName)));
    }

    [Fact]
    public async Task ProcessNext_InfectedResult_DeletesBytesAndRetainsDetectionMetadata()
    {
        byte[] content = "simulated infected content"u8.ToArray();
        Guid fileRecordId = await CreatePendingFileAsync(content);
        var scanner = new QueueClamAvClient(
            ClamAvScanResult.Infected("Eicar-Signature") with
            {
                ScannerVersion = "ClamAV test-version"
            });
        using ProcessorHarness harness = CreateHarness(scanner);

        Assert.True(await harness.Processor.ProcessNextAsync(default));

        (FileRecord record, List<ScanAttempt> attempts) = await LoadRecordAsync(fileRecordId);
        Assert.Equal(FileRecordStatus.Infected, record.Status);
        Assert.Equal(FileStorageState.Deleted, record.StorageState);
        Assert.Equal("Eicar-Signature", record.DetectionName);
        Assert.Single(attempts);
        Assert.Equal(ScanAttemptResult.Infected, attempts[0].Result);
        Assert.False(attempts[0].IsRetryable);
        Assert.False(File.Exists(Path.Combine(factory.QuarantineRootPath, record.StorageName)));
        Assert.False(File.Exists(Path.Combine(factory.CleanRootPath, record.StorageName)));
    }

    [Fact]
    public async Task ProcessNext_ClamAvUnavailable_IsScanFailedAndNeverClean()
    {
        Guid fileRecordId = await CreatePendingFileAsync("quarantined"u8.ToArray());
        var scanner = new QueueClamAvClient(ClamAvScanResult.Failed(
            ScanFailureCode.ScannerUnavailable,
            isRetryable: true));
        var timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);
        using ProcessorHarness harness = CreateHarness(scanner, timeProvider);

        Assert.True(await harness.Processor.ProcessNextAsync(default));

        (FileRecord record, List<ScanAttempt> attempts) = await LoadRecordAsync(fileRecordId);
        Assert.Equal(FileRecordStatus.ScanFailed, record.Status);
        Assert.Equal(FileStorageState.Quarantine, record.StorageState);
        Assert.Equal(ScanFailureCode.ScannerUnavailable, record.LastScanFailureCode);
        Assert.NotNull(record.NextScanAttemptAtUtc);
        Assert.Single(attempts);
        Assert.Equal(ScanAttemptResult.Failed, attempts[0].Result);
        Assert.True(File.Exists(Path.Combine(factory.QuarantineRootPath, record.StorageName)));
        Assert.False(File.Exists(Path.Combine(factory.CleanRootPath, record.StorageName)));

        timeProvider.Advance(TimeSpan.FromSeconds(5));
        await harness.Processor.PrepareDurableWorkAsync(default);
        await harness.Processor.ProcessNextAsync(default);
        timeProvider.Advance(TimeSpan.FromSeconds(10));
        await harness.Processor.PrepareDurableWorkAsync(default);
        await harness.Processor.ProcessNextAsync(default);
    }

    [Fact]
    public async Task ProcessNext_RealTransportUnavailable_PersistsFailureAndNeverClean()
    {
        Guid fileRecordId = await CreatePendingFileAsync("outage content"u8.ToArray());
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int unavailablePort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        ClamAvOptions clamAvOptions = CreateClamAvOptions();
        clamAvOptions.Host = IPAddress.Loopback.ToString();
        clamAvOptions.Port = unavailablePort;
        var scanner = new ClamAvClient(Options.Create(clamAvOptions));
        ScannerWorkerOptions workerOptions = CreateWorkerOptions();
        workerOptions.MaximumAttempts = 1;
        using ProcessorHarness harness = CreateHarness(scanner, workerOptions: workerOptions);

        Assert.True(await harness.Processor.ProcessNextAsync(default));

        (FileRecord record, List<ScanAttempt> attempts) = await LoadRecordAsync(fileRecordId);
        Assert.Equal(FileRecordStatus.ScanFailed, record.Status);
        Assert.NotEqual(FileRecordStatus.Clean, record.Status);
        Assert.Equal(FileStorageState.Quarantine, record.StorageState);
        Assert.Equal(ScanFailureCode.ScannerUnavailable, record.LastScanFailureCode);
        Assert.Null(record.NextScanAttemptAtUtc);
        ScanAttempt attempt = Assert.Single(attempts);
        Assert.Equal(ScanFailureCode.ScannerUnavailable, attempt.FailureCode);
    }

    [Fact]
    public async Task ProcessNext_RetryableFailures_StopAfterMaximumAttempts()
    {
        Guid fileRecordId = await CreatePendingFileAsync("retry content"u8.ToArray());
        var scanner = new QueueClamAvClient(
            ClamAvScanResult.Failed(ScanFailureCode.Timeout, isRetryable: true),
            ClamAvScanResult.Failed(ScanFailureCode.Timeout, isRetryable: true));
        var timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);
        ScannerWorkerOptions workerOptions = CreateWorkerOptions();
        workerOptions.MaximumAttempts = 2;
        workerOptions.InitialRetryDelaySeconds = 2;
        using ProcessorHarness harness = CreateHarness(scanner, timeProvider, workerOptions);

        Assert.True(await harness.Processor.ProcessNextAsync(default));
        timeProvider.Advance(TimeSpan.FromSeconds(2));
        Assert.True(await harness.Processor.PrepareDurableWorkAsync(default) > 0);
        Assert.True(await harness.Processor.ProcessNextAsync(default));

        (FileRecord record, List<ScanAttempt> attempts) = await LoadRecordAsync(fileRecordId);
        Assert.Equal(FileRecordStatus.ScanFailed, record.Status);
        Assert.Equal(2, record.ScanAttemptCount);
        Assert.Null(record.NextScanAttemptAtUtc);
        Assert.Equal(2, attempts.Count);
        Assert.All(attempts, attempt =>
            Assert.Equal(ScanFailureCode.Timeout, attempt.FailureCode));
    }

    [Fact]
    public async Task ProcessNext_RetryableFailures_UseCappedExponentialBackoff()
    {
        Guid fileRecordId = await CreatePendingFileAsync("backoff content"u8.ToArray());
        var scanner = new QueueClamAvClient(
            ClamAvScanResult.Failed(ScanFailureCode.Timeout, isRetryable: true));
        var timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);
        ScannerWorkerOptions workerOptions = CreateWorkerOptions();
        workerOptions.MaximumAttempts = 4;
        workerOptions.InitialRetryDelaySeconds = 2;
        workerOptions.MaximumRetryDelaySeconds = 3;
        using ProcessorHarness harness = CreateHarness(scanner, timeProvider, workerOptions);

        Assert.True(await harness.Processor.ProcessNextAsync(default));
        (FileRecord firstFailure, _) = await LoadRecordAsync(fileRecordId);
        AssertTimestampClose(
            timeProvider.GetUtcNow().AddSeconds(2),
            firstFailure.NextScanAttemptAtUtc);

        timeProvider.Advance(TimeSpan.FromSeconds(2));
        await harness.Processor.PrepareDurableWorkAsync(default);
        Assert.True(await harness.Processor.ProcessNextAsync(default));
        (FileRecord secondFailure, _) = await LoadRecordAsync(fileRecordId);
        AssertTimestampClose(
            timeProvider.GetUtcNow().AddSeconds(3),
            secondFailure.NextScanAttemptAtUtc);

        timeProvider.Advance(TimeSpan.FromSeconds(3));
        await harness.Processor.PrepareDurableWorkAsync(default);
        Assert.True(await harness.Processor.ProcessNextAsync(default));
        timeProvider.Advance(TimeSpan.FromSeconds(3));
        await harness.Processor.PrepareDurableWorkAsync(default);
        Assert.True(await harness.Processor.ProcessNextAsync(default));
        (FileRecord exhausted, _) = await LoadRecordAsync(fileRecordId);
        Assert.Null(exhausted.NextScanAttemptAtUtc);
    }

    [Fact]
    public async Task PrepareDurableWork_StaleClaimIsRecoveredAndCanCompleteAfterNewScope()
    {
        Guid fileRecordId = await CreatePendingFileAsync("restart content"u8.ToArray());
        var timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);
        ScannerWorkerOptions workerOptions = CreateWorkerOptions();
        workerOptions.InitialRetryDelaySeconds = 1;
        workerOptions.StuckJobTimeoutSeconds = 10;

        using (ProcessorHarness firstProcess = CreateHarness(
                   new QueueClamAvClient(),
                   timeProvider,
                   workerOptions))
        {
            FileScanJob? abandonedClaim = await firstProcess.Workflow.TryClaimNextAsync(default);
            Assert.NotNull(abandonedClaim);
        }

        timeProvider.Advance(TimeSpan.FromSeconds(11));
        using (ProcessorHarness recoveryProcess = CreateHarness(
                   new QueueClamAvClient(),
                   timeProvider,
                   workerOptions))
        {
            Assert.True(await recoveryProcess.Processor.PrepareDurableWorkAsync(default) > 0);
        }

        (FileRecord failedRecord, List<ScanAttempt> failedAttempts) =
            await LoadRecordAsync(fileRecordId);
        Assert.Equal(FileRecordStatus.ScanFailed, failedRecord.Status);
        Assert.Single(failedAttempts);
        Assert.Equal(ScanFailureCode.WorkerInterrupted, failedAttempts[0].FailureCode);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        using (ProcessorHarness restartedProcess = CreateHarness(
                   new QueueClamAvClient(ClamAvScanResult.Clean()),
                   timeProvider,
                   workerOptions))
        {
            await restartedProcess.Processor.PrepareDurableWorkAsync(default);
            Assert.True(await restartedProcess.Processor.ProcessNextAsync(default));
        }

        (FileRecord recoveredRecord, List<ScanAttempt> recoveredAttempts) =
            await LoadRecordAsync(fileRecordId);
        Assert.Equal(FileRecordStatus.Clean, recoveredRecord.Status);
        Assert.Equal(2, recoveredAttempts.Count);
    }

    [Fact]
    public async Task HostedWorker_ProcessesDurablePendingRecord()
    {
        var scanner = new QueueClamAvClient(ClamAvScanResult.Clean());
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
                services.AddSingleton<IClamAvClient>(scanner);
            });
        });
        _ = workerFactory.Services;
        Guid fileRecordId = await CreatePendingFileAsync("background content"u8.ToArray());
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        FileRecord? record = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            (record, _) = await LoadRecordAsync(fileRecordId);
            if (record.Status == FileRecordStatus.Clean)
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert.NotNull(record);
        Assert.Equal(FileRecordStatus.Clean, record.Status);
    }

    private ProcessorHarness CreateHarness(
        IClamAvClient scanner,
        TimeProvider? timeProvider = null,
        ScannerWorkerOptions? workerOptions = null)
    {
        IServiceScope scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FileSentryDbContext>();
        var storagePaths = scope.ServiceProvider.GetRequiredService<StoragePathProvider>();
        TimeProvider effectiveTimeProvider = timeProvider ?? TimeProvider.System;
        var workflow = new FileScanWorkflowService(
            dbContext,
            storagePaths,
            Options.Create(workerOptions ?? CreateWorkerOptions()),
            effectiveTimeProvider,
            NullLogger<FileScanWorkflowService>.Instance);
        var processor = new FileScanProcessor(
            workflow,
            scanner,
            storagePaths,
            Options.Create(CreateClamAvOptions()),
            NullLogger<FileScanProcessor>.Instance);
        return new ProcessorHarness(scope, workflow, processor);
    }

    private async Task<Guid> CreatePendingFileAsync(byte[] content)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FileSentryDbContext>();
        var storagePaths = scope.ServiceProvider.GetRequiredService<StoragePathProvider>();
        Guid userId = Guid.NewGuid();
        string normalizedEmail = $"SCANNER-{Guid.NewGuid():N}@EXAMPLE.COM";
        dbContext.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = normalizedEmail.ToLowerInvariant(),
            NormalizedUserName = normalizedEmail,
            Email = normalizedEmail.ToLowerInvariant(),
            NormalizedEmail = normalizedEmail,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        Guid fileRecordId = Guid.NewGuid();
        string storageName = storagePaths.CreateStorageName(fileRecordId);
        dbContext.FileRecords.Add(new FileRecord
        {
            Id = fileRecordId,
            OwnerId = userId,
            OriginalFileName = "scan.pdf",
            StorageName = storageName,
            StorageState = FileStorageState.Quarantine,
            SizeBytes = content.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            DetectedMediaType = "application/pdf",
            Status = FileRecordStatus.PendingScan,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();
        await File.WriteAllBytesAsync(storagePaths.GetQuarantinePath(storageName), content);
        return fileRecordId;
    }

    private async Task<(FileRecord Record, List<ScanAttempt> Attempts)> LoadRecordAsync(
        Guid fileRecordId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FileSentryDbContext>();
        FileRecord record = await dbContext.FileRecords
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == fileRecordId);
        List<ScanAttempt> attempts = await dbContext.ScanAttempts
            .AsNoTracking()
            .Where(attempt => attempt.FileRecordId == fileRecordId)
            .OrderBy(attempt => attempt.AttemptNumber)
            .ToListAsync();
        return (record, attempts);
    }

    private static ScannerWorkerOptions CreateWorkerOptions() => new()
    {
        Enabled = false,
        MaximumAttempts = 3,
        InitialRetryDelaySeconds = 5,
        MaximumRetryDelaySeconds = 60,
        StuckJobTimeoutSeconds = 120,
        PollingIntervalMilliseconds = 100
    };

    private static ClamAvOptions CreateClamAvOptions() => new()
    {
        MaximumStreamSizeBytes = 10 * 1024 * 1024,
        StreamChunkSizeBytes = 64 * 1024
    };

    private static void AssertTimestampClose(
        DateTimeOffset expected,
        DateTimeOffset? actual)
    {
        Assert.NotNull(actual);
        Assert.InRange(
            (actual.Value - expected).Duration(),
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(1));
    }

    private sealed class QueueClamAvClient(params ClamAvScanResult[] results) : IClamAvClient
    {
        private readonly Queue<ClamAvScanResult> _results = new(results);
        private ClamAvScanResult? _lastResult;

        public async Task<ClamAvScanResult> ScanAsync(
            Stream content,
            CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[4096];
            while (await content.ReadAsync(buffer, cancellationToken) > 0)
            {
            }

            if (_results.TryDequeue(out ClamAvScanResult? result))
            {
                _lastResult = result;
            }

            return _lastResult
                ?? throw new InvalidOperationException("No fake ClamAV result was configured.");
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration)
        {
            utcNow += duration;
        }
    }

    private sealed class ProcessorHarness(
        IServiceScope scope,
        FileScanWorkflowService workflow,
        FileScanProcessor processor) : IDisposable
    {
        public FileScanWorkflowService Workflow { get; } = workflow;

        public FileScanProcessor Processor { get; } = processor;

        public void Dispose()
        {
            scope.Dispose();
        }
    }
}
