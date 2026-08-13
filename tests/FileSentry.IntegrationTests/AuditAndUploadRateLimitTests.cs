using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileSentry.Api.Domain.Auditing;
using FileSentry.Api.Domain.Files;
using FileSentry.Api.Infrastructure.Correlation;
using FileSentry.Api.Persistence;
using FileSentry.Api.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace FileSentry.IntegrationTests;

[Collection(AuthenticationApiCollection.Name)]
public sealed class AuditAndUploadRateLimitTests(AuthenticationApiFactory factory)
{
    [Fact]
    public async Task UploadAccepted_PersistsSafeActorFileAndCorrelationAuditData()
    {
        (Guid ownerId, string token) = await CreateUserAsync();
        const string correlationId = "audit-upload-correlation-001";
        byte[] content = TestFileContent.Pdf();
        using HttpClient client = CreateClient(factory, token);

        HttpResponseMessage response = await UploadAsync(
            client,
            "audit.pdf",
            content,
            correlationId);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(
            correlationId,
            response.Headers.GetValues(CorrelationIdPolicy.HeaderName).Single());
        Guid fileId = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("fileId").GetGuid();
        using IServiceScope scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FileSentryDbContext>();
        AuditEvent auditEvent = await dbContext.AuditEvents
            .AsNoTracking()
            .SingleAsync(candidate => candidate.CorrelationId == correlationId);
        FileRecord record = await dbContext.FileRecords
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == fileId);

        Assert.Equal(AuditEventType.UploadAccepted, auditEvent.EventType);
        Assert.Equal(ownerId, auditEvent.ActorUserId);
        Assert.Equal(fileId, auditEvent.FileRecordId);
        Assert.Equal(FileRecordStatus.PendingScan, auditEvent.NewStatus);
        Assert.Equal(correlationId, record.CorrelationId);
        Assert.InRange(
            auditEvent.OccurredAtUtc,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(1));

        string serializedAudit = JsonSerializer.Serialize(auditEvent);
        Assert.DoesNotContain("audit.pdf", serializedAudit, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(content), serializedAudit, StringComparison.Ordinal);
        Assert.DoesNotContain(record.Sha256, serializedAudit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", serializedAudit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", serializedAudit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("content", serializedAudit, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidIncomingCorrelationId_IsReplacedBeforePersistence()
    {
        (_, string token) = await CreateUserAsync();
        using HttpClient client = CreateClient(factory, token);

        HttpResponseMessage response = await UploadAsync(
            client,
            "correlation.pdf",
            TestFileContent.Pdf(),
            "invalid correlation with spaces");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        string effectiveCorrelationId = response.Headers
            .GetValues(CorrelationIdPolicy.HeaderName)
            .Single();
        Assert.True(CorrelationIdPolicy.IsValid(effectiveCorrelationId));
        Assert.NotEqual("invalid correlation with spaces", effectiveCorrelationId);
        using IServiceScope scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FileSentryDbContext>();
        Assert.True(await dbContext.AuditEvents.AnyAsync(
            candidate => candidate.CorrelationId == effectiveCorrelationId));
    }

    [Fact]
    public async Task UploadRateLimit_AllowsConfiguredNormalUsageThenReturnsStableProblem()
    {
        using WebApplicationFactory<Program> limitedFactory = CreateLimitedFactory(permitLimit: 2);
        (Guid ownerId, string token) = await CreateUserAsync();
        using HttpClient client = CreateClient(limitedFactory, token);

        Assert.Equal(
            HttpStatusCode.Accepted,
            (await UploadAsync(client, "one.pdf", TestFileContent.Pdf())).StatusCode);
        Assert.Equal(
            HttpStatusCode.Accepted,
            (await UploadAsync(client, "two.pdf", TestFileContent.Pdf())).StatusCode);
        HttpResponseMessage rejected = await UploadAsync(
            client,
            "three.pdf",
            TestFileContent.Pdf());

        await AssertProblemAsync(
            rejected,
            HttpStatusCode.TooManyRequests,
            "UPLOAD_RATE_LIMITED");
        using IServiceScope scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FileSentryDbContext>();
        Assert.Equal(
            2,
            await dbContext.AuditEvents.CountAsync(auditEvent =>
                auditEvent.ActorUserId == ownerId
                && auditEvent.EventType == AuditEventType.UploadAccepted));
    }

    [Fact]
    public async Task UploadRateLimit_IsPartitionedByAuthenticatedUser()
    {
        using WebApplicationFactory<Program> limitedFactory = CreateLimitedFactory(permitLimit: 1);
        (_, string firstToken) = await CreateUserAsync();
        (_, string secondToken) = await CreateUserAsync();
        using HttpClient firstClient = CreateClient(limitedFactory, firstToken);
        using HttpClient secondClient = CreateClient(limitedFactory, secondToken);

        Assert.Equal(
            HttpStatusCode.Accepted,
            (await UploadAsync(firstClient, "first.pdf", TestFileContent.Pdf())).StatusCode);
        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await UploadAsync(firstClient, "blocked.pdf", TestFileContent.Pdf())).StatusCode);
        Assert.Equal(
            HttpStatusCode.Accepted,
            (await UploadAsync(secondClient, "second.pdf", TestFileContent.Pdf())).StatusCode);
    }

    [Fact]
    public async Task UploadRateLimit_DoesNotReplaceAuthenticationRequirement()
    {
        using WebApplicationFactory<Program> limitedFactory = CreateLimitedFactory(permitLimit: 1);
        using HttpClient client = CreateClient(limitedFactory);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            HttpResponseMessage response = await UploadAsync(
                client,
                "unauthorized.pdf",
                TestFileContent.Pdf());
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    private WebApplicationFactory<Program> CreateLimitedFactory(int permitLimit) =>
        factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["UploadRateLimit:PermitLimit"] = permitLimit.ToString(),
                    ["UploadRateLimit:WindowSeconds"] = "60"
                })));

    private async Task<(Guid UserId, string Token)> CreateUserAsync()
    {
        Guid userId = Guid.NewGuid();
        string email = $"audit-{userId:N}@example.com";
        using IServiceScope scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FileSentryDbContext>();
        dbContext.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();
        return (userId, CreateToken(userId));
    }

    private static HttpClient CreateClient(
        WebApplicationFactory<Program> applicationFactory,
        string? token = null)
    {
        HttpClient client = applicationFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        string fileName,
        byte[] content,
        string? correlationId = null)
    {
        using var multipart = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        multipart.Add(fileContent, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/files")
        {
            Content = multipart
        };
        if (correlationId is not null)
        {
            request.Headers.TryAddWithoutValidation(CorrelationIdPolicy.HeaderName, correlationId);
        }

        return await client.SendAsync(request);
    }

    private string CreateToken(Guid ownerId)
    {
        var token = new JwtSecurityToken(
            factory.Issuer,
            factory.Audience,
            [new Claim(JwtRegisteredClaimNames.Sub, ownerId.ToString())],
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(factory.SigningKey)),
                SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedCode, problem.RootElement.GetProperty("code").GetString());
    }
}
