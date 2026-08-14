# FileSentry Project Context

## Purpose

FileSentry is a secure file-upload and malware-scanning API. It authenticates users,
validates and streams uploads into non-public quarantine, calculates SHA-256, scans
bytes with ClamAV, and exposes only conclusively clean content through owner-scoped
endpoints.

The primary design rule is:

> Every uploaded file is untrusted until validation and malware scanning complete
> successfully.

## Implemented technology

| Area | Decision |
|---|---|
| Runtime | .NET 10, exact SDK pinned in `global.json` |
| API | ASP.NET Core controllers with development OpenAPI |
| C# client | Pack-ready .NET 10 `FileSentry.Client` library using service API-key authentication |
| Architecture | Modular monolith |
| Database | PostgreSQL 17 |
| Persistence | Entity Framework Core with Npgsql migrations |
| Authentication | ASP.NET Core Identity/JWT plus owner-scoped `X-Api-Key` service identity |
| Scanner | Official ClamAV container; clamd TCP protocol |
| Scan transport | ClamAV `INSTREAM` over bounded streams |
| Storage | Local temp, quarantine, and clean directories outside the web root |
| Background processing | ASP.NET Core `BackgroundService` with durable database state |
| Testing | xUnit, WebApplicationFactory, and Testcontainers |
| Local deployment | Docker Compose for API/worker, migration, PostgreSQL, ClamAV, and persistent storage |
| CI | GitHub Actions on Ubuntu |

## Repository layout

```text
FileSentry/
|-- .github/workflows/ci.yml
|-- deploy/
|   |-- .env.example
|   `-- docker-compose.yml
|-- docs/
|   |-- ai/
|   |-- architecture.md
|   |-- demo.md
|   |-- PROJECT_PLAN.md
|   `-- threat-model.md
|-- src/FileSentry.Api/
|-- src/FileSentry.Client/
|-- tests/
|   |-- FileSentry.UnitTests/
|   `-- FileSentry.IntegrationTests/
|-- AGENTS.md
|-- FileSentry.slnx
|-- global.json
|-- LICENSE
`-- README.md
```

## Component responsibilities

### API

The API registers/authenticates JWT users and authenticates a configured service
identity, accepts bounded upload streams under generated storage names, performs
format validation, and returns safe metadata. It lists owner records, retrieves
owner metadata, streams only owner-controlled clean content, and coordinates
durable deletion. Service callers have file-owner scope only; they cannot use
registration, login, `/auth/me`, foreign-file, or administrative behavior.

### C# client

`FileSentry.Client` wraps the existing file routes with streaming async methods for
upload, metadata/list, bounded status polling, clean download, and delete. It adds
the service key to individual requests rather than shared default headers. It
returns typed file/status models and bounded, credential-redacted ProblemDetails
exceptions. The server remains authoritative for format validation, hashing,
ownership, scanning, and download permission.

### PostgreSQL

PostgreSQL stores users, file records, scan attempts, security audit events, retry
timing, and active workflow claims. Database state, not directory contents, is
authoritative.

### Scanner worker

The in-process background worker claims eligible rows with PostgreSQL locking,
streams quarantine bytes to ClamAV, records attempts, promotes clean files, deletes
infected bytes, retries bounded transient failures, and recovers stale scanning jobs
after a crash.

### ClamAV

Health checks use `PING`; scans use `INSTREAM`. No client-controlled storage path is
sent to the scanner, and its container does not receive an application-storage
mount. ClamAV has no host port mapping and is reachable only on the private Compose
network because clamd TCP has no transport security.

### Storage

The configured root resolves outside the web root and contains fixed directories:

```text
<configured-root>/
|-- temp/
|-- quarantine/
`-- clean/
```

Compose uses `/var/lib/filesentry` as the configured root and mounts each child as
a persistent named volume with private application ownership.

Files use generated internal names. Original names are sanitized display metadata.
No upload directory is static web content.

## File policy and state

- Allowed: PDF, PNG, JPEG (`.jpg`/`.jpeg`), and DOCX.
- Maximum actual streamed file size: 10 MiB.
- Client MIME type is untrusted metadata.
- Extension and detected format must agree.
- DOCX must be a valid Word Open XML package, not merely ZIP content.

```text
PendingScan -> Scanning -> Clean
                        -> Infected
                        -> ScanFailed -> PendingScan (bounded retry)

