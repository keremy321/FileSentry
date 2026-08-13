using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileSentry.Api.Application.Files;
using FileSentry.Api.Contracts.Authentication;
using FileSentry.Api.Contracts.Files;
using FileSentry.Api.Domain.Files;
using FileSentry.Api.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace FileSentry.IntegrationTests;

[Collection(AuthenticationApiCollection.Name)]
public sealed class FileIngestionEndpointsTests(AuthenticationApiFactory factory)
{
    private const string ValidPassword = "Valid!Passw0rd";

    [Theory]
    [InlineData("sample.pdf", "application/pdf", "application/pdf")]
    [InlineData("sample.png", "image/png", "image/png")]
    [InlineData("sample.jpg", "image/jpeg", "image/jpeg")]
    [InlineData("sample.jpeg", "image/jpeg", "image/jpeg")]
    [InlineData(
        "sample.docx",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    public async Task Upload_ValidSupportedFile_ReturnsAcceptedAndPersistsPendingScan(
        string fileName,
        string clientMediaType,
        string expectedMediaType)
    {
        using HttpClient client = CreateClient();
        (Guid ownerId, string token) = await CreateAuthenticatedUserAsync(client);
        byte[] content = ContentFor(fileName);

        HttpResponseMessage response = await UploadAsync(
            client,
            token,
            fileName,
            content,
            clientMediaType);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        FileUploadResponse upload = await ReadUploadAsync(response);
        Assert.NotEqual(Guid.Empty, upload.FileId);
        Assert.Equal(fileName, upload.OriginalFileName);
        Assert.Equal(content.LongLength, upload.SizeBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), upload.Sha256);
        Assert.Equal(expectedMediaType, upload.DetectedMediaType);
        Assert.Equal(nameof(FileRecordStatus.PendingScan), upload.Status);

        using IServiceScope scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FileSentryDbContext>();
        FileRecord record = await dbContext.FileRecords.SingleAsync(candidate =>
            candidate.Id == upload.FileId);
        Assert.Equal(ownerId, record.OwnerId);
        Assert.Equal(FileRecordStatus.PendingScan, record.Status);
        Assert.Equal(upload.Sha256, record.Sha256);
        Assert.Equal(clientMediaType, record.ClientMediaType);
        Assert.Matches("^[0-9a-f]{32}\\.quarantine$", record.StorageName);
        Assert.True(File.Exists(Path.Combine(factory.QuarantineRootPath, record.StorageName)));
        Assert.Empty(Directory.GetFiles(factory.TempRootPath));
    }

    [Fact]
    public async Task Upload_WithoutAuthentication_ReturnsUnauthorized()
    {
        using HttpClient client = CreateClient();

        HttpResponseMessage response = await UploadAsync(
            client,
            token: null,
            "sample.pdf",
            TestFileContent.Pdf(),
            "application/pdf");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Upload_EmptyFile_ReturnsStableProblemAndLeavesNoFiles()
    {
        await AssertRejectedAndCleanAsync(
            "empty.pdf",
            [],
            "application/pdf",
            HttpStatusCode.BadRequest,
            "EMPTY_FILE");
    }

    [Fact]
    public async Task Upload_StreamOverTenMiB_ReturnsPayloadTooLargeAndLeavesNoFiles()
    {
        byte[] content = new byte[FileIngestionService.MaximumFileSizeBytes + 1];
        TestFileContent.Pdf().CopyTo(content, 0);

        await AssertRejectedAndCleanAsync(
            "large.pdf",
            content,
            "application/pdf",
            HttpStatusCode.RequestEntityTooLarge,
            "FILE_TOO_LARGE");
    }

    [Fact]
    public async Task Upload_UnsupportedExtension_ReturnsUnsupportedMediaTypeAndLeavesNoFiles()
    {
        await AssertRejectedAndCleanAsync(
            "payload.exe",
            TestFileContent.Pdf(),
            "application/pdf",
            HttpStatusCode.UnsupportedMediaType,
            "UNSUPPORTED_EXTENSION");
    }

    [Fact]
    public async Task Upload_ForgedClientMediaType_DetectsFromBytesAndAccepts()
    {
        using HttpClient client = CreateClient();
        (_, string token) = await CreateAuthenticatedUserAsync(client);

        HttpResponseMessage response = await UploadAsync(
            client,
            token,
            "report.pdf",
            TestFileContent.Pdf(),
            "application/x-msdownload");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        FileUploadResponse upload = await ReadUploadAsync(response);
        Assert.Equal("application/pdf", upload.DetectedMediaType);
    }

    [Fact]
    public async Task Upload_ExtensionSignatureMismatch_ReturnsStableProblemAndLeavesNoFiles()
    {
        await AssertRejectedAndCleanAsync(
            "photo.png",
            TestFileContent.Pdf(),
            "image/png",
            HttpStatusCode.BadRequest,
            "FILE_TYPE_MISMATCH");
    }

    [Fact]
    public async Task Upload_MalformedSignature_ReturnsStableProblemAndLeavesNoFiles()
    {
        await AssertRejectedAndCleanAsync(
            "report.pdf",
            "malformed file"u8.ToArray(),
            "application/pdf",
            HttpStatusCode.BadRequest,
            "INVALID_FILE_SIGNATURE");
    }

    [Fact]
    public async Task Upload_GenericZipMasqueradingAsDocx_ReturnsInvalidDocxAndLeavesNoFiles()
    {
        await AssertRejectedAndCleanAsync(
            "report.docx",
            TestFileContent.GenericZip(),
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            HttpStatusCode.BadRequest,
            "INVALID_DOCX");
    }

    [Fact]
    public async Task Upload_MultipleFiles_ReturnsStableProblemAndRemovesFirstStagedFile()
    {
        using HttpClient client = CreateClient();
        (_, string token) = await CreateAuthenticatedUserAsync(client);
        HashSet<string> tempBefore = ExistingFiles(factory.TempRootPath);
        HashSet<string> quarantineBefore = ExistingFiles(factory.QuarantineRootPath);
        int recordsBefore = await CountFileRecordsAsync();
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new ByteArrayContent(TestFileContent.Pdf()), "first", "first.pdf");
        multipart.Add(new ByteArrayContent(TestFileContent.Png()), "second", "second.png");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/files")
        {
            Content = multipart
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response = await client.SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "MULTIPLE_FILES");
        Assert.Equal(tempBefore, ExistingFiles(factory.TempRootPath));
        Assert.Equal(quarantineBefore, ExistingFiles(factory.QuarantineRootPath));
        Assert.Equal(recordsBefore, await CountFileRecordsAsync());
    }

    [Fact]
    public async Task Upload_TraversalFilename_IsDisplayMetadataOnly()
    {
        using HttpClient client = CreateClient();
        (_, string token) = await CreateAuthenticatedUserAsync(client);

        HttpResponseMessage response = await UploadAsync(
            client,
            token,
            "../../hostile\\report.pdf",
            TestFileContent.Pdf(),
            "application/pdf");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        FileUploadResponse upload = await ReadUploadAsync(response);
        Assert.Equal("report.pdf", upload.OriginalFileName);
        Assert.False(File.Exists(Path.Combine(factory.StorageRootPath, "report.pdf")));

        using IServiceScope scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FileSentryDbContext>();
        FileRecord record = await dbContext.FileRecords.SingleAsync(candidate =>
            candidate.Id == upload.FileId);
        Assert.DoesNotContain("report", record.StorageName, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(factory.QuarantineRootPath, record.StorageName)));
    }

