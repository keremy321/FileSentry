using System.Buffers;
using System.Security.Cryptography;
using FileSentry.Api.Contracts.Files;
using FileSentry.Api.Domain.Files;
using FileSentry.Api.Infrastructure.Storage;
using FileSentry.Api.Persistence;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Net.Http.Headers;

namespace FileSentry.Api.Application.Files;

public sealed class FileIngestionService(
    FileSentryDbContext dbContext,
    StoragePathProvider storagePaths,
    FileFormatValidator formatValidator,
    TimeProvider timeProvider)
{
    public const long MaximumFileSizeBytes = 10 * 1024 * 1024;

    private const int StreamBufferSize = 64 * 1024;
    private const int MaximumBoundaryLength = 128;
    private const int MaximumMultipartSectionCount = 8;
    private const int MaximumNonFileSectionBytes = 16 * 1024;
    private const int MaximumClientMediaTypeLength = 256;

    public async Task<FileUploadResponse> IngestAsync(
        Stream requestBody,
        string? requestContentType,
        Guid ownerId,
        CancellationToken cancellationToken)
    {
        UploadedTempFile? uploadedFile = null;
        string? quarantinePath = null;
        bool movedToQuarantine = false;
        bool committed = false;

        try
        {
            uploadedFile = await ReadMultipartFileAsync(
                requestBody,
                requestContentType,
                cancellationToken);

            FileFormatValidationResult validation = await formatValidator.ValidateAsync(
                uploadedFile.TempPath,
                cancellationToken);
            ValidateDetectedFormat(uploadedFile.ExpectedFormat, validation);

            SupportedFileFormat detectedFormat = validation.DetectedFormat!.Value;
            Guid fileId = Guid.NewGuid();
            DateTimeOffset now = timeProvider.GetUtcNow();
            string storageName = storagePaths.CreateStorageName(fileId);
            quarantinePath = storagePaths.GetQuarantinePath(storageName);
            var record = new FileRecord
            {
                Id = fileId,
                OwnerId = ownerId,
                OriginalFileName = uploadedFile.OriginalFileName,
                StorageName = storageName,
                StorageState = FileStorageState.Quarantine,
                SizeBytes = uploadedFile.SizeBytes,
                Sha256 = uploadedFile.Sha256,
                DetectedMediaType = GetMediaType(detectedFormat),
                ClientMediaType = uploadedFile.ClientMediaType,
                Status = FileRecordStatus.PendingScan,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);
            dbContext.FileRecords.Add(record);
            await dbContext.SaveChangesAsync(cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(uploadedFile.TempPath, quarantinePath);
            movedToQuarantine = true;
            await transaction.CommitAsync(CancellationToken.None);
            committed = true;

            return new FileUploadResponse(
                record.Id,
                record.OriginalFileName,
                record.SizeBytes,
                record.Sha256,
                record.DetectedMediaType,
                record.Status.ToString(),
                record.CreatedAtUtc);
        }
        finally
        {
            if (uploadedFile is not null)
            {
                DeleteIfPresent(uploadedFile.TempPath);
            }

            if (!committed && movedToQuarantine && quarantinePath is not null)
            {
                DeleteIfPresent(quarantinePath);
            }
        }
    }

    private async Task<UploadedTempFile> ReadMultipartFileAsync(
        Stream requestBody,
        string? requestContentType,
        CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(requestContentType, out MediaTypeHeaderValue? mediaType)
            || !string.Equals(
                mediaType.MediaType.Value,
                "multipart/form-data",
                StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidMultipart();
        }

        string? boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > MaximumBoundaryLength)
        {
            throw InvalidMultipart();
        }

        var reader = new MultipartReader(boundary, requestBody)
        {
            HeadersCountLimit = 16,
            HeadersLengthLimit = 16 * 1024,
            BodyLengthLimit = MaximumFileSizeBytes + StreamBufferSize
        };
        UploadedTempFile? uploadedFile = null;
        int sectionCount = 0;

        try
        {
            while (true)
            {
                MultipartSection? section;
                try
                {
                    section = await reader.ReadNextSectionAsync(cancellationToken);
                }
                catch (IOException)
                {
                    throw InvalidMultipart();
                }

                if (section is null)
                {
                    break;
                }

                sectionCount++;
                if (sectionCount > MaximumMultipartSectionCount)
                {
                    throw InvalidMultipart();
                }

                if (!ContentDispositionHeaderValue.TryParse(
                        section.ContentDisposition,
                        out ContentDispositionHeaderValue? contentDisposition)
                    || !string.Equals(
                        contentDisposition.DispositionType.Value,
                        "form-data",
                        StringComparison.OrdinalIgnoreCase))
                {
                    await DrainNonFileSectionAsync(section.Body, cancellationToken);
                    continue;
                }

                bool isFileSection = contentDisposition.FileName.HasValue
                    || contentDisposition.FileNameStar.HasValue;
                if (!isFileSection)
                {
                    await DrainNonFileSectionAsync(section.Body, cancellationToken);
                    continue;
                }

                if (uploadedFile is not null)
                {
                    throw new FileIngestionException(
                        StatusCodes.Status400BadRequest,
                        "MULTIPLE_FILES",
                        "Multiple files are not allowed",
                        "Submit exactly one file per upload request.");
                }

                string rawFileName = HeaderUtilities.RemoveQuotes(
                    contentDisposition.FileNameStar.HasValue
                        ? contentDisposition.FileNameStar
                        : contentDisposition.FileName).Value ?? string.Empty;
                string originalFileName = FileNameSanitizer.Sanitize(rawFileName);
                SupportedFileFormat expectedFormat = GetExpectedFormat(originalFileName);
                uploadedFile = await StreamToTempAsync(
                    section.Body,
                    originalFileName,
                    expectedFormat,
                    NormalizeClientMediaType(section.ContentType),
                    cancellationToken);
            }
        }
        catch (InvalidDataException)
        {
            if (uploadedFile is not null)
            {
                DeleteIfPresent(uploadedFile.TempPath);
            }

            throw InvalidMultipart();
        }
        catch
        {
            if (uploadedFile is not null)
            {
                DeleteIfPresent(uploadedFile.TempPath);
            }

            throw;
        }

        return uploadedFile ?? throw new FileIngestionException(
            StatusCodes.Status400BadRequest,
            "FILE_REQUIRED",
            "File required",
            "The multipart request must contain exactly one file.");
    }

    private async Task<UploadedTempFile> StreamToTempAsync(
        Stream source,
        string originalFileName,
        SupportedFileFormat expectedFormat,
        string? clientMediaType,
        CancellationToken cancellationToken)
    {
        string tempPath = storagePaths.CreateTempPath();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);

        try
        {
            await using var destination = new FileStream(
                tempPath,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = StreamBufferSize,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long totalBytes = 0;

            while (true)
            {
                int bytesRead;
                try
                {
                    bytesRead = await source.ReadAsync(buffer, cancellationToken);
                }
                catch (InvalidDataException)
                {
                    throw FileTooLarge();
                }
                catch (IOException)
                {
                    throw InvalidMultipart();
                }

                if (bytesRead == 0)
                {
                    break;
                }

                if (totalBytes > MaximumFileSizeBytes - bytesRead)
                {
                    throw FileTooLarge();
                }

                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                hash.AppendData(buffer, 0, bytesRead);
                totalBytes += bytesRead;
            }

            if (totalBytes == 0)
            {
                throw new FileIngestionException(
                    StatusCodes.Status400BadRequest,
                    "EMPTY_FILE",
                    "Empty file",
                    "The uploaded file must contain at least one byte.");
            }

            await destination.FlushAsync(cancellationToken);
            return new UploadedTempFile(
                tempPath,
                originalFileName,
                expectedFormat,
                clientMediaType,
                totalBytes,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        catch
        {
            DeleteIfPresent(tempPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async Task DrainNonFileSectionAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
        int totalBytes = 0;

        try
        {
            while (true)
            {
                int bytesRead;
                try
                {
                    bytesRead = await source.ReadAsync(buffer, cancellationToken);
                }
                catch (IOException)
                {
                    throw InvalidMultipart();
                }
                if (bytesRead == 0)
                {
                    return;
                }

                totalBytes += bytesRead;
                if (totalBytes > MaximumNonFileSectionBytes)
                {
                    throw InvalidMultipart();
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static SupportedFileFormat GetExpectedFormat(string fileName)
    {
        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".pdf" => SupportedFileFormat.Pdf,
            ".png" => SupportedFileFormat.Png,
            ".jpg" or ".jpeg" => SupportedFileFormat.Jpeg,
            ".docx" => SupportedFileFormat.Docx,
            _ => throw new FileIngestionException(
                StatusCodes.Status415UnsupportedMediaType,
                "UNSUPPORTED_EXTENSION",
                "Unsupported file extension",
                "Only PDF, PNG, JPEG, and DOCX files are accepted.")
        };
    }

    private static void ValidateDetectedFormat(
        SupportedFileFormat expectedFormat,
        FileFormatValidationResult validation)
    {
        if (!validation.IsStructurallyValid)
        {
            if (expectedFormat == SupportedFileFormat.Docx && validation.IsZipContainer)
            {
                throw new FileIngestionException(
                    StatusCodes.Status400BadRequest,
                    "INVALID_DOCX",
                    "Invalid DOCX package",
                    "The uploaded ZIP content is not a valid Word Open XML document.");
            }

            throw new FileIngestionException(
                StatusCodes.Status400BadRequest,
                "INVALID_FILE_SIGNATURE",
                "Invalid file signature",
                "The uploaded bytes do not form a supported valid file.");
        }

        if (validation.DetectedFormat != expectedFormat)
        {
            throw new FileIngestionException(
                StatusCodes.Status400BadRequest,
                "FILE_TYPE_MISMATCH",
                "File type mismatch",
                "The filename extension does not match the server-detected file type.");
        }
    }

    private static string GetMediaType(SupportedFileFormat format) => format switch
    {
        SupportedFileFormat.Pdf => "application/pdf",
        SupportedFileFormat.Png => "image/png",
        SupportedFileFormat.Jpeg => "image/jpeg",
        SupportedFileFormat.Docx =>
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    private static string? NormalizeClientMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return null;
        }

        string normalized = new(
            mediaType.Trim()
                .Select(character => char.IsControl(character) ? '_' : character)
                .Take(MaximumClientMediaTypeLength)
                .ToArray());
        return normalized;
    }

    private static FileIngestionException FileTooLarge() => new(
        StatusCodes.Status413PayloadTooLarge,
        "FILE_TOO_LARGE",
        "File too large",
        "The uploaded file exceeds the 10 MiB limit.");

    private static FileIngestionException InvalidMultipart() => new(
        StatusCodes.Status400BadRequest,
        "INVALID_MULTIPART",
        "Invalid multipart request",
        "The request must be valid multipart/form-data.");

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed record UploadedTempFile(
        string TempPath,
        string OriginalFileName,
        SupportedFileFormat ExpectedFormat,
        string? ClientMediaType,
        long SizeBytes,
        string Sha256);
}
