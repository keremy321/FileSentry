# FileSentry Project Context

## Purpose

FileSentry is a secure file-upload and malware-scanning API. It accepts authenticated uploads, validates their format, streams them into non-public quarantine storage, calculates SHA-256 hashes, scans them with a locally hosted ClamAV daemon, and releases only conclusively clean files through ownership-protected endpoints.

The primary design rule is:

> Every uploaded file is untrusted until validation and malware scanning complete successfully.

## Initial Technology Decisions

| Area | Decision |
|---|---|
| Runtime | .NET 10 |
| API | ASP.NET Core controllers with OpenAPI |
| Architecture | Modular monolith |
| Database | PostgreSQL 17 |
| Persistence | Entity Framework Core with Npgsql |
| Authentication | ASP.NET Core Identity and JWT bearer tokens, implemented in a later phase |
| Scanner | Official ClamAV container; ClamD TCP protocol |
| Scan transport | `INSTREAM` for file bytes, implemented in a later phase |
| Storage | Local mounted quarantine, clean, and temporary directories |
| Background processing | ASP.NET Core `BackgroundService` with durable database state |
| Testing | xUnit, WebApplicationFactory, and Testcontainers |
| Local infrastructure | Docker Compose |
| CI | GitHub Actions |

## Solution Layout

```text
FileSentry/
├── deploy/
├── docs/
│   └── ai/
├── src/
│   └── FileSentry.Api/
├── tests/
│   ├── FileSentry.UnitTests/
│   └── FileSentry.IntegrationTests/
├── AGENTS.md
├── FileSentry.slnx
└── README.md
```

The exact internal folders may grow with implemented behavior. Clear separation is preferred over ceremonial layering.

## Components

### API

The API will authenticate requests, accept bounded upload streams, validate formats, generate safe internal identifiers, persist metadata, expose status, and stream clean files after ownership checks.

### PostgreSQL

PostgreSQL will store users, file records, scan attempts, audit events, workflow locks, retry data, and current file state. Database state—not directory contents—is authoritative.

### Scanner Worker

A background worker will claim pending database records, stream quarantined bytes to ClamAV, persist every attempt, promote clean files, delete infected bytes, retry transient failures, and reclaim stale jobs after a crash.

### ClamAV

ClamAV is a local signature-based malware-scanning dependency. Health checks use `PING`; later scans use `INSTREAM`. ClamAV should not receive client-controlled paths.

### Storage

Runtime storage uses three fixed trusted roots:

```text
data/
├── temp/
├── quarantine/
└── clean/
```

Files use generated internal names. Original names are display metadata only. None of these directories is public static content.

## Planned File States

```text
PendingScan -> Scanning -> Clean
                        -> Infected
                        -> ScanFailed -> PendingScan (bounded retry)

PendingScan/Clean/Infected/ScanFailed -> Deleted
```

Only `Clean` content may be downloaded, and only by its owner. Scanner errors are non-downloadable.

## Initial File Policy

- Allowed: PDF, PNG, JPEG, DOCX.
- Maximum size: 10 MiB.
- Rejected: executables, scripts, generic archives, HTML, SVG, `.doc`, `.docm`, unknown formats, and extension/signature mismatches.
- Client MIME type is untrusted metadata.
- Format verification uses server-side signatures and format-aware checks.
- DOCX must be a valid Open XML Word package, not merely a ZIP file.

## Planned API

Base path: `/api/v1`.

```text
POST   /auth/register
POST   /auth/login
POST   /files
GET    /files
GET    /files/{fileId}
GET    /files/{fileId}/download
DELETE /files/{fileId}
GET    /health/live
GET    /health/ready
```

Only health endpoints belong to the current infrastructure milestone. Other endpoints are planned, not implemented.

## Security Boundaries

1. Client to API: all request data is untrusted.
2. API to filesystem: only generated paths under fixed roots are permitted.
3. Quarantine to scanner: content remains untrusted during scanning.
4. Scanner to clean storage: promotion requires an exact conclusive clean result.
5. User to file: authentication and ownership are separate checks.
6. Application to infrastructure: timeouts, cancellation, and fail-closed error classification are required.

## Planned Persistent Entities

Do not implement these before their dedicated task:

- `ApplicationUser`
- `FileRecord`
- `ScanAttempt`
- `AuditEvent`

Do not create a migration until persistent entities and their EF configurations exist.

## Current State

The solution was scaffolded in Visual Studio with:

- `FileSentry.Api`;
- `FileSentry.UnitTests`;
- `FileSentry.IntegrationTests`;
- controller-based Web API and OpenAPI;
- root README and Git repository structure.

The current milestone is defined only in `CURRENT_TASK.md`. Update this status after a milestone is verified.

## Explicit Non-Goals for the Initial Release

- frontend or mobile UI;
- microservices;
- public sharing;
- cloud object storage;
- message broker;
- multi-engine scanning;
- sandbox execution;
- OCR, document extraction, or LLM analysis;
- content disarm and reconstruction;
- Kubernetes or multi-region deployment;
- resumable uploads;
- claiming that antivirus can guarantee harmless content.

## Definition of a Trustworthy Implementation

- clean clone builds and tests;
- setup is reproducible;
- secrets are externalized;
- all dependency failures are safe;
- workflow state survives restarts;
- object ownership is enforced in database queries;
- tests cover adversarial and failure cases;
- documentation distinguishes implemented behavior from planned behavior.

