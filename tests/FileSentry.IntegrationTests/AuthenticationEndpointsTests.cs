using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileSentry.Api.Contracts.Authentication;
using FileSentry.Api.Persistence;
using FileSentry.Api.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace FileSentry.IntegrationTests;

[Collection(AuthenticationApiCollection.Name)]
public sealed class AuthenticationEndpointsTests(AuthenticationApiFactory factory)
{
    private const string ValidPassword = "Valid!Passw0rd";

    [Fact]
    public async Task Register_WithValidDetails_CreatesUserWithHashedPassword()
    {
        using HttpClient client = CreateClient();
        string email = CreateEmail();

        HttpResponseMessage response = await RegisterAsync(client, email, ValidPassword);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        CurrentUserResponse? registration =
            await response.Content.ReadFromJsonAsync<CurrentUserResponse>();
        Assert.NotNull(registration);
        Assert.Equal(email, registration.Email);

        using IServiceScope scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FileSentryDbContext>();
        ApplicationUser user = await dbContext.Users.SingleAsync(candidate => candidate.Email == email);
        Assert.NotNull(user.PasswordHash);
        Assert.NotEqual(ValidPassword, user.PasswordHash);

        var passwordHasher = scope.ServiceProvider
            .GetRequiredService<IPasswordHasher<ApplicationUser>>();
        Assert.NotEqual(
            PasswordVerificationResult.Failed,
            passwordHasher.VerifyHashedPassword(user, user.PasswordHash, ValidPassword));
    }

