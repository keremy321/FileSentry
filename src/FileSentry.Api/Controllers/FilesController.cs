using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FileSentry.Api.Application.Files;
using FileSentry.Api.Contracts.Files;
using FileSentry.Api.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FileSentry.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/files")]
public sealed class FilesController(
    FileIngestionService ingestionService,
    FileAccessService accessService,
    ILogger<FilesController> logger) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<FileMetadataResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (!TryGetOwnerId(out Guid ownerId))
        {
            return Unauthorized();
        }

        IReadOnlyList<FileMetadataResponse> files = await accessService.ListAsync(
            ownerId,
            cancellationToken);
        return Ok(files);
    }

    [HttpGet("{fileId:guid}")]
    [ProducesResponseType<FileMetadataResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMetadata(
        Guid fileId,
        CancellationToken cancellationToken)
    {
        if (!TryGetOwnerId(out Guid ownerId))
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await accessService.GetMetadataAsync(
                ownerId,
                fileId,
                cancellationToken));
        }
        catch (FileAccessException exception)
        {
            return FileProblem(exception);
        }
    }

    [HttpGet("{fileId:guid}/download")]
    public async Task<IActionResult> Download(
        Guid fileId,
        CancellationToken cancellationToken)
    {
        if (!TryGetOwnerId(out Guid ownerId))
        {
            return Unauthorized();
        }

        try
        {
            FileDownload download = await accessService.OpenDownloadAsync(
                ownerId,
                fileId,
                cancellationToken);
            Response.Headers.XContentTypeOptions = "nosniff";
            return File(
                download.Content,
                download.MediaType,
                download.DownloadName,
                enableRangeProcessing: false);
        }
        catch (FileAccessException exception)
        {
            return FileProblem(exception);
        }
    }

    [HttpDelete("{fileId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(
        Guid fileId,
        CancellationToken cancellationToken)
    {
        if (!TryGetOwnerId(out Guid ownerId))
        {
            return Unauthorized();
        }

        try
        {
            await accessService.DeleteAsync(ownerId, fileId, cancellationToken);
            return NoContent();
        }
        catch (FileAccessException exception)
        {
            return FileProblem(exception);
        }
    }

    [HttpPost]
    [DisableFormValueModelBinding]
    [ProducesResponseType<FileUploadResponse>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Upload(CancellationToken cancellationToken)
    {
        if (!TryGetOwnerId(out Guid ownerId))
        {
            return Unauthorized();
        }

        try
        {
            FileUploadResponse response = await ingestionService.IngestAsync(
                Request.Body,
                Request.ContentType,
                ownerId,
                cancellationToken);
            return Accepted(response);
        }
        catch (FileIngestionException exception)
        {
            return UploadProblem(exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Secure file ingestion failed.");
            return UploadProblem(new FileIngestionException(
                StatusCodes.Status500InternalServerError,
                "UPLOAD_FAILED",
                "Upload failed",
                "The file could not be accepted safely."));
        }
    }

    private bool TryGetOwnerId(out Guid ownerId)
    {
        string? subject = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        return Guid.TryParse(subject, out ownerId);
    }

    private ObjectResult FileProblem(FileAccessException exception)
    {
        var problemDetails = new ProblemDetails
        {
            Status = exception.StatusCode,
            Title = exception.Title,
            Detail = exception.Message,
            Instance = HttpContext.Request.Path
        };
        problemDetails.Extensions["code"] = exception.Code;

        var result = new ObjectResult(problemDetails)
        {
            StatusCode = exception.StatusCode
        };
        result.ContentTypes.Add("application/problem+json");
        return result;
    }

    private ObjectResult UploadProblem(FileIngestionException exception)
    {
        var problemDetails = new ProblemDetails
        {
            Status = exception.StatusCode,
            Title = exception.Title,
            Detail = exception.Message,
            Instance = HttpContext.Request.Path
        };
        problemDetails.Extensions["code"] = exception.Code;

        var result = new ObjectResult(problemDetails)
        {
            StatusCode = exception.StatusCode
        };
        result.ContentTypes.Add("application/problem+json");
        return result;
    }
}