PendingScan / Scanning / Clean / Infected / ScanFailed -> Deleted
```

Only `Clean` content may be downloaded, and only by its owner. Scanner errors are
non-downloadable. Infected bytes are removed while safe metadata remains.

## Implemented API

Base path: `/api/v1`.

```text
POST   /auth/register
POST   /auth/login
GET    /auth/me
POST   /files
GET    /files
GET    /files/{fileId}
GET    /files/{fileId}/download
DELETE /files/{fileId}
GET    /health/live
GET    /health/ready
```

File routes require authentication. Owner ID is included in read/download/delete
queries so cross-owner identifiers behave like missing records.

Requests receive a validated or generated `X-Correlation-ID`. Authentication and
upload endpoints use separate startup-validated fixed-window rate-limit policies;
uploads are partitioned by authenticated user ID.

## Persistent entities and migrations

`ApplicationUser` uses a GUID Identity key. `FileRecord` stores ownership, generated
storage metadata, safe original name, size, SHA-256, detected type, correlation,
workflow/storage state, retry timing, and scanner result metadata. `ScanAttempt`
records each claim and controlled result/failure metadata. `AuditEvent` records a
strictly controlled event type, actor/file IDs where applicable, correlation, UTC
time, state transition, bounded failure code, and duration.

The checked-in migrations are:

1. `InitialIdentity`;
2. `AddSecureFileIngestion`;
3. `AddDurableClamAvScanning`;
4. `AddFileAuthorizationLifecycle`;
5. `AddSecurityAuditEvents`.

The API does not opportunistically auto-migrate. Compose runs the same application
image once with `--migrate`, applies checked-in migrations, and provisions the
configured stable passwordless service owner before starting the API.

## Security decisions

1. Client request data is untrusted.
2. Only generated paths under trusted roots are valid.
3. Quarantine content remains untrusted even after format validation.
4. Only an exact conclusive scanner clean result permits promotion.
5. Authentication and per-object ownership are separate checks.
6. Dependency failures must leave content unavailable.
7. Audit and structured logging fields exclude bytes, filenames in free-form logs,
   tokens, passwords, keys, database secrets, and raw malware.
8. Security-critical options validate at startup.
9. A supplied Bearer header takes precedence over an API key, and service-key
   comparison uses fixed-time digest comparison with generic failures.

## Current verified state

The v1.0 MVP plus the first two v1.1 milestones implement infrastructure health,
Identity/JWT and service authentication, secure ingestion, durable ClamAV scanning
and recovery, owner-protected file lifecycle, auditing/correlation/rate limiting,
an all-container Compose topology, a pack-ready .NET client SDK, adversarial tests,
and GitHub Actions CI.

Integration tests use disposable PostgreSQL 17 and real ClamAV Testcontainers. The
final v1.0 local gate passed from an isolated clone with a zero-warning Release
build, the complete test suite, formatting, migrations, Compose validation, and the
NuGet vulnerability audit. Live clean, EICAR-infected,
retry-exhausted scanner-outage, ownership, audit/correlation, and rate-limit flows
also passed. Local development uses an ignored `deploy/.env` for Compose and .NET
User Secrets (or equivalent external configuration) for the database connection
string and JWT signing key.

The container image is multi-stage, Release-published, and runs as non-root. Compose
publishes only the API on loopback by default; PostgreSQL, ClamAV, and named upload
volumes remain private to the stack. Secrets are supplied externally through the
ignored `deploy/.env` for local use or an operational secret provider. GitHub
Actions validates Compose and builds the API image in addition to the existing
solution gates. The v1.1 workflow change is locally validated but has not yet run on
a hosted runner because this task does not commit or push.

The SDK is versioned `1.1.0-preview.1` and intentionally remains unpublished. Its
safe consumer workflow is upload, wait for a terminal scan status, continue only on
`Clean`, then stream the download. A clean result reduces risk but is not a safety
guarantee.

## Limitations and explicit non-goals

- A clean antivirus result is not proof that a file is harmless.
- Local named-volume storage, one configured service identity, and the in-process
  worker target a single Compose host.
- There is no frontend, public sharing, admin API/UI, cloud storage, message broker,
  multi-engine scanning, sandbox execution, content disarm, OCR, document preview,
  Kubernetes, or distributed rate limiting.
- Production TLS, managed secrets, backup/restore, monitoring, host permissions, and
  retention/quota policy remain operational responsibilities.

## Trustworthy completion criteria

- clean checkout restores, builds, tests, and applies migrations reproducibly;
- secrets and runtime artifacts remain external to source control;
- dependency failures fail closed;
- workflow state survives restarts;
- object ownership is enforced in database queries;
- adversarial and real-dependency tests remain green;
- documentation separates implemented behavior, limitations, and future work.