    [Fact]
    public async Task Upload_DuplicateOriginalFilenames_CreateDistinctRecordsAndStorageNames()
    {
        using HttpClient client = CreateClient();
        (_, string token) = await CreateAuthenticatedUserAsync(client);
        byte[] content = TestFileContent.Pdf();

        FileUploadResponse first = await ReadUploadAsync(await UploadAsync(
            client, token, "duplicate.pdf", content, "application/pdf"));
        FileUploadResponse second = await ReadUploadAsync(await UploadAsync(
            client, token, "duplicate.pdf", content, "application/pdf"));

        Assert.NotEqual(first.FileId, second.FileId);
        using IServiceScope scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FileSentryDbContext>();
        List<FileRecord> records = await dbContext.FileRecords
            .Where(record => record.Id == first.FileId || record.Id == second.FileId)
            .ToListAsync();
        Assert.Equal(2, records.Count);
        Assert.Single(records.Select(record => record.OriginalFileName).Distinct());
        Assert.Equal(2, records.Select(record => record.StorageName).Distinct().Count());
    }

    [Fact]
    public async Task Upload_PersistenceFailure_RemovesStagedFileAndDoesNotCreateRecord()
    {
        using HttpClient client = CreateClient();
        Guid nonexistentOwnerId = Guid.NewGuid();
        string token = CreateToken(nonexistentOwnerId);
        HashSet<string> tempBefore = ExistingFiles(factory.TempRootPath);
        HashSet<string> quarantineBefore = ExistingFiles(factory.QuarantineRootPath);

        HttpResponseMessage response = await UploadAsync(
            client,
            token,
            "report.pdf",
            TestFileContent.Pdf(),
            "application/pdf");

        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "UPLOAD_FAILED");
        Assert.Equal(tempBefore, ExistingFiles(factory.TempRootPath));
        Assert.Equal(quarantineBefore, ExistingFiles(factory.QuarantineRootPath));
        using IServiceScope scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FileSentryDbContext>();
        Assert.False(await dbContext.FileRecords.AnyAsync(record =>
            record.OwnerId == nonexistentOwnerId));
    }

    [Fact]
    public async Task Ingestion_CancellationDuringStreaming_RemovesPartialTempFile()
    {
        byte[] fileContent = new byte[4096];
        TestFileContent.Pdf().CopyTo(fileContent, 0);
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new ByteArrayContent(fileContent), "file", "cancelled.pdf");
        byte[] requestBytes = await multipart.ReadAsByteArrayAsync();
        string contentType = multipart.Headers.ContentType!.ToString();
        using var cancellationSource = new CancellationTokenSource();
        await using var requestBody = new CancelAfterBytesStream(
            requestBytes,
            cancellationSource,
            cancelAfterBytes: 512);
        HashSet<string> tempBefore = ExistingFiles(factory.TempRootPath);
        HashSet<string> quarantineBefore = ExistingFiles(factory.QuarantineRootPath);
        int recordsBefore = await CountFileRecordsAsync();
        using IServiceScope scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<FileIngestionService>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.IngestAsync(
            requestBody,
            contentType,
            Guid.NewGuid(),
            Guid.NewGuid().ToString("N"),
            cancellationSource.Token));

        Assert.True(cancellationSource.IsCancellationRequested);
        Assert.Equal(tempBefore, ExistingFiles(factory.TempRootPath));
        Assert.Equal(quarantineBefore, ExistingFiles(factory.QuarantineRootPath));
        Assert.Equal(recordsBefore, await CountFileRecordsAsync());
    }

    private async Task AssertRejectedAndCleanAsync(
        string fileName,
        byte[] content,
        string clientMediaType,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        using HttpClient client = CreateClient();
        (_, string token) = await CreateAuthenticatedUserAsync(client);
        HashSet<string> tempBefore = ExistingFiles(factory.TempRootPath);
        HashSet<string> quarantineBefore = ExistingFiles(factory.QuarantineRootPath);
        int recordsBefore = await CountFileRecordsAsync();

        HttpResponseMessage response = await UploadAsync(
            client,
            token,
            fileName,
            content,
            clientMediaType);

        await AssertProblemAsync(response, expectedStatus, expectedCode);
        Assert.Equal(tempBefore, ExistingFiles(factory.TempRootPath));
        Assert.Equal(quarantineBefore, ExistingFiles(factory.QuarantineRootPath));
        Assert.Equal(recordsBefore, await CountFileRecordsAsync());
    }

    private HttpClient CreateClient() => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost")
    });

    private static async Task<(Guid UserId, string Token)> CreateAuthenticatedUserAsync(
        HttpClient client)
    {
        string email = $"upload-{Guid.NewGuid():N}@example.com";
        HttpResponseMessage registration = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterRequest(email, ValidPassword));
        registration.EnsureSuccessStatusCode();
        CurrentUserResponse user = await registration.Content
            .ReadFromJsonAsync<CurrentUserResponse>()
            ?? throw new InvalidOperationException("Registration response was empty.");

        HttpResponseMessage login = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(email, ValidPassword));
        login.EnsureSuccessStatusCode();
        AuthenticationResponse authentication = await login.Content
            .ReadFromJsonAsync<AuthenticationResponse>()
            ?? throw new InvalidOperationException("Authentication response was empty.");
        return (user.UserId, authentication.AccessToken);
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        string? token,
        string fileName,
        byte[] content,
        string clientMediaType)
    {
        using var multipart = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(clientMediaType);
        multipart.Add(fileContent, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/files")
        {
            Content = multipart
        };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request);
    }

    private static byte[] ContentFor(string fileName) => Path.GetExtension(fileName) switch
    {
        ".pdf" => TestFileContent.Pdf(),
        ".png" => TestFileContent.Png(),
        ".jpg" or ".jpeg" => TestFileContent.Jpeg(),
        ".docx" => TestFileContent.Docx(),
        _ => throw new ArgumentOutOfRangeException(nameof(fileName))
    };

    private static async Task<FileUploadResponse> ReadUploadAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FileUploadResponse>()
            ?? throw new InvalidOperationException("Upload response was empty.");
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

    private static HashSet<string> ExistingFiles(string path) =>
        Directory.Exists(path)
            ? Directory.GetFiles(path)
                .Select(filePath => Path.GetFileName(filePath)
                    ?? throw new InvalidOperationException("Storage file name was empty."))
                .ToHashSet(StringComparer.Ordinal)
            : [];

    private async Task<int> CountFileRecordsAsync()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FileSentryDbContext>();
        return await dbContext.FileRecords.CountAsync();
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

    private sealed class CancelAfterBytesStream(
        byte[] content,
        CancellationTokenSource cancellationSource,
        int cancelAfterBytes) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => content.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_position >= cancelAfterBytes)
            {
                cancellationSource.Cancel();
                return ValueTask.FromCanceled<int>(cancellationSource.Token);
            }

            int count = Math.Min(
                Math.Min(buffer.Length, 32),
                Math.Min(cancelAfterBytes - _position, content.Length - _position));
            content.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return ValueTask.FromResult(count);
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
