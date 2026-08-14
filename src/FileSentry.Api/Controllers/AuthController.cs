using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FileSentry.Api.Contracts.Authentication;
using FileSentry.Api.Infrastructure.Options;
using FileSentry.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FileSentry.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    JwtTokenService jwtTokenService) : ControllerBase
{
    private const int MaximumEmailLength = 256;

    [AllowAnonymous]
    [EnableRateLimiting(AuthenticationRateLimitOptions.PolicyName)]
    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        string? email = NormalizeEmailInput(request.Email);
        if (email is null || string.IsNullOrWhiteSpace(request.Password))
        {
            return AuthenticationProblem(
                StatusCodes.Status400BadRequest,
                "Invalid registration",
                "A valid email address and password are required.",
                "INVALID_REGISTRATION");
        }

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserName = email,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        IdentityResult result = await userManager.CreateAsync(user, request.Password);
        if (result.Succeeded)
        {
            return CreatedAtAction(
                nameof(GetCurrentUser),
                new CurrentUserResponse(user.Id, email));
        }

        if (result.Errors.Any(error =>
                error.Code is nameof(IdentityErrorDescriber.DuplicateEmail)
                    or nameof(IdentityErrorDescriber.DuplicateUserName)))
        {
            return AuthenticationProblem(
                StatusCodes.Status409Conflict,
                "Email already registered",
                "An account cannot be created with the supplied email address.",
                "EMAIL_ALREADY_REGISTERED");
        }

        return AuthenticationProblem(
            StatusCodes.Status400BadRequest,
            "Invalid registration",
            "The registration details do not satisfy the account policy.",
            "INVALID_REGISTRATION",
            new Dictionary<string, object?>
            {
                ["errors"] = result.Errors.Select(error => error.Description).ToArray()
            });
    }

    [AllowAnonymous]
    [EnableRateLimiting(AuthenticationRateLimitOptions.PolicyName)]
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        string? email = NormalizeEmailInput(request.Email);
        if (email is null || string.IsNullOrWhiteSpace(request.Password))
        {
            return InvalidCredentials();
        }

        ApplicationUser? user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            return InvalidCredentials();
        }

        Microsoft.AspNetCore.Identity.SignInResult result =
            await signInManager.CheckPasswordSignInAsync(
                user,
                request.Password,
                lockoutOnFailure: true);

        return result.Succeeded
            ? Ok(jwtTokenService.CreateAccessToken(user))
            : InvalidCredentials();
    }

    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    [HttpGet("me")]
    public IActionResult GetCurrentUser()
    {
        string? subject = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        string? email = User.FindFirstValue(JwtRegisteredClaimNames.Email);

        if (!Guid.TryParse(subject, out Guid userId) || string.IsNullOrWhiteSpace(email))
        {
            return Unauthorized();
        }

        return Ok(new CurrentUserResponse(userId, email));
    }

    private IActionResult InvalidCredentials() => AuthenticationProblem(
        StatusCodes.Status401Unauthorized,
        "Invalid credentials",
        "The supplied credentials are invalid.",
        "INVALID_CREDENTIALS");

    private static string? NormalizeEmailInput(string? email)
    {
        string? trimmedEmail = email?.Trim();
        return trimmedEmail is { Length: > 0 and <= MaximumEmailLength }
            && new EmailAddressAttribute().IsValid(trimmedEmail)
                ? trimmedEmail
                : null;
    }

    private ObjectResult AuthenticationProblem(
        int statusCode,
        string title,
        string detail,
        string code,
        IDictionary<string, object?>? additionalExtensions = null)
    {
        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = HttpContext.Request.Path
        };
        problemDetails.Extensions["code"] = code;

        if (additionalExtensions is not null)
        {
            foreach ((string key, object? value) in additionalExtensions)
            {
                problemDetails.Extensions[key] = value;
            }
        }

        var result = new ObjectResult(problemDetails)
        {
            StatusCode = statusCode
        };
        result.ContentTypes.Add("application/problem+json");
        return result;
    }
}
