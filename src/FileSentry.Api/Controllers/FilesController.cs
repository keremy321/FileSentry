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
    ILogger<FilesController> logger) : ControllerBase
{
    [HttpPost]
    [DisableFormValueModelBinding]
    [ProducesResponseType<FileUploadResponse>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Upload(CancellationToken cancellationToken)
    {
        string? subject = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (!Guid.TryParse(subject, out Guid ownerId))
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
