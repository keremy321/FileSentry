using FileSentry.Client;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace FileSentry.AspNetExample;

internal static class ScanEndpoint
{
    private const int MaximumBoundaryLength = 128;

    public static async Task<IResult> HandleAsync(
        HttpContext context,
        FileForwardingService forwardingService,
        CancellationToken cancellationToken)
    {
        if (!TryGetBoundary(context.Request.ContentType, out string boundary))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid upload",
                "EXAMPLE_MULTIPART_REQUIRED",
                "Send one file using multipart/form-data.");
        }

        try
        {
            var reader = new MultipartReader(boundary, context.Request.Body);
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(cancellationToken)) is not null)
            {
                if (!TryGetFileName(section, out string fileName))
                {
                    continue;
                }

                ForwardedFile forwarded = await forwardingService.ForwardAsync(
                    section.Body,
                    fileName,
                    cancellationToken);
                if (forwarded.Metadata.Status != FileStatus.Clean)
                {
                    await forwarded.DisposeAsync();
                    return NonCleanResult(forwarded.Metadata.Status);
                }

                context.Response.RegisterForDisposeAsync(forwarded);
                return Results.Stream(
                    forwarded.CleanContent!,
                    forwarded.Metadata.DetectedMediaType,
                    forwarded.Metadata.OriginalFileName,
                    enableRangeProcessing: false);
            }

            return Problem(
                StatusCodes.Status400BadRequest,
                "File missing",
                "EXAMPLE_FILE_REQUIRED",
                "The multipart request did not contain a file section.");
        }
        catch (FileSentryPollingTimeoutException)
        {
            return Problem(
                StatusCodes.Status504GatewayTimeout,
                "Scan timed out",
                "EXAMPLE_SCAN_TIMEOUT",
                "The file was not processed because scanning did not finish in time.");
        }
        catch (FileSentryApiException)
        {
            return Problem(
                StatusCodes.Status502BadGateway,
                "File security service rejected the request",
                "EXAMPLE_FILESENTRY_API_ERROR",
                "The file could not be evaluated safely.");
        }
        catch (FileSentryProtocolException)
        {
            return Problem(
                StatusCodes.Status502BadGateway,
                "Unexpected file security response",
                "EXAMPLE_FILESENTRY_PROTOCOL_ERROR",
                "The file could not be evaluated safely.");
        }
        catch (HttpRequestException)
        {
            return Problem(
                StatusCodes.Status502BadGateway,
                "File security service unavailable",
                "EXAMPLE_FILESENTRY_UNAVAILABLE",
                "The file could not be evaluated safely.");
        }
        catch (InvalidDataException)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid multipart upload",
                "EXAMPLE_MULTIPART_INVALID",
                "The upload could not be read safely.");
        }
        catch (ArgumentException)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid upload metadata",
                "EXAMPLE_UPLOAD_INVALID",
                "The upload metadata was not accepted.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    private static IResult NonCleanResult(FileStatus status) => status switch
    {
        FileStatus.Infected => Problem(
            StatusCodes.Status422UnprocessableEntity,
            "File rejected",
            "EXAMPLE_FILE_INFECTED",
            "The file was not released because malware was detected."),
        FileStatus.ScanFailed => Problem(
            StatusCodes.Status503ServiceUnavailable,
            "Scan failed",
            "EXAMPLE_SCAN_FAILED",
            "The file was not released because scanning was inconclusive."),
        FileStatus.Deleted => Problem(
            StatusCodes.Status409Conflict,
            "File unavailable",
            "EXAMPLE_FILE_DELETED",
            "The file is no longer available."),
        _ => Problem(
            StatusCodes.Status502BadGateway,
            "Unexpected scan status",
            "EXAMPLE_SCAN_STATUS_INVALID",
            "The file was not released because no clean result was available.")
    };

    private static bool TryGetBoundary(string? contentType, out string boundary)
    {
        boundary = string.Empty;
        if (!MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? mediaType)
            || !string.Equals(
                mediaType.MediaType.Value,
                "multipart/form-data",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        StringSegment boundarySegment = HeaderUtilities.RemoveQuotes(mediaType.Boundary);
        if (!boundarySegment.HasValue
            || boundarySegment.Length == 0
            || boundarySegment.Length > MaximumBoundaryLength)
        {
            return false;
        }

        boundary = boundarySegment.Value!;
        return true;
    }

    private static bool TryGetFileName(MultipartSection section, out string fileName)
    {
        fileName = string.Empty;
        if (!ContentDispositionHeaderValue.TryParse(
                section.ContentDisposition,
                out ContentDispositionHeaderValue? contentDisposition)
            || !string.Equals(
                contentDisposition.DispositionType.Value,
                "form-data",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        StringSegment fileNameSegment = contentDisposition.FileNameStar.HasValue
            ? contentDisposition.FileNameStar
            : contentDisposition.FileName;
        fileName = HeaderUtilities.RemoveQuotes(fileNameSegment).Value ?? string.Empty;
        return !string.IsNullOrWhiteSpace(fileName);
    }

    private static IResult Problem(
        int statusCode,
        string title,
        string code,
        string detail) => Results.Problem(
            statusCode: statusCode,
            title: title,
            detail: detail,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code
            });
}
