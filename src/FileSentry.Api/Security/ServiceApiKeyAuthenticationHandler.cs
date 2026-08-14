using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using FileSentry.Api.Infrastructure.Options;
using FileSentry.Api.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace FileSentry.Api.Security;

public sealed class ServiceApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IOptions<ServiceAuthenticationOptions> serviceOptions,
    FileSentryDbContext dbContext)
    : AuthenticationHandler<AuthenticationSchemeOptions>(
        schemeOptions,
        loggerFactory,
        encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        ServiceAuthenticationOptions configuredOptions = serviceOptions.Value;
        if (!configuredOptions.Enabled)
        {
            return AuthenticateResult.NoResult();
        }

        StringValues suppliedValues = Request.Headers[ServiceAuthenticationDefaults.HeaderName];
        if (suppliedValues.Count == 0)
        {
            return AuthenticateResult.NoResult();
        }

        string? suppliedKey = suppliedValues.Count == 1 ? suppliedValues[0] : null;
        if (!IsValidCredential(suppliedKey, configuredOptions.ApiKey))
        {
            return AuthenticateResult.Fail("Invalid service credential.");
        }

        string reservedUserName = ServiceIdentityProvisioner.GetReservedUserName(
            configuredOptions.ServiceId);
        bool identityExists = await dbContext.Users
            .AsNoTracking()
            .AnyAsync(
                user => user.Id == configuredOptions.ServiceId
                    && user.UserName == reservedUserName
                    && user.PasswordHash == null,
                Context.RequestAborted);
        if (!identityExists)
        {
            return AuthenticateResult.Fail("Invalid service credential.");
        }

        Claim[] claims =
        [
            new(JwtRegisteredClaimNames.Sub, configuredOptions.ServiceId.ToString()),
            new(ClaimTypes.NameIdentifier, configuredOptions.ServiceId.ToString()),
            new(ClaimTypes.Name, configuredOptions.ServiceName),
            new(ServiceAuthenticationDefaults.ActorTypeClaim,
                ServiceAuthenticationDefaults.ActorType)
        ];
        var identity = new ClaimsIdentity(claims, ServiceAuthenticationDefaults.Scheme);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(
            principal,
            ServiceAuthenticationDefaults.Scheme));
    }

    private static bool IsValidCredential(string? suppliedKey, string configuredKey)
    {
        if (suppliedKey is null
            || suppliedKey.Length > ServiceAuthenticationOptions.MaximumApiKeyLength)
        {
            return false;
        }

        byte[] suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedKey));
        byte[] configuredHash = SHA256.HashData(Encoding.UTF8.GetBytes(configuredKey));
        try
        {
            return CryptographicOperations.FixedTimeEquals(suppliedHash, configuredHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(suppliedHash);
            CryptographicOperations.ZeroMemory(configuredHash);
        }
    }
}
