# FileSentry

FileSentry is a backend-only ASP.NET Core API that accepts authenticated file
uploads, validates their real format, quarantines them, scans their bytes with
ClamAV, and exposes only conclusively clean files to their owner.

> Every uploaded file is untrusted until validation and malware scanning complete
> successfully.

The implemented MVP uses .NET 10, PostgreSQL 17, Entity Framework Core, ASP.NET
Core Identity, JWT bearer and service API-key authentication, protected persistent
storage, a durable background scanner, Docker Compose, and GitHub Actions.

## Security model

- Filenames, extensions, client MIME types, identifiers, and bytes are untrusted.
- Uploads stream to a generated temporary path; the full file is never buffered in
  memory.
- The actual streamed size is limited to 10 MiB and SHA-256 is calculated during
  ingestion.
- Only `.pdf`, `.png`, `.jpg`, `.jpeg`, and `.docx` are accepted. Server-side
  signatures must match the extension, and DOCX must be a valid Word Open XML
  package rather than an arbitrary ZIP.
- Internal storage names are generated. Client filenames are sanitized display
  metadata and never become filesystem paths.
- Files remain outside the web root in temporary, quarantine, or clean storage.
- ClamAV receives bytes through `INSTREAM`, never a client-controlled path.
- Scanner errors, timeouts, malformed replies, and unknown results fail closed.
- Every metadata, download, and delete query includes the authenticated owner ID.
  Cross-owner identifiers return the same `404` behavior as missing files.
- Only a file in `Clean` state with matching clean storage can be downloaded.

See [architecture](docs/architecture.md) and the [threat model](docs/threat-model.md)
for the trust boundaries and residual risks.

## Implemented architecture

FileSentry is a modular monolith. The API and scanner worker run in the same ASP.NET
Core process; PostgreSQL is the durable workflow source of truth.

```text
JWT user or API-key service
             |
             v
ASP.NET Core API + scanner BackgroundService
    |           |                 |
    v           v                 v
PostgreSQL   persistent storage  ClamAV INSTREAM
             temp/quarantine/
                  clean
```

Docker Compose runs the API/worker, a one-shot migration service, PostgreSQL, and
ClamAV on one private network. Only the API is published, on `127.0.0.1:8080` by
default. PostgreSQL and ClamAV are addressed by service name and have no host port
mapping. ClamAV port 3310 is unencrypted and unauthenticated and must remain
internal.

## Prerequisites

- Git
- .NET SDK 10.0.302 (pinned by `global.json`)
- Docker Engine with Docker Compose v2
- PowerShell 7 for the examples below, or equivalent shell commands

The test suite also uses Docker because Testcontainers starts disposable PostgreSQL
and ClamAV instances.

## Clean-clone setup

### 1. Create local configuration

Copy the placeholder file:

```powershell
Copy-Item deploy/.env.example deploy/.env
```

`deploy/.env` is ignored by Git. Replace every `replace-with-...` value. The JWT
signing key and service API key must each contain at least 32 random bytes; use
different values. Generate suitable values and a stable service identity, for
example:

```powershell
$randomBytes = [byte[]]::new(48)
[Security.Cryptography.RandomNumberGenerator]::Fill($randomBytes)
[Convert]::ToBase64String($randomBytes)
New-Guid
```

Put the generated GUID in `FILESENTRY_SERVICE_ID`. Keep it stable across restarts
and API-key rotations because it is the service's persisted file-owner ID. Never
commit `deploy/.env` or use the example placeholders as real credentials.

### 2. Build and start the complete stack

```powershell
docker compose --env-file deploy/.env -f deploy/docker-compose.yml up --detach --build
docker compose --env-file deploy/.env -f deploy/docker-compose.yml ps --all
```

The `migrate` service applies all checked-in EF migrations and provisions the
configured passwordless service identity before the API starts. It should show
`Exited (0)` while `api`, `postgresql`, and `clamav` become healthy. The first
ClamAV start may take several minutes while signatures initialize. Follow logs if
needed:

```powershell
docker compose --env-file deploy/.env -f deploy/docker-compose.yml logs -f clamav
```

The API is available at `http://127.0.0.1:8080`. Change
`FILESENTRY_API_BIND_ADDRESS` only when deliberate network exposure is protected by
appropriate TLS and network controls. PostgreSQL and ClamAV remain unexposed.

### 3. Migration and storage behavior

The normal `up` command runs migrations once through the `migrate` service. To
apply new checked-in migrations explicitly after an update:

```powershell
docker compose --env-file deploy/.env -f deploy/docker-compose.yml run --rm migrate
```

The API process does not migrate opportunistically. Temp, quarantine, clean,
PostgreSQL, and ClamAV signature data use separate named volumes. Upload storage is
mounted only into the API/migration containers, never into ClamAV.

### 4. Stop the stack

Stop containers without deleting persisted data:

```powershell
docker compose --env-file deploy/.env -f deploy/docker-compose.yml down
```

