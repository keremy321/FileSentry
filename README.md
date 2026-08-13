# FileSentry

FileSentry is a backend-only ASP.NET Core API that accepts authenticated file
uploads, validates their real format, quarantines them, scans their bytes with
ClamAV, and exposes only conclusively clean files to their owner.

> Every uploaded file is untrusted until validation and malware scanning complete
> successfully.

The implemented MVP uses .NET 10, PostgreSQL 17, Entity Framework Core, ASP.NET
Core Identity, JWT bearer authentication, local protected storage, a durable
background scanner, Docker Compose, and GitHub Actions.

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
Authenticated client
        |
        v
ASP.NET Core API + scanner BackgroundService
    |           |                 |
    v           v                 v
PostgreSQL   protected storage   ClamAV INSTREAM
             temp/quarantine/
                  clean
```

Docker Compose currently starts PostgreSQL and ClamAV. The API runs on the host,
which is why both dependency ports are bound to `127.0.0.1`. ClamAV port 3310 is
unencrypted and unauthenticated; never expose it beyond loopback. Remove its host
mapping if the API is later containerized on the same private Docker network.

## Prerequisites

- Git
- .NET SDK 10.0.302 (pinned by `global.json`)
- Docker Engine with Docker Compose v2
- PowerShell 7 for the examples below, or equivalent shell commands

The test suite also uses Docker because Testcontainers starts disposable PostgreSQL
and ClamAV instances.

## Clean-clone setup

### 1. Create local infrastructure configuration

Copy the placeholder file and replace only the local PostgreSQL password:

```powershell
Copy-Item deploy/.env.example deploy/.env
```

`deploy/.env` is ignored by Git. Keep the database name, user, and ports aligned
with the connection string configured in the next step.

### 2. Start PostgreSQL and ClamAV

```powershell
docker compose --env-file deploy/.env -f deploy/docker-compose.yml up -d
docker compose --env-file deploy/.env -f deploy/docker-compose.yml ps
```

The first ClamAV start may take several minutes while signatures initialize. Follow
its logs if needed:

```powershell
docker compose --env-file deploy/.env -f deploy/docker-compose.yml logs -f clamav
```

Stop the dependencies without deleting their named volumes with:

```powershell
docker compose --env-file deploy/.env -f deploy/docker-compose.yml down
```

### 3. Configure API secrets

The application deliberately has no checked-in database password or JWT key. Set
both through the API project's .NET User Secrets store:

```powershell
dotnet user-secrets --project src/FileSentry.Api set "ConnectionStrings:PostgreSql" "Host=127.0.0.1;Port=5432;Database=filesentry;Username=filesentry;Password=<same-password-as-deploy-env>"
dotnet user-secrets --project src/FileSentry.Api set "Jwt:SigningKey" "<generate-a-random-secret-containing-at-least-32-bytes>"
```

Do not use the literal placeholders. Equivalent environment variables are
`ConnectionStrings__PostgreSql` and `Jwt__SigningKey`. Other validated defaults are
in `src/FileSentry.Api/appsettings.json`, including:

- ClamAV host, ports, timeouts, and stream limit;
- three bounded scan attempts with exponential backoff and stale-job recovery;
- authentication and upload fixed-window limits;
- the storage root, resolved outside the API web root.

Invalid security-critical configuration causes startup to fail clearly.

### 4. Restore tools and apply migrations

```powershell
dotnet tool restore
dotnet restore FileSentry.slnx
dotnet ef database update --project src/FileSentry.Api
```

The API does not silently create or migrate the database at startup. Applying the
checked-in EF Core migrations is an explicit setup step.

### 5. Run the API

```powershell
dotnet run --project src/FileSentry.Api --launch-profile http
```

The development HTTP profile listens at `http://localhost:5029`. Development
OpenAPI JSON is available at `http://localhost:5029/openapi/v1.json`.

## Health checks

```powershell
Invoke-WebRequest http://localhost:5029/health/live
Invoke-WebRequest http://localhost:5029/health/ready
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
$baseUrl = 'http://localhost:5029'
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
dotnet ef migrations list --project src/FileSentry.Api
docker compose --env-file deploy/.env -f deploy/docker-compose.yml config --quiet
dotnet package list --project FileSentry.slnx --vulnerable --include-transitive
```

Unit tests cover deterministic validation and protocol behavior. Integration tests
use Testcontainers with real PostgreSQL and, for real scanner paths, ClamAV. The
EICAR test value is assembled inside tests rather than committed as a standalone
sample.

## Continuous integration

`.github/workflows/ci.yml` runs on pull requests and pushes to `main`. Its
least-privilege Ubuntu job installs the exact SDK, restores, validates Compose,
pre-pulls the existing Testcontainers images, builds in Release, runs the complete
test suite, verifies formatting, and audits direct and transitive NuGet packages.

CI uses runtime-generated Testcontainers credentials and step-scoped Compose
placeholders. It requires no repository secrets or developer User Secrets. The
hosted workflow has been observed passing.

## Limitations and residual risk

A ClamAV `Clean` response means the configured signatures and heuristics did not
identify the content at scan time. It is not proof that a file is harmless. Zero-day
malware, parser vulnerabilities, malicious document behavior, and social-engineering
content can remain undetected.

Additional MVP limitations:

- local storage and the in-process worker target one application host;
- ClamAV TCP has no transport security and is safe here only because it is bound to
  loopback;
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