    [Fact]
    public async Task Register_WithInvalidEmail_ReturnsInvalidRegistrationProblem()
    {
        using HttpClient client = CreateClient();

        HttpResponseMessage response = await RegisterAsync(
            client,
            "not-an-email",
            ValidPassword);

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "INVALID_REGISTRATION");
    }

    [Fact]
    public async Task Register_WithWeakPassword_ReturnsInvalidRegistrationProblem()
    {
        using HttpClient client = CreateClient();

        HttpResponseMessage response = await RegisterAsync(
            client,
            CreateEmail(),
            "weak");

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "INVALID_REGISTRATION");
    }

    [Fact]
    public async Task Register_WithDuplicateEmailIgnoringCase_ReturnsConflict()
    {
        using HttpClient client = CreateClient();
        string email = CreateEmail();
        Assert.Equal(HttpStatusCode.Created, (await RegisterAsync(client, email, ValidPassword)).StatusCode);

        HttpResponseMessage duplicateResponse = await RegisterAsync(
            client,
            email.ToUpperInvariant(),
            ValidPassword);

        await AssertProblemAsync(
            duplicateResponse,
            HttpStatusCode.Conflict,
            "EMAIL_ALREADY_REGISTERED");
    }

    [Fact]
    public async Task Login_WithCorrectCredentials_ReturnsShortLivedJwtWithSafeClaims()
    {
        using HttpClient client = CreateClient();
        string email = CreateEmail();
        await RegisterAsync(client, email, ValidPassword);

        HttpResponseMessage response = await LoginAsync(client, email, ValidPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AuthenticationResponse? authentication =
            await response.Content.ReadFromJsonAsync<AuthenticationResponse>();
        Assert.NotNull(authentication);
        Assert.Equal("Bearer", authentication.TokenType);
        Assert.InRange(
            authentication.ExpiresAtUtc,
            DateTimeOffset.UtcNow.AddMinutes(14),
            DateTimeOffset.UtcNow.AddMinutes(16));

        JwtSecurityToken token = new JwtSecurityTokenHandler().ReadJwtToken(
            authentication.AccessToken);
        Assert.Equal(factory.Issuer, token.Issuer);
        Assert.Contains(factory.Audience, token.Audiences);
        Assert.Equal(email, token.Claims.Single(claim => claim.Type == JwtRegisteredClaimNames.Email).Value);
        Assert.True(Guid.TryParse(
            token.Claims.Single(claim => claim.Type == JwtRegisteredClaimNames.Sub).Value,
            out _));
        Assert.Contains(token.Claims, claim => claim.Type == JwtRegisteredClaimNames.Jti);
        Assert.DoesNotContain(token.Claims, claim =>
            claim.Type.Contains("password", StringComparison.OrdinalIgnoreCase)
            || claim.Type.Contains("securitystamp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Login_WithBadPasswordAndUnknownEmail_ReturnsSameExternalFailure()
    {
        using HttpClient client = CreateClient();
        string email = CreateEmail();
        await RegisterAsync(client, email, ValidPassword);

        HttpResponseMessage badPassword = await LoginAsync(client, email, "Wrong!Passw0rd");
        HttpResponseMessage unknownEmail = await LoginAsync(
            client,
            CreateEmail(),
            "Wrong!Passw0rd");

        Assert.Equal(HttpStatusCode.Unauthorized, badPassword.StatusCode);
        Assert.Equal(badPassword.StatusCode, unknownEmail.StatusCode);
        Assert.Equal(
            badPassword.Content.Headers.ContentType?.MediaType,
            unknownEmail.Content.Headers.ContentType?.MediaType);

        using JsonDocument badPasswordProblem = await JsonDocument.ParseAsync(
            await badPassword.Content.ReadAsStreamAsync());
        using JsonDocument unknownEmailProblem = await JsonDocument.ParseAsync(
            await unknownEmail.Content.ReadAsStreamAsync());
        Assert.Equal(
            badPasswordProblem.RootElement.GetProperty("code").GetString(),
            unknownEmailProblem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            badPasswordProblem.RootElement.GetProperty("title").GetString(),
            unknownEmailProblem.RootElement.GetProperty("title").GetString());
        Assert.Equal(
            badPasswordProblem.RootElement.GetProperty("detail").GetString(),
            unknownEmailProblem.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Login_RepeatedFailuresLockAccountWithoutChangingExternalFailure()
    {
        using HttpClient client = CreateClient();
        string email = CreateEmail();
        await RegisterAsync(client, email, ValidPassword);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            HttpResponseMessage failedResponse = await LoginAsync(
                client,
                email,
                "Wrong!Passw0rd");
            await AssertProblemAsync(
                failedResponse,
                HttpStatusCode.Unauthorized,
                "INVALID_CREDENTIALS");
        }

        HttpResponseMessage lockedResponse = await LoginAsync(client, email, ValidPassword);
        await AssertProblemAsync(
            lockedResponse,
            HttpStatusCode.Unauthorized,
            "INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task Me_WithValidToken_ReturnsAuthenticatedUser()
    {
        using HttpClient client = CreateClient();
        string email = CreateEmail();
        await RegisterAsync(client, email, ValidPassword);
        AuthenticationResponse authentication = await LoginSuccessfullyAsync(client, email);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            authentication.TokenType,
            authentication.AccessToken);
        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CurrentUserResponse? currentUser =
            await response.Content.ReadFromJsonAsync<CurrentUserResponse>();
        Assert.NotNull(currentUser);
        Assert.Equal(email, currentUser.Email);
        Assert.NotEqual(Guid.Empty, currentUser.UserId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-jwt")]
    public async Task Me_WithMissingOrMalformedToken_ReturnsUnauthorized(string? token)
    {
        using HttpClient client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_WithInvalidSignature_ReturnsUnauthorized()
    {
        string token = CreateToken(
            factory.Issuer,
            factory.Audience,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
            DateTime.UtcNow.AddMinutes(5));

        Assert.Equal(HttpStatusCode.Unauthorized, await GetMeStatusAsync(token));
    }

    [Fact]
    public async Task Me_WithExpiredToken_ReturnsUnauthorized()
    {
        string token = CreateToken(
            factory.Issuer,
            factory.Audience,
            factory.SigningKey,
            DateTime.UtcNow.AddMinutes(-2));

        Assert.Equal(HttpStatusCode.Unauthorized, await GetMeStatusAsync(token));
    }

    [Fact]
    public async Task Me_WithUnsignedToken_ReturnsUnauthorized()
    {
        string token = CreateToken(
            factory.Issuer,
            factory.Audience,
            signingKey: null,
            DateTime.UtcNow.AddMinutes(5));

        Assert.Equal(HttpStatusCode.Unauthorized, await GetMeStatusAsync(token));
    }

    [Theory]
    [InlineData("wrong-issuer", null)]
    [InlineData(null, "wrong-audience")]
    public async Task Me_WithInvalidIssuerOrAudience_ReturnsUnauthorized(
        string? issuer,
        string? audience)
    {
        string token = CreateToken(
            issuer ?? factory.Issuer,
            audience ?? factory.Audience,
            factory.SigningKey,
            DateTime.UtcNow.AddMinutes(5));

        Assert.Equal(HttpStatusCode.Unauthorized, await GetMeStatusAsync(token));
    }

    [Fact]
    public async Task AuthenticationEndpoints_AreRateLimitedWithStableProblemCode()
    {
        using WebApplicationFactory<Program> rateLimitedFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AuthenticationRateLimit:PermitLimit"] = "2"
                })));
        using HttpClient client = rateLimitedFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await LoginAsync(client, CreateEmail(), ValidPassword)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await LoginAsync(client, CreateEmail(), ValidPassword)).StatusCode);

        HttpResponseMessage rejected = await LoginAsync(client, CreateEmail(), ValidPassword);
        await AssertProblemAsync(
            rejected,
            HttpStatusCode.TooManyRequests,
            "AUTH_RATE_LIMITED");
    }

    [Fact]
    public async Task HealthEndpoints_RemainAnonymousAndReadyChecksDependencies()
    {
        using HttpClient client = CreateClient();

        HttpResponseMessage live = await client.GetAsync("/health/live");
        HttpResponseMessage ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("Healthy", await live.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync());
    }

    private HttpClient CreateClient() => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost")
    });

    private static string CreateEmail() => $"user-{Guid.NewGuid():N}@example.com";

    private static Task<HttpResponseMessage> RegisterAsync(
        HttpClient client,
        string email,
        string password) => client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterRequest(email, password));

    private static Task<HttpResponseMessage> LoginAsync(
        HttpClient client,
        string email,
        string password) => client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(email, password));

    private static async Task<AuthenticationResponse> LoginSuccessfullyAsync(
        HttpClient client,
        string email)
    {
        HttpResponseMessage response = await LoginAsync(client, email, ValidPassword);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AuthenticationResponse>()
            ?? throw new InvalidOperationException("Authentication response was empty.");
    }

    private async Task<HttpStatusCode> GetMeStatusAsync(string token)
    {
        using HttpClient client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await client.SendAsync(request)).StatusCode;
    }

    private static string CreateToken(
        string issuer,
        string audience,
        string? signingKey,
        DateTime expires)
    {
        DateTime notBefore = expires <= DateTime.UtcNow
            ? expires.AddMinutes(-5)
            : DateTime.UtcNow.AddMinutes(-1);
        SigningCredentials? credentials = signingKey is null
            ? null
            : new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer,
            audience,
            [
                new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
                new Claim(JwtRegisteredClaimNames.Email, CreateEmail())
            ],
            notBefore,
            expires,
            credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument problem = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync());
        Assert.Equal(expectedCode, problem.RootElement.GetProperty("code").GetString());
    }
}
