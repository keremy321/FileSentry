using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FileSentry.Api.Contracts.Authentication;
using FileSentry.Api.Infrastructure.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace FileSentry.Api.Security;

public sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider timeProvider)
{
    public AuthenticationResponse CreateAccessToken(ApplicationUser user)
    {
        JwtOptions configuredOptions = options.Value;
        DateTimeOffset issuedAtUtc = timeProvider.GetUtcNow();
        DateTimeOffset expiresAtUtc = issuedAtUtc.AddMinutes(
            configuredOptions.AccessTokenLifetimeMinutes);

        Claim[] claims =
        [
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Iat,
                EpochTime.GetIntDate(issuedAtUtc.UtcDateTime).ToString(),
                ClaimValueTypes.Integer64)
        ];

        var signingKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(configuredOptions.SigningKey));
        var token = new JwtSecurityToken(
            issuer: configuredOptions.Issuer,
            audience: configuredOptions.Audience,
            claims: claims,
            notBefore: issuedAtUtc.UtcDateTime,
            expires: expiresAtUtc.UtcDateTime,
            signingCredentials: new SigningCredentials(
                signingKey,
                SecurityAlgorithms.HmacSha256));

        return new AuthenticationResponse(
            new JwtSecurityTokenHandler().WriteToken(token),
            "Bearer",
            expiresAtUtc);
    }
}
