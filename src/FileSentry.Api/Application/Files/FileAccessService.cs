using FileSentry.Api.Contracts.Files;
using FileSentry.Api.Domain.Files;
using FileSentry.Api.Domain.Scanning;
using FileSentry.Api.Infrastructure.Storage;
using FileSentry.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FileSentry.Api.Application.Files;

public sealed class FileAccessService(
    FileSentryDbContext dbContext,
    StoragePathProvider storagePaths,
    TimeProvider timeProvider,
    ILogger<FileAccessService> logger)
{
    public async Task<IReadOnlyList<FileMetadataResponse>> ListAsync(
        Guid ownerId,
        CancellationToken cancellationToken)
    {
        return await dbContext.FileRecords
            .AsNoTracking()
            .Where(record => record.OwnerId == ownerId)
            .OrderByDescending(record => record.CreatedAtUtc)
            .ThenByDescending(record => record.Id)
            .Select(record => new FileMetadataResponse(
                record.Id,
                record.OriginalFileName,
                record.SizeBytes,
                record.Sha256,
                record.DetectedMediaType,
                record.Status.ToString(),
                record.CreatedAtUtc,
                record.UpdatedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<FileMetadataResponse> GetMetadataAsync(
        Guid ownerId,
        Guid fileId,
        CancellationToken cancellationToken)
    {
        FileMetadataResponse? response = await dbContext.FileRecords
            .AsNoTracking()
            .Where(record => record.OwnerId == ownerId && record.Id == fileId)
            .Select(record => new FileMetadataResponse(
                record.Id,
                record.OriginalFileName,
                record.SizeBytes,
                record.Sha256,
                record.DetectedMediaType,
                record.Status.ToString(),
                record.CreatedAtUtc,
                record.UpdatedAtUtc))
            .SingleOrDefaultAsync(cancellationToken);

        return response ?? throw NotFound();
    }

    public async Task<FileDownload> OpenDownloadAsync(
        Guid ownerId,
        Guid fileId,
        CancellationToken cancellationToken)
    {
        DownloadRecord? record = await dbContext.FileRecords
            .AsNoTracking()
            .Where(candidate => candidate.OwnerId == ownerId && candidate.Id == fileId)
            .Select(candidate => new DownloadRecord(
                candidate.Id,
                candidate.OriginalFileName,
                candidate.StorageName,
                candidate.StorageState,
                candidate.SizeBytes,
                candidate.DetectedMediaType,
                candidate.Status))
            .SingleOrDefaultAsync(cancellationToken);
        if (record is null)
        {
            throw NotFound();
        }

        if (record.Status != FileRecordStatus.Clean)
        {
            throw new FileAccessException(
                StatusCodes.Status409Conflict,
                "FILE_NOT_CLEAN",
                "File is not available for download",
                "Only files with a conclusive clean scan result can be downloaded.");
        }

        if (record.StorageState != FileStorageState.Clean)
        {
            throw ContentUnavailable(record.Id);
        }

        try
        {
            string cleanPath = storagePaths.GetCleanPath(record.StorageName);
            var content = new FileStream(
                cleanPath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read | FileShare.Delete,
                    BufferSize = 64 * 1024,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
            if (content.Length != record.SizeBytes)
            {
                await content.DisposeAsync();
                throw ContentUnavailable(record.Id);
            }

            string safeDownloadName = FileNameSanitizer.Sanitize(record.OriginalFileName);
            if (string.IsNullOrWhiteSpace(safeDownloadName))
            {
                safeDownloadName = "download";
            }

            return new FileDownload(content, record.DetectedMediaType, safeDownloadName);
        }
        catch (FileAccessException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogDebug(
                exception,
                "Opening trusted clean content failed for file record {FileRecordId}.",
                record.Id);
            throw ContentUnavailable(record.Id);
        }
    }

    public async Task DeleteAsync(
        Guid ownerId,
        Guid fileId,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        FileRecord? record = await dbContext.FileRecords
            .FromSqlInterpolated($$"""
                SELECT f.*
                FROM "FileRecords" AS f
                WHERE f."OwnerId" = {{ownerId}}
                  AND f."Id" = {{fileId}}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
        if (record is null)
        {
            throw NotFound();
        }

        try
        {
            DeleteStoredBytes(record.StorageName);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogError(
                exception,
                "Stored bytes could not be deleted for file record {FileRecordId}.",
                record.Id);
            throw new FileAccessException(
                StatusCodes.Status500InternalServerError,
                "FILE_DELETE_FAILED",
                "File deletion failed",
                "The file could not be deleted safely.");
        }

        if (record.ActiveScanAttemptId is Guid attemptId)
        {
            ScanAttempt? attempt = await dbContext.ScanAttempts.SingleOrDefaultAsync(
                candidate => candidate.Id == attemptId,
                cancellationToken);
            if (attempt is not null && attempt.CompletedAtUtc is null)
            {
                attempt.CompletedAtUtc = timeProvider.GetUtcNow();
                attempt.Result = ScanAttemptResult.Failed;
                attempt.FailureCode = ScanFailureCode.WorkerInterrupted;
                attempt.IsRetryable = false;
            }
        }

        record.Status = FileRecordStatus.Deleted;
        record.StorageState = FileStorageState.Deleted;
        record.ActiveScanAttemptId = null;
        record.ScanningStartedAtUtc = null;
        record.NextScanAttemptAtUtc = null;
        record.UpdatedAtUtc = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(CancellationToken.None);
    }

    private void DeleteStoredBytes(string storageName)
    {
        string quarantinePath = storagePaths.GetQuarantinePath(storageName);
        string cleanPath = storagePaths.GetCleanPath(storageName);
        if (File.Exists(quarantinePath))
        {
            File.Delete(quarantinePath);
        }

        if (File.Exists(cleanPath))
        {
            File.Delete(cleanPath);
        }

        if (File.Exists(quarantinePath) || File.Exists(cleanPath))
        {
            throw new IOException("Stored file deletion did not complete.");
        }
    }

    private FileAccessException ContentUnavailable(Guid fileId)
    {
        logger.LogError(
            "Database state is clean but trusted content is unavailable for file record {FileRecordId}.",
            fileId);
        return new FileAccessException(
            StatusCodes.Status500InternalServerError,
            "FILE_CONTENT_UNAVAILABLE",
            "File content is unavailable",
            "The clean file content is not available.");
    }

    private static FileAccessException NotFound() => new(
        StatusCodes.Status404NotFound,
        "FILE_NOT_FOUND",
        "File not found",
        "The requested file was not found.");

    private sealed record DownloadRecord(
        Guid Id,
        string OriginalFileName,
        string StorageName,
        FileStorageState StorageState,
        long SizeBytes,
        string DetectedMediaType,
        FileRecordStatus Status);
}
