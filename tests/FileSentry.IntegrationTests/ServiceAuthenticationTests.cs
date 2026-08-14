using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FileSentry.Api.Domain.Auditing;
using FileSentry.Api.Domain.Files;
using FileSentry.Api.Infrastructure.Storage;
using FileSentry.Api.Persistence;
using FileSentry.Api.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FileSentry.IntegrationTests;

[Collection(AuthenticationApiCollection.Name)]
public sealed class ServiceAuthenticationTests(AuthenticationApiFactory factory)
{
    [Fact]
    public async Task ServiceCredential_MissingInvalidOrCombinedWithBadBearer_FailsGenerically()
    {
        using HttpClient missingClient = CreateClient();
        using HttpClient invalidClient = CreateClient();
        invalidClient.DefaultRequestHeaders.Add(
            ServiceAuthenticationDefaults.HeaderName,
            "invalid-service-credential-with-sufficient-length");
        using HttpClient ambiguousClient = CreateServiceClient();
        ambiguousClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "invalid-token");

        HttpResponseMessage missing = await missingClient.GetAsync("/api/v1/files");
        HttpResponseMessage invalid = await invalidClient.GetAsync("/api/v1/files");
        HttpResponseMessage ambiguous = await ambiguousClient.GetAsync("/api/v1/files");

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, ambiguous.StatusCode);
        Assert.Equal(
            await missing.Content.ReadAsStringAsync(),
            await invalid.Content.ReadAsStringAsync());
        await AssertCredentialNotExposedAsync(missing, invalid, ambiguous);
    }

    [Fact]
    public async Task ServiceCredential_UsesOwnerScopedFileLifecycleWithoutAuthManagementAccess()
    {
        using HttpClient serviceClient = CreateServiceClient();
        using var multipart = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(TestFileContent.Pdf());
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        multipart.Add(fileContent, "file", "service-upload.pdf");

        HttpResponseMessage upload = await serviceClient.PostAsync("/api/v1/files", multipart);
        Assert.Equal(HttpStatusCode.Accepted, upload.StatusCode);
        using JsonDocument uploadBody = JsonDocument.Parse(
            await upload.Content.ReadAsStringAsync());
        Guid uploadedFileId = uploadBody.RootElement.GetProperty("fileId").GetGuid();

        using IServiceScope scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FileSentryDbContext>();
        FileRecord persistedUpload = await dbContext.FileRecords
            .AsNoTracking()
            .SingleAsync(record => record.Id == uploadedFileId);
        Assert.Equal(factory.ServiceId, persistedUpload.OwnerId);
        Assert.Equal(FileRecordStatus.PendingScan, persistedUpload.Status);

        HttpResponseMessage metadata = await serviceClient.GetAsync(
            $"/api/v1/files/{uploadedFileId}");
        HttpResponseMessage list = await serviceClient.GetAsync("/api/v1/files");
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        (HttpClient jwtClient, Guid jwtUploadId) = await CreateJwtClientWithUploadAsync();
        HttpResponseMessage deleteJwtUpload;
        using (jwtClient)
        {
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await jwtClient.GetAsync($"/api/v1/files/{uploadedFileId}")).StatusCode);
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await serviceClient.GetAsync($"/api/v1/files/{jwtUploadId}")).StatusCode);
            deleteJwtUpload = await jwtClient.DeleteAsync($"/api/v1/files/{jwtUploadId}");
            Assert.Equal(HttpStatusCode.NoContent, deleteJwtUpload.StatusCode);
        }

        (FileRecord cleanRecord, byte[] cleanBytes) = await CreateCleanServiceFileAsync();
        HttpResponseMessage download = await serviceClient.GetAsync(
            $"/api/v1/files/{cleanRecord.Id}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(cleanBytes, await download.Content.ReadAsByteArrayAsync());

        HttpResponseMessage deleteClean = await serviceClient.DeleteAsync(
            $"/api/v1/files/{cleanRecord.Id}");
        HttpResponseMessage deletePending = await serviceClient.DeleteAsync(
            $"/api/v1/files/{uploadedFileId}");
        HttpResponseMessage currentUser = await serviceClient.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.NoContent, deleteClean.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deletePending.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, currentUser.StatusCode);

        List<AuditEvent> serviceAuditEvents = await dbContext.AuditEvents
            .AsNoTracking()
            .Where(auditEvent => auditEvent.ActorUserId == factory.ServiceId)
            .ToListAsync();
        Assert.Contains(
            serviceAuditEvents,
            auditEvent => auditEvent.EventType == AuditEventType.UploadAccepted
                && auditEvent.FileRecordId == uploadedFileId);
        Assert.Contains(
            serviceAuditEvents,
            auditEvent => auditEvent.EventType == AuditEventType.FileDownloaded
                && auditEvent.FileRecordId == cleanRecord.Id);
        Assert.Contains(
            serviceAuditEvents,
            auditEvent => auditEvent.EventType == AuditEventType.FileDeleted
                && auditEvent.FileRecordId == cleanRecord.Id);

        await AssertCredentialNotExposedAsync(
            upload,
            metadata,
            list,
            download,
            deleteClean,
            deletePending,
            deleteJwtUpload,
            currentUser);
        Assert.DoesNotContain(
            factory.LogMessages,
            message => message.Contains(factory.ServiceApiKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProvisionedServiceIdentity_IsReservedAndHasNoPassword()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FileSentryDbContext>();

        ApplicationUser serviceUser = await dbContext.Users
            .AsNoTracking()
            .SingleAsync(user => user.Id == factory.ServiceId);

        Assert.Equal(
            ServiceIdentityProvisioner.GetReservedUserName(factory.ServiceId),
            serviceUser.UserName);
        Assert.Null(serviceUser.PasswordHash);
    }

    private HttpClient CreateClient() => factory.CreateClient(
        new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

    private HttpClient CreateServiceClient()
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Add(
            ServiceAuthenticationDefaults.HeaderName,
            factory.ServiceApiKey);
        return client;
    }

    private async Task<(HttpClient Client, Guid UploadedFileId)> CreateJwtClientWithUploadAsync()
    {
        using HttpClient anonymousClient = CreateClient();
        string email = $"service-auth-{Guid.NewGuid():N}@example.test";
        const string password = "ServiceAuth!Test9";
        var credentials = new { email, password };
        HttpResponseMessage registration = await anonymousClient.PostAsJsonAsync(
            "/api/v1/auth/register",
            credentials);
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        HttpResponseMessage login = await anonymousClient.PostAsJsonAsync(
            "/api/v1/auth/login",
            credentials);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using JsonDocument loginBody = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        string token = loginBody.RootElement.GetProperty("accessToken").GetString()!;

        HttpClient jwtClient = CreateClient();
        jwtClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        using var multipart = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(TestFileContent.Pdf());
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        multipart.Add(fileContent, "file", "jwt-owned.pdf");
        HttpResponseMessage upload = await jwtClient.PostAsync("/api/v1/files", multipart);
        Assert.Equal(HttpStatusCode.Accepted, upload.StatusCode);
        using JsonDocument uploadBody = JsonDocument.Parse(await upload.Content.ReadAsStringAsync());
        return (jwtClient, uploadBody.RootElement.GetProperty("fileId").GetGuid());
    }

    private async Task<(FileRecord Record, byte[] Content)> CreateCleanServiceFileAsync()
    {
        byte[] content = TestFileContent.Pdf();
        using IServiceScope scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FileSentryDbContext>();
        var storagePaths = scope.ServiceProvider.GetRequiredService<StoragePathProvider>();
        Guid fileId = Guid.NewGuid();
        string storageName = storagePaths.CreateStorageName(fileId);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var record = new FileRecord
        {
            Id = fileId,
            OwnerId = factory.ServiceId,
            OriginalFileName = "service-clean.pdf",
            StorageName = storageName,
            StorageState = FileStorageState.Clean,
            SizeBytes = content.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            DetectedMediaType = "application/pdf",
            CorrelationId = Guid.NewGuid().ToString("N"),
            Status = FileRecordStatus.Clean,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        dbContext.FileRecords.Add(record);
        await dbContext.SaveChangesAsync();
        await File.WriteAllBytesAsync(storagePaths.GetCleanPath(storageName), content);
        return (record, content);
    }

    private async Task AssertCredentialNotExposedAsync(params HttpResponseMessage[] responses)
    {
        foreach (HttpResponseMessage response in responses)
        {
            Assert.DoesNotContain(
                factory.ServiceApiKey,
                await response.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                response.Headers.SelectMany(header => header.Value),
                value => value.Contains(factory.ServiceApiKey, StringComparison.Ordinal));
            Assert.DoesNotContain(
                response.Content.Headers.SelectMany(header => header.Value),
                value => value.Contains(factory.ServiceApiKey, StringComparison.Ordinal));
        }
    }
}
