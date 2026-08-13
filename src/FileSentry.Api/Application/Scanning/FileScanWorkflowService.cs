using FileSentry.Api.Domain.Files;
using FileSentry.Api.Domain.Scanning;
using FileSentry.Api.Infrastructure.Options;
using FileSentry.Api.Infrastructure.Scanning;
using FileSentry.Api.Infrastructure.Storage;
using FileSentry.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace FileSentry.Api.Application.Scanning;

public sealed class FileScanWorkflowService(
    FileSentryDbContext dbContext,
    StoragePathProvider storagePaths,
    IOptions<ScannerWorkerOptions> options,
    TimeProvider timeProvider,
    ILogger<FileScanWorkflowService> logger)
{
    private const int RecoveryBatchSize = 100;

    public async Task<int> PrepareDurableWorkAsync(CancellationToken cancellationToken)
    {
        int recovered = await RecoverCompletedAndStaleJobsAsync(cancellationToken);
        int cleaned = await CleanupInfectedFilesAsync(cancellationToken);
        int requeued = await RequeueDueFailuresAsync(cancellationToken);
        return recovered + cleaned + requeued;
    }

    public async Task<FileScanJob?> TryClaimNextAsync(CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        DateTimeOffset now = timeProvider.GetUtcNow();
        string pendingStatus = FileRecordStatus.PendingScan.ToString();
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        FileRecord? record = await dbContext.FileRecords
            .FromSqlInterpolated($$"""
                SELECT f.*
                FROM "FileRecords" AS f
                WHERE f."Status" = {{pendingStatus}}
                  AND (f."NextScanAttemptAtUtc" IS NULL OR f."NextScanAttemptAtUtc" <= {{now}})
                ORDER BY COALESCE(f."NextScanAttemptAtUtc", f."CreatedAtUtc"), f."CreatedAtUtc", f."Id"
                FOR UPDATE SKIP LOCKED
                LIMIT 1
                """)
            .SingleOrDefaultAsync(cancellationToken);
        if (record is null)
        {
            return null;
        }

        Guid attemptId = Guid.NewGuid();
        int attemptNumber = checked(record.ScanAttemptCount + 1);
        record.Status = FileRecordStatus.Scanning;
        record.ScanAttemptCount = attemptNumber;
        record.ActiveScanAttemptId = attemptId;
        record.ScanningStartedAtUtc = now;
        record.NextScanAttemptAtUtc = null;
        record.ScanCompletedAtUtc = null;
        record.DetectionName = null;
        record.LastScanFailureCode = null;
        record.UpdatedAtUtc = now;

        dbContext.ScanAttempts.Add(new ScanAttempt
        {
            Id = attemptId,
            FileRecordId = record.Id,
            AttemptNumber = attemptNumber,
            StartedAtUtc = now,
            Result = ScanAttemptResult.InProgress
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new FileScanJob(
            record.Id,
            attemptId,
            attemptNumber,
            record.StorageName,
            record.SizeBytes);
    }

    public async Task<bool> CompleteAndFinalizeAsync(
        FileScanJob job,
        ClamAvScanResult result,
        CancellationToken cancellationToken)
    {
        bool persisted = await PersistScanResultAsync(job, result, cancellationToken);
        return persisted
            && await FinalizeCompletedAttemptAsync(job.FileRecordId, cancellationToken);
    }

    private async Task<bool> PersistScanResultAsync(
        FileScanJob job,
        ClamAvScanResult result,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        FileRecord? record = await LockFileAsync(job.FileRecordId, cancellationToken);
        if (record is null
            || record.Status != FileRecordStatus.Scanning
            || record.ActiveScanAttemptId != job.ScanAttemptId)
        {
            return false;
        }

        ScanAttempt? attempt = await dbContext.ScanAttempts.SingleOrDefaultAsync(
            candidate => candidate.Id == job.ScanAttemptId,
            cancellationToken);
        if (attempt is null)
        {
            return false;
        }

        if (attempt.CompletedAtUtc is null)
        {
            attempt.CompletedAtUtc = timeProvider.GetUtcNow();
            attempt.Result = result.Result;
            attempt.FailureCode = result.FailureCode;
            attempt.IsRetryable = result.IsRetryable;
            attempt.DetectionName = BoundMetadata(result.DetectionName);
            attempt.ScannerVersion = BoundMetadata(result.ScannerVersion);
            record.LastScannerVersion = attempt.ScannerVersion;
            record.UpdatedAtUtc = timeProvider.GetUtcNow();
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<int> RecoverCompletedAndStaleJobsAsync(
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        DateTimeOffset staleBefore = timeProvider.GetUtcNow().AddSeconds(
            -options.Value.StuckJobTimeoutSeconds);
        List<Guid> completedIds = await (
                from record in dbContext.FileRecords.AsNoTracking()
                join attempt in dbContext.ScanAttempts.AsNoTracking()
                    on record.ActiveScanAttemptId equals (Guid?)attempt.Id
                where record.Status == FileRecordStatus.Scanning
                    && attempt.CompletedAtUtc != null
                orderby attempt.CompletedAtUtc
                select record.Id)
            .Take(RecoveryBatchSize)
            .ToListAsync(cancellationToken);
        int recovered = 0;
        foreach (Guid fileRecordId in completedIds)
        {
            if (await FinalizeCompletedAttemptAsync(fileRecordId, cancellationToken))
            {
                recovered++;
            }
        }

        dbContext.ChangeTracker.Clear();
        List<Guid> staleIds = await dbContext.FileRecords
            .AsNoTracking()
            .Where(record => record.Status == FileRecordStatus.Scanning
                && record.ScanningStartedAtUtc <= staleBefore)
            .OrderBy(record => record.ScanningStartedAtUtc)
            .Select(record => record.Id)
            .Take(RecoveryBatchSize)
            .ToListAsync(cancellationToken);
        foreach (Guid fileRecordId in staleIds)
        {
            if (await FailStaleAttemptAsync(fileRecordId, staleBefore, cancellationToken))
            {
                recovered++;
            }
        }

        return recovered;
    }

    private async Task<bool> FailStaleAttemptAsync(
        Guid fileRecordId,
        DateTimeOffset staleBefore,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        FileRecord? record = await LockFileAsync(fileRecordId, cancellationToken);
        if (record is null
            || record.Status != FileRecordStatus.Scanning
            || record.ScanningStartedAtUtc > staleBefore)
        {
            return false;
        }

        ScanAttempt? attempt = record.ActiveScanAttemptId is Guid attemptId
            ? await dbContext.ScanAttempts.SingleOrDefaultAsync(
                candidate => candidate.Id == attemptId,
                cancellationToken)
            : null;
        if (attempt?.CompletedAtUtc is not null)
        {
            return false;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        if (attempt is null)
        {
            int recoveredAttemptNumber = checked(record.ScanAttemptCount + 1);
            attempt = new ScanAttempt
            {
                Id = Guid.NewGuid(),
                FileRecordId = record.Id,
                AttemptNumber = recoveredAttemptNumber,
                StartedAtUtc = record.ScanningStartedAtUtc ?? now,
                Result = ScanAttemptResult.InProgress
            };
            dbContext.ScanAttempts.Add(attempt);
            record.ScanAttemptCount = recoveredAttemptNumber;
        }

        attempt.CompletedAtUtc = now;
        attempt.Result = ScanAttemptResult.Failed;
        attempt.FailureCode = ScanFailureCode.WorkerInterrupted;
        attempt.IsRetryable = true;
        ApplyFailure(record, attempt, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<bool> FinalizeCompletedAttemptAsync(
        Guid fileRecordId,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        FileRecord? record = await LockFileAsync(fileRecordId, cancellationToken);
        if (record is null
            || record.Status != FileRecordStatus.Scanning
            || record.ActiveScanAttemptId is not Guid attemptId)
        {
            return false;
        }

        ScanAttempt? attempt = await dbContext.ScanAttempts.SingleOrDefaultAsync(
            candidate => candidate.Id == attemptId,
            cancellationToken);
        if (attempt?.CompletedAtUtc is null)
        {
            return false;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        switch (attempt.Result)
        {
            case ScanAttemptResult.Clean:
                FinalizeClean(record, attempt, now);
                break;
            case ScanAttemptResult.Infected:
                FinalizeInfected(record, attempt, now);
                break;
            case ScanAttemptResult.Failed:
                ApplyFailure(record, attempt, now);
                break;
            default:
                return false;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(CancellationToken.None);
        return true;
    }

    private void FinalizeClean(
        FileRecord record,
        ScanAttempt attempt,
        DateTimeOffset now)
    {
        try
        {
            string quarantinePath = storagePaths.GetQuarantinePath(record.StorageName);
            string cleanPath = storagePaths.GetCleanPath(record.StorageName);
            bool quarantineExists = File.Exists(quarantinePath);
            bool cleanExists = File.Exists(cleanPath);

            if (quarantineExists && !cleanExists)
            {
                File.Move(quarantinePath, cleanPath);
            }
            else if (quarantineExists || !cleanExists)
            {
                throw new IOException("Clean-file promotion found an inconsistent storage state.");
            }

            record.Status = FileRecordStatus.Clean;
            record.StorageState = FileStorageState.Clean;
            record.ScanCompletedAtUtc = now;
            record.UpdatedAtUtc = now;
            record.DetectionName = null;
            record.LastScanFailureCode = null;
            ClearActiveClaim(record);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            attempt.FailureCode = StoredBytesExist(record.StorageName)
                ? ScanFailureCode.StorageError
                : ScanFailureCode.FileMissing;
            attempt.IsRetryable = attempt.FailureCode == ScanFailureCode.StorageError;
            ApplyFailure(record, attempt, now);
            logger.LogError(
                exception,
                "Clean-file promotion failed for file record {FileRecordId}.",
                record.Id);
        }
    }

    private void FinalizeInfected(
        FileRecord record,
        ScanAttempt attempt,
        DateTimeOffset now)
    {
        bool deletionSucceeded = TryDeleteStoredBytes(record);
        record.Status = FileRecordStatus.Infected;
        record.StorageState = deletionSucceeded
            ? FileStorageState.Deleted
            : FileStorageState.Quarantine;
        record.ScanCompletedAtUtc = now;
        record.UpdatedAtUtc = now;
        record.DetectionName = attempt.DetectionName;
        record.LastScanFailureCode = deletionSucceeded
            ? null
            : ScanFailureCode.StorageError;
        if (!deletionSucceeded)
        {
            attempt.FailureCode = ScanFailureCode.StorageError;
        }

        attempt.IsRetryable = false;
        ClearActiveClaim(record);
    }

    private async Task<int> CleanupInfectedFilesAsync(CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        List<Guid> candidateIds = await dbContext.FileRecords
            .AsNoTracking()
            .Where(record => record.Status == FileRecordStatus.Infected
                && record.StorageState != FileStorageState.Deleted)
            .OrderBy(record => record.ScanCompletedAtUtc)
            .Select(record => record.Id)
            .Take(RecoveryBatchSize)
            .ToListAsync(cancellationToken);
        int cleaned = 0;

        foreach (Guid fileRecordId in candidateIds)
        {
            dbContext.ChangeTracker.Clear();
            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);
            FileRecord? record = await LockFileAsync(fileRecordId, cancellationToken);
            if (record is null
                || record.Status != FileRecordStatus.Infected
                || record.StorageState == FileStorageState.Deleted)
            {
                continue;
            }

            if (TryDeleteStoredBytes(record))
            {
                record.StorageState = FileStorageState.Deleted;
                record.LastScanFailureCode = null;
                record.UpdatedAtUtc = timeProvider.GetUtcNow();
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(CancellationToken.None);
                cleaned++;
            }
        }

        return cleaned;
    }

    private async Task<int> RequeueDueFailuresAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        int maximumAttempts = options.Value.MaximumAttempts;
        dbContext.ChangeTracker.Clear();
        return await dbContext.FileRecords
            .Where(record => record.Status == FileRecordStatus.ScanFailed
                && record.NextScanAttemptAtUtc != null
                && record.NextScanAttemptAtUtc <= now
                && record.ScanAttemptCount < maximumAttempts)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(record => record.Status, FileRecordStatus.PendingScan)
                .SetProperty(record => record.NextScanAttemptAtUtc, (DateTimeOffset?)null)
                .SetProperty(record => record.UpdatedAtUtc, now),
                cancellationToken);
    }

    private void ApplyFailure(
        FileRecord record,
        ScanAttempt attempt,
        DateTimeOffset now)
    {
        record.Status = FileRecordStatus.ScanFailed;
        record.ScanCompletedAtUtc = now;
        record.UpdatedAtUtc = now;
        record.DetectionName = null;
        record.LastScanFailureCode = attempt.FailureCode ?? ScanFailureCode.Unknown;
        record.NextScanAttemptAtUtc = attempt.IsRetryable
            && record.ScanAttemptCount < options.Value.MaximumAttempts
                ? now + CalculateBackoff(record.ScanAttemptCount)
                : null;
        ClearActiveClaim(record);
    }

    private TimeSpan CalculateBackoff(int completedAttemptCount)
    {
        ScannerWorkerOptions configuredOptions = options.Value;
        long multiplier = 1L << Math.Min(Math.Max(completedAttemptCount - 1, 0), 30);
        long seconds = Math.Min(
            configuredOptions.MaximumRetryDelaySeconds,
            configuredOptions.InitialRetryDelaySeconds * multiplier);
        return TimeSpan.FromSeconds(seconds);
    }

    private bool TryDeleteStoredBytes(FileRecord record)
    {
        try
        {
            string quarantinePath = storagePaths.GetQuarantinePath(record.StorageName);
            string cleanPath = storagePaths.GetCleanPath(record.StorageName);
            if (File.Exists(quarantinePath))
            {
                File.Delete(quarantinePath);
            }

            if (File.Exists(cleanPath))
            {
                File.Delete(cleanPath);
            }

            return !File.Exists(quarantinePath) && !File.Exists(cleanPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogError(
                exception,
                "Infected-byte deletion failed for file record {FileRecordId}.",
                record.Id);
            return false;
        }
    }

    private bool StoredBytesExist(string storageName)
    {
        try
        {
            return File.Exists(storagePaths.GetQuarantinePath(storageName))
                || File.Exists(storagePaths.GetCleanPath(storageName));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<FileRecord?> LockFileAsync(
        Guid fileRecordId,
        CancellationToken cancellationToken)
    {
        return await dbContext.FileRecords
            .FromSqlInterpolated($$"""
                SELECT f.*
                FROM "FileRecords" AS f
                WHERE f."Id" = {{fileRecordId}}
                FOR UPDATE SKIP LOCKED
                """)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static void ClearActiveClaim(FileRecord record)
    {
        record.ActiveScanAttemptId = null;
        record.ScanningStartedAtUtc = null;
    }

    private static string? BoundMetadata(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : new string(value
                .Select(character => char.IsControl(character) ? '_' : character)
                .Take(256)
                .ToArray());
    }
}