Deleting named volumes is intentionally separate and destroys database and stored
file state. Production deployments should inject secrets from a managed secret
store rather than treating a local `.env` file as secret management.

Other validated defaults are in `src/FileSentry.Api/appsettings.json`, including:

- ClamAV host, ports, timeouts, and stream limit;
- three bounded scan attempts with exponential backoff and stale-job recovery;
- authentication and upload fixed-window limits;
- the storage root, resolved outside the API web root.

Invalid database, JWT, service-authentication, scanner, storage, or rate-limit
configuration causes startup to fail clearly. Service authentication is disabled
by default outside Compose and requires a non-empty GUID, a controlled service
name, and a key of at least 32 bytes when enabled.

## Health checks

```powershell
Invoke-WebRequest http://127.0.0.1:8080/health/live
Invoke-WebRequest http://127.0.0.1:8080/health/ready
```

- `GET /health/live` checks that the process is running.
- `GET /health/ready` checks the process, PostgreSQL, and ClamAV.

Readiness returns a failure status while either dependency is unavailable. That
failure never makes quarantined content downloadable.

## Authentication and API examples

The following PowerShell session registers a user, logs in, and builds an
authorization header. The password policy requires at least 12 characters with
uppercase, lowercase, numeric, and non-alphanumeric characters.

```powershell
$baseUrl = 'http://127.0.0.1:8080'
$credentials = @{
    email = 'user@example.test'
    password = '<choose-a-valid-local-password>'
} | ConvertTo-Json

Invoke-RestMethod -Method Post -Uri "$baseUrl/api/v1/auth/register" `
    -ContentType 'application/json' -Body $credentials

$login = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/v1/auth/login" `
    -ContentType 'application/json' -Body $credentials
$headers = @{ Authorization = "Bearer $($login.accessToken)" }

Invoke-RestMethod -Headers $headers -Uri "$baseUrl/api/v1/auth/me"
```

Registration returns `201 Created`; login returns a short-lived bearer token.
Authentication endpoints are rate-limited per client IP. Do not log or persist the
token outside a suitable secret store.

### Service authentication

Trusted backend clients can use the configured service credential on existing file
routes without registering or storing a username/password:

```powershell
$serviceHeaders = @{ 'X-Api-Key' = '<value-from-FILESENTRY_SERVICE_API_KEY>' }
Invoke-RestMethod -Headers $serviceHeaders -Uri "$baseUrl/api/v1/files"
```

The service may upload, list, read metadata, download clean owned files, and delete
owned files. It has the same owner predicates and upload rate limit as a JWT user;
it cannot access another user's files, authentication management routes, or
administrative capabilities. `/api/v1/auth/me` remains JWT-only. If a request
contains both a Bearer header and `X-Api-Key`, the Bearer credential is authoritative
and an invalid JWT cannot fall back to the service key.

The migration service creates a reserved passwordless Identity row for the stable
service ID. Rotating `FILESENTRY_SERVICE_API_KEY` and recreating the API preserves
ownership as long as `FILESENTRY_SERVICE_ID` is unchanged. The key is external
configuration: it is not persisted, returned, or intentionally logged.

### Upload and scan workflow

Upload exactly one multipart file:

```powershell
$upload = Invoke-RestMethod -Method Post -Headers $headers `
    -Uri "$baseUrl/api/v1/files" `
    -Form @{ file = Get-Item 'C:\path\to\document.pdf' }
$upload
```

Successful ingestion returns `202 Accepted` with safe metadata: file ID, sanitized
original filename, actual byte size, SHA-256, detected media type, creation time,
and initial `PendingScan` status. Client `Content-Type` does not determine the
detected format.

The durable workflow is:

```text
PendingScan -> Scanning -> Clean
                        -> Infected
                        -> ScanFailed -> PendingScan (bounded retry)

PendingScan / Scanning / Clean / Infected / ScanFailed -> Deleted
```

Clean bytes move from quarantine to clean storage. Infected bytes are deleted while
safe database metadata remains. Retryable failures remain quarantined; exhausted or
non-retryable failures remain unavailable. Stale `Scanning` claims are recovered
after a restart.

Poll the metadata endpoint with the returned ID:

```powershell
$fileId = $upload.fileId
Invoke-RestMethod -Headers $headers -Uri "$baseUrl/api/v1/files/$fileId"
```

### File access endpoints

| Method | Route | Behavior |
|---|---|---|
| `GET` | `/api/v1/files` | Lists the caller's file metadata, newest first |
| `GET` | `/api/v1/files/{fileId}` | Returns caller-owned metadata and current state |
| `GET` | `/api/v1/files/{fileId}/download` | Streams caller-owned `Clean` content only |
| `DELETE` | `/api/v1/files/{fileId}` | Removes stored bytes and marks metadata `Deleted` |

```powershell
Invoke-RestMethod -Headers $headers -Uri "$baseUrl/api/v1/files"

Invoke-WebRequest -Headers $headers `
    -Uri "$baseUrl/api/v1/files/$fileId/download" `
    -OutFile '.\downloaded-file.pdf'

