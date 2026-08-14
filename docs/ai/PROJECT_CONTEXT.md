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
| Architecture | Modular monolith |
| Database | PostgreSQL 17 |
| Persistence | Entity Framework Core with Npgsql migrations |
| Authentication | ASP.NET Core Identity and JWT bearer tokens |
| Scanner | Official ClamAV container; clamd TCP protocol |
| Scan transport | ClamAV `INSTREAM` over bounded streams |
| Storage | Local temp, quarantine, and clean directories outside the web root |
| Background processing | ASP.NET Core `BackgroundService` with durable database state |
| Testing | xUnit, WebApplicationFactory, and Testcontainers |
| Local infrastructure | Docker Compose for PostgreSQL and ClamAV |
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

The API registers/authenticates callers, accepts bounded upload streams under
generated storage names, performs format validation, and returns safe metadata. It
lists owner records, retrieves owner metadata, streams only owner-controlled clean
content, and coordinates durable deletion.

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
sent to the scanner. Compose binds port 3310 to loopback while the API runs on the
host because clamd TCP has no transport security.

### Storage

The configured root resolves outside the web root and contains fixed directories:

```text
data/
|-- temp/
|-- quarantine/
`-- clean/
```

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

The API does not auto-migrate; clean-clone setup explicitly runs `dotnet ef database
update`.

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

## Current verified state

The MVP implements infrastructure health, Identity/JWT authentication, secure
ingestion, durable ClamAV scanning and recovery, owner-protected file lifecycle,
auditing/correlation/rate limiting, adversarial tests, and GitHub Actions CI.

Integration tests use disposable PostgreSQL 17 and real ClamAV Testcontainers. The
final local gate passed from an isolated clone: a zero-warning Release build, 31
unit tests, 75 integration tests, formatting, migrations, Compose validation, and
the NuGet vulnerability audit all succeeded. Live clean, EICAR-infected,
retry-exhausted scanner-outage, ownership, audit/correlation, and rate-limit flows
also passed. Local development uses an ignored `deploy/.env` for Compose and .NET
User Secrets (or equivalent external configuration) for the database connection
string and JWT signing key.

Release documentation now lives in the README plus focused architecture, threat
model, demo, and project-plan files. GitHub Actions has passed on merged `main`.
A recorded demo video is intentionally outside project scope; `docs/demo.md` remains
the repeatable written guide. Remote repository description/topics remain
unverified, and no tag or GitHub release has been created.

## Limitations and explicit non-goals

- A clean antivirus result is not proof that a file is harmless.
- Local storage and the in-process worker target a single host.
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
