using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileSentry.Api.Application.Scanning;
using FileSentry.Api.Contracts.Files;
using FileSentry.Api.Domain.Auditing;
using FileSentry.Api.Domain.Files;
using FileSentry.Api.Domain.Scanning;
using FileSentry.Api.Infrastructure.Scanning;
using FileSentry.Api.Infrastructure.Storage;
using FileSentry.Api.Persistence;
using FileSentry.Api.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace FileSentry.IntegrationTests;

[Collection(AuthenticationApiCollection.Name)]
public sealed class FileAccessEndpointsTests(AuthenticationApiFactory factory)
{
    [Theory]
    [InlineData("GET", "/api/v1/files")]
    [InlineData("GET", "/api/v1/files/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "/api/v1/files/00000000-0000-0000-0000-000000000001/download")]
    [InlineData("DELETE", "/api/v1/files/00000000-0000-0000-0000-000000000001")]
    public async Task FileAccessEndpoints_RequireAuthentication(string method, string path)
    {
        using HttpClient client = CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_ReturnsOnlyOwnedSafeMetadataNewestFirst()
    {
        (Guid ownerId, string token) = await CreateUserAsync();
        (Guid otherOwnerId, _) = await CreateUserAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        FileRecord older = await CreateFileAsync(
            ownerId,
            FileRecordStatus.PendingScan,
            "older.pdf",
            TestFileContent.Pdf(),
            now.AddMinutes(-2));
        FileRecord newer = await CreateFileAsync(
            ownerId,
            FileRecordStatus.Clean,
            "newer.pdf",
            TestFileContent.Pdf(),
            now.AddMinutes(-1));
        _ = await CreateFileAsync(
            otherOwnerId,
            FileRecordStatus.Clean,
            "other.pdf",
            TestFileContent.Pdf(),
            now);
        using HttpClient client = CreateAuthenticatedClient(token);

        HttpResponseMessage response = await client.GetAsync("/api/v1/files");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        List<FileMetadataResponse>? files =
            await response.Content.ReadFromJsonAsync<List<FileMetadataResponse>>();
        Assert.NotNull(files);
        Assert.Equal([newer.Id, older.Id], files.Select(file => file.FileId));
        Assert.All(files, file => Assert.Equal(ownerId, file.FileId == newer.Id
            ? newer.OwnerId
            : older.OwnerId));
        string json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("storageName", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storageState", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ownerId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scanAttempt", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Metadata_OwnedFileSucceedsWithSafeFields()
    {
        (Guid ownerId, string token) = await CreateUserAsync();
        FileRecord record = await CreateFileAsync(
            ownerId,
            FileRecordStatus.Clean,
            "report.pdf",
            TestFileContent.Pdf());
        using HttpClient client = CreateAuthenticatedClient(token);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/files/{record.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        FileMetadataResponse? metadata =
            await response.Content.ReadFromJsonAsync<FileMetadataResponse>();
        Assert.NotNull(metadata);
        Assert.Equal(record.Id, metadata.FileId);
        Assert.Equal(record.OriginalFileName, metadata.OriginalFileName);
        Assert.Equal(record.Sha256, metadata.Sha256);
        Assert.Equal(nameof(FileRecordStatus.Clean), metadata.Status);
        AssertTimestampClose(record.CreatedAtUtc, metadata.CreatedAtUtc);
        AssertTimestampClose(record.UpdatedAtUtc, metadata.UpdatedAtUtc);
    }

    [Fact]
    public async Task Metadata_CrossOwnerAndMissingUseSameNotFoundResponse()
    {
        (Guid ownerId, string token) = await CreateUserAsync();
        (Guid otherOwnerId, _) = await CreateUserAsync();
        FileRecord other = await CreateFileAsync(
            otherOwnerId,
            FileRecordStatus.Clean,
            "private.pdf",
            TestFileContent.Pdf());
        using HttpClient client = CreateAuthenticatedClient(token);

        HttpResponseMessage crossOwner = await client.GetAsync($"/api/v1/files/{other.Id}");
        HttpResponseMessage missing = await client.GetAsync($"/api/v1/files/{Guid.NewGuid()}");

        await AssertSameNotFoundAsync(crossOwner, missing);
        Assert.NotEqual(ownerId, other.OwnerId);
    }

    [Fact]
    public async Task Download_CleanOwnedFileStreamsVerifiedContentWithSafeHeaders()
    {
        (Guid ownerId, string token) = await CreateUserAsync();
        byte[] content = TestFileContent.Pdf();
        FileRecord record = await CreateFileAsync(
            ownerId,
            FileRecordStatus.Clean,
            "../../report\r\nX-Evil: injected.pdf",
            content);
        using HttpClient client = CreateAuthenticatedClient(token);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/files/{record.Id}/download");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(record.DetectedMediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(content, await response.Content.ReadAsByteArrayAsync());
        ContentDispositionHeaderValue? disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition.DispositionType);
        string rawDisposition = disposition.ToString();
        Assert.DoesNotContain("\r", rawDisposition, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", rawDisposition, StringComparison.Ordinal);
        Assert.DoesNotContain("../", rawDisposition, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Evil:", rawDisposition, StringComparison.Ordinal);
        Assert.Contains("report", rawDisposition, StringComparison.OrdinalIgnoreCase);
        AuditEvent auditEvent = Assert.Single(await LoadAuditEventsAsync(
            record.Id,
            AuditEventType.FileDownloaded));
        Assert.Equal(ownerId, auditEvent.ActorUserId);
        Assert.Equal(
            response.Headers.GetValues("X-Correlation-ID").Single(),
            auditEvent.CorrelationId);
    }

    [Fact]
    public async Task Download_CrossOwnerCleanFileReturnsNotFound()
    {
        (_, string token) = await CreateUserAsync();
        (Guid otherOwnerId, _) = await CreateUserAsync();
        FileRecord record = await CreateFileAsync(
            otherOwnerId,
            FileRecordStatus.Clean,
            "private.pdf",
            TestFileContent.Pdf());
        using HttpClient client = CreateAuthenticatedClient(token);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/files/{record.Id}/download");

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "FILE_NOT_FOUND");
    }

    [Theory]
    [InlineData(FileRecordStatus.PendingScan)]
    [InlineData(FileRecordStatus.Scanning)]
    [InlineData(FileRecordStatus.Infected)]
    [InlineData(FileRecordStatus.ScanFailed)]
    [InlineData(FileRecordStatus.Deleted)]
    public async Task Download_NonCleanStateReturnsStableProblem(FileRecordStatus status)
    {
        (Guid ownerId, string token) = await CreateUserAsync();
        FileRecord record = await CreateFileAsync(
            ownerId,
            status,
            "not-clean.pdf",
            TestFileContent.Pdf());
        using HttpClient client = CreateAuthenticatedClient(token);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/files/{record.Id}/download");

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "FILE_NOT_CLEAN");
    }

    [Fact]
    public async Task Download_CleanStateWithMissingBytesFailsSafely()
    {
        (Guid ownerId, string token) = await CreateUserAsync();
        FileRecord record = await CreateFileAsync(
            ownerId,
            FileRecordStatus.Clean,
            "missing.pdf",
            TestFileContent.Pdf(),
            storeBytes: false);
        using HttpClient client = CreateAuthenticatedClient(token);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/files/{record.Id}/download");

        await AssertProblemAsync(
            response,
            HttpStatusCode.InternalServerError,
            "FILE_CONTENT_UNAVAILABLE");
    }

    [Theory]
    [InlineData(FileRecordStatus.PendingScan)]
    [InlineData(FileRecordStatus.Clean)]
    public async Task Delete_OwnedFileRemovesBytesAndPersistsDeletedState(FileRecordStatus status)
    {
        (Guid ownerId, string token) = await CreateUserAsync();
        FileRecord record = await CreateFileAsync(
            ownerId,
            status,
            "delete.pdf",
            TestFileContent.Pdf());
        string quarantinePath = Path.Combine(factory.QuarantineRootPath, record.StorageName);
        string cleanPath = Path.Combine(factory.CleanRootPath, record.StorageName);
        using HttpClient client = CreateAuthenticatedClient(token);

        HttpResponseMessage response = await client.DeleteAsync($"/api/v1/files/{record.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        FileRecord persisted = await LoadFileAsync(record.Id);
        Assert.Equal(FileRecordStatus.Deleted, persisted.Status);
        Assert.Equal(FileStorageState.Deleted, persisted.StorageState);
        Assert.Null(persisted.ActiveScanAttemptId);
        Assert.False(File.Exists(quarantinePath));
        Assert.False(File.Exists(cleanPath));
        AuditEvent auditEvent = Assert.Single(await LoadAuditEventsAsync(
            record.Id,
            AuditEventType.FileDeleted));
        Assert.Equal(ownerId, auditEvent.ActorUserId);
        Assert.Equal(status, auditEvent.PreviousStatus);
        Assert.Equal(FileRecordStatus.Deleted, auditEvent.NewStatus);

        HttpResponseMessage download = await client.GetAsync(
            $"/api/v1/files/{record.Id}/download");
        await AssertProblemAsync(download, HttpStatusCode.Conflict, "FILE_NOT_CLEAN");
    }

    [Fact]
    public async Task Delete_CrossOwnerReturnsNotFoundAndPreservesBytes()
    {
        (_, string token) = await CreateUserAsync();
        (Guid otherOwnerId, _) = await CreateUserAsync();
        FileRecord record = await CreateFileAsync(
            otherOwnerId,
            FileRecordStatus.Clean,
            "private.pdf",
            TestFileContent.Pdf());
        string cleanPath = Path.Combine(factory.CleanRootPath, record.StorageName);
        using HttpClient client = CreateAuthenticatedClient(token);

        HttpResponseMessage response = await client.DeleteAsync($"/api/v1/files/{record.Id}");

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "FILE_NOT_FOUND");
        Assert.True(File.Exists(cleanPath));
        Assert.Equal(FileRecordStatus.Clean, (await LoadFileAsync(record.Id)).Status);
    }

    [Fact]
    public async Task Delete_WhileScanningPreventsLaterCleanFinalization()
    {
        (Guid ownerId, string token) = await CreateUserAsync();
        Guid attemptId = Guid.NewGuid();
        FileRecord record = await CreateFileAsync(
            ownerId,
            FileRecordStatus.Scanning,
            "racing.pdf",
            TestFileContent.Pdf(),
            activeAttemptId: attemptId);
        using HttpClient client = CreateAuthenticatedClient(token);
        using IServiceScope scope = factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<FileScanWorkflowService>();
        var job = new FileScanJob(
            record.Id,
            attemptId,
            1,
            record.StorageName,
            record.SizeBytes,
            record.CorrelationId ?? Guid.NewGuid().ToString("N"));

        HttpResponseMessage deleted = await client.DeleteAsync($"/api/v1/files/{record.Id}");
        bool finalized = await workflow.CompleteAndFinalizeAsync(
            job,
            ClamAvScanResult.Clean(),
            default);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(finalized);
        FileRecord persisted = await LoadFileAsync(record.Id);
        Assert.Equal(FileRecordStatus.Deleted, persisted.Status);
        Assert.Equal(FileStorageState.Deleted, persisted.StorageState);
        Assert.False(File.Exists(Path.Combine(factory.QuarantineRootPath, record.StorageName)));
        Assert.False(File.Exists(Path.Combine(factory.CleanRootPath, record.StorageName)));
    }

    private HttpClient CreateClient() => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost")
    });

    private HttpClient CreateAuthenticatedClient(string token)
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<(Guid UserId, string Token)> CreateUserAsync()
    {
        Guid userId = Guid.NewGuid();
        string email = $"access-{userId:N}@example.com";
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

    private async Task<FileRecord> CreateFileAsync(
        Guid ownerId,
        FileRecordStatus status,
        string originalFileName,
        byte[] content,
        DateTimeOffset? createdAtUtc = null,
        bool storeBytes = true,
        Guid? activeAttemptId = null)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FileSentryDbContext>();
        var storagePaths = scope.ServiceProvider.GetRequiredService<StoragePathProvider>();
        DateTimeOffset created = createdAtUtc ?? DateTimeOffset.UtcNow;
        Guid fileId = Guid.NewGuid();
        string storageName = storagePaths.CreateStorageName(fileId);
        FileStorageState storageState = status switch
        {
            FileRecordStatus.Clean => FileStorageState.Clean,
            FileRecordStatus.Infected or FileRecordStatus.Deleted => FileStorageState.Deleted,
            _ => FileStorageState.Quarantine
        };
        var record = new FileRecord
        {
            Id = fileId,
            OwnerId = ownerId,
            OriginalFileName = originalFileName,
            StorageName = storageName,
            StorageState = storageState,
            SizeBytes = content.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            DetectedMediaType = "application/pdf",
            CorrelationId = Guid.NewGuid().ToString("N"),
            Status = status,
            ScanAttemptCount = activeAttemptId.HasValue ? 1 : 0,
            ActiveScanAttemptId = activeAttemptId,
            ScanningStartedAtUtc = activeAttemptId.HasValue ? created : null,
            CreatedAtUtc = created,
            UpdatedAtUtc = created
        };
        dbContext.FileRecords.Add(record);
        if (activeAttemptId is Guid attemptId)
        {
            dbContext.ScanAttempts.Add(new ScanAttempt
            {
                Id = attemptId,
                FileRecordId = fileId,
                AttemptNumber = 1,
                StartedAtUtc = created,
                Result = ScanAttemptResult.InProgress
            });
        }

        await dbContext.SaveChangesAsync();
        if (storeBytes && storageState != FileStorageState.Deleted)
        {
            string path = storageState == FileStorageState.Clean
                ? storagePaths.GetCleanPath(storageName)
                : storagePaths.GetQuarantinePath(storageName);
            await File.WriteAllBytesAsync(path, content);
        }

        return record;
    }

    private async Task<FileRecord> LoadFileAsync(Guid fileId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FileSentryDbContext>();
        return await dbContext.FileRecords
            .AsNoTracking()
            .SingleAsync(record => record.Id == fileId);
    }

    private async Task<List<AuditEvent>> LoadAuditEventsAsync(
        Guid fileId,
        AuditEventType eventType)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FileSentryDbContext>();
        return await dbContext.AuditEvents
            .AsNoTracking()
            .Where(auditEvent => auditEvent.FileRecordId == fileId
                && auditEvent.EventType == eventType)
            .ToListAsync();
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

    private static async Task AssertSameNotFoundAsync(
        HttpResponseMessage first,
        HttpResponseMessage second)
    {
        Assert.Equal(HttpStatusCode.NotFound, first.StatusCode);
        Assert.Equal(first.StatusCode, second.StatusCode);
        using JsonDocument firstProblem = JsonDocument.Parse(
            await first.Content.ReadAsStringAsync());
        using JsonDocument secondProblem = JsonDocument.Parse(
            await second.Content.ReadAsStringAsync());
        Assert.Equal(
            firstProblem.RootElement.GetProperty("code").GetString(),
            secondProblem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            firstProblem.RootElement.GetProperty("title").GetString(),
            secondProblem.RootElement.GetProperty("title").GetString());
        Assert.Equal(
            firstProblem.RootElement.GetProperty("detail").GetString(),
            secondProblem.RootElement.GetProperty("detail").GetString());
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

    private static void AssertTimestampClose(DateTimeOffset expected, DateTimeOffset actual)
    {
        Assert.InRange(
            (actual - expected).Duration(),
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(1));
    }
}