Invoke-RestMethod -Method Delete -Headers $headers `
    -Uri "$baseUrl/api/v1/files/$fileId"
```

A non-clean download returns `409` with code `FILE_NOT_CLEAN`. Missing and
cross-owner IDs return the same `404 FILE_NOT_FOUND` response. Errors use
`application/problem+json` with a stable `code` extension.

## C# client SDK

The pack-ready [.NET client](src/FileSentry.Client/README.md) wraps the existing
service-authenticated `/api/v1/files` contract. It is currently versioned
`1.1.0-preview.1` for development and has not been published to NuGet.

```csharp
using FileSentry.Client;

using var client = new FileSentryClient(new FileSentryClientOptions
{
    BaseAddress = new Uri("https://filesentry.example/"),
    ApiKey = configuration["FileSentry:ApiKey"]!,
    PollingInterval = TimeSpan.FromSeconds(1),
    ScanTimeout = TimeSpan.FromMinutes(5)
});

await using Stream uploadContent = File.OpenRead("cv.pdf");
FileUpload upload = await client.UploadAsync(uploadContent, "cv.pdf", cancellationToken);
FileMetadata result = await client.WaitForScanAsync(upload.FileId, cancellationToken);

if (result.Status == FileStatus.Clean)
{
    await using Stream clean = await client.DownloadAsync(
        upload.FileId,
        cancellationToken);
    // Consume the stream while still treating its content as potentially risky.
}
```

Use the sequence `Upload -> Wait for terminal scan status -> continue only if Clean
-> Download`. `Infected`, `ScanFailed`, and `Deleted` stop polling without becoming
successful. Unknown or malformed states fail safely. Uploads are not retried
automatically, and API ProblemDetails are available through typed, credential-
redacted SDK exceptions.

Runnable project-reference examples for a console consumer and a streaming ASP.NET
Core backend are documented in [examples/README.md](examples/README.md).

## Audit, correlation, and rate limiting

- Every request receives an `X-Correlation-ID`. A valid caller value is reused;
  otherwise the API generates one and returns it in the response.
- Upload correlation is persisted so background scan events remain connected after
  restarts.
- PostgreSQL audit events cover accepted uploads, scan start/outcomes, malware
  detection, failed scans, downloads, and deletes.
- Audit fields are strictly controlled and contain no bytes, passwords, JWTs,
  arbitrary client objects, or raw malware.
- Authentication defaults to 10 attempts per 60-second fixed window per client IP.
- Upload defaults to 10 attempts per 60-second fixed window per authenticated user.
- A rejected request returns `429` ProblemDetails with `AUTH_RATE_LIMITED` or
  `UPLOAD_RATE_LIMITED`.

These are in-process rate limiters suitable for this single-instance MVP, not a
distributed quota system.

## Tests and local verification

```powershell
dotnet restore FileSentry.slnx
dotnet build FileSentry.slnx --configuration Release
dotnet test FileSentry.slnx --configuration Release
dotnet format FileSentry.slnx --verify-no-changes
docker compose --env-file deploy/.env -f deploy/docker-compose.yml config --quiet
docker compose --env-file deploy/.env -f deploy/docker-compose.yml run --rm migrate
dotnet package list --project FileSentry.slnx --vulnerable --include-transitive
```

Unit tests cover deterministic validation and protocol behavior. Integration tests
use Testcontainers with real PostgreSQL and, for real scanner paths, ClamAV. The
EICAR test value is assembled inside tests rather than committed as a standalone
sample.

## Continuous integration

`.github/workflows/ci.yml` runs on pull requests and pushes to `main`. Its
least-privilege Ubuntu job installs the exact SDK, restores, validates Compose,
pre-pulls the existing Testcontainers images, builds in Release, builds the API
container image, runs the complete test suite, validates the unpublished client
package, verifies formatting, and audits direct and transitive NuGet packages.

CI uses runtime-generated Testcontainers credentials and step-scoped Compose
placeholders. It requires no repository secrets or developer User Secrets. The
hosted workflow has been observed passing.

## Limitations and residual risk

A ClamAV `Clean` response means the configured signatures and heuristics did not
identify the content at scan time. It is not proof that a file is harmless. Zero-day
malware, parser vulnerabilities, malicious document behavior, and social-engineering
content can remain undetected.

Additional MVP limitations:

- local named-volume storage and the in-process worker target one Compose host;
- ClamAV TCP has no transport security and is safe here only because it has no host
  port mapping and remains on the private Compose network;
- there is no content disarm, sandbox execution, document preview, storage quota,
  retention worker, public sharing, admin UI/API, or multi-engine scanning;
- there is no distributed rate limiter, message broker, cloud storage, or
  horizontally scaled worker coordination;
- production deployment must provide TLS, strong external secrets, backups,
  restricted operating-system permissions, monitoring, and a retention policy.

Future enhancements are tracked in [the project plan](docs/PROJECT_PLAN.md). A
repeatable manual walkthrough is in [the demo guide](docs/demo.md).

## License

FileSentry is available under the [MIT License](LICENSE).
