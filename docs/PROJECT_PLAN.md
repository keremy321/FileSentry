# FileSentry Project Plan

FileSentry is a secure file-ingestion and malware-scanning API built with ASP.NET
Core, PostgreSQL, ClamAV, and Docker.

**Status:** The v1.0 MVP and release verification are complete. The first v1.1
deployment/integration milestone is recorded in `docs/ai/CURRENT_TASK.md`.

**Architecture:** .NET 10 modular monolith with a database-backed scanner worker.

**License:** MIT.

## Product principle

> Every uploaded file is untrusted until validation and malware scanning complete
> successfully.

The API authenticates a caller, streams supported content into protected quarantine,
calculates SHA-256, verifies the actual file format, persists a durable scan job,
scans bytes through ClamAV `INSTREAM`, and releases only conclusively clean content
through owner-protected endpoints.

## Implemented MVP

### Foundation and authentication

- ASP.NET Core API targeting the SDK pinned in `global.json`;
- PostgreSQL 17 with EF Core migrations and Identity-backed GUID users;
- registration, login, short-lived JWT bearer tokens, password policy, lockout, and
  per-IP authentication rate limiting;
- liveness and PostgreSQL/ClamAV readiness checks;
- full Docker Compose stack for the non-root API/worker, migrations, private
  PostgreSQL/ClamAV dependencies, and persistent upload storage.

### Secure ingestion

- authenticated `POST /api/v1/files` with a per-user upload rate limit;
- streaming multipart processing with an actual 10 MiB byte limit;
- SHA-256 calculation during streaming;
- generated internal storage names under fixed temp/quarantine/clean roots outside
  the web root;
- PDF, PNG, JPEG, and DOCX allowlist;
- server-side signature and extension consistency validation;
- Word Open XML package validation for DOCX;
- cleanup and rollback on validation, persistence, storage, cancellation, and other
  failed ingestion paths;
- initial durable `PendingScan` state and safe upload audit event.

### Durable scanning

- ClamAV `INSTREAM` client with bounded streaming and cancellation;
- PostgreSQL-backed claims and persisted `ScanAttempt` records;
- `PendingScan -> Scanning -> Clean/Infected/ScanFailed` transitions;
- fail-closed classification for timeout, connection, protocol, size, malformed,
  and unknown scanner results;
- bounded exponential retries and stale-claim recovery;
- clean-file promotion and infected-byte deletion;
- safe scanner result, detection, duration, and version metadata where available.

### Authorized file lifecycle

- owner-scoped list, metadata, clean download, and delete endpoints;
- ownership included directly in database predicates to prevent IDOR;
- identical not-found behavior for foreign and unknown identifiers;
- downloads restricted to matching `Clean` database and storage state;
- `nosniff` download response and sanitized display filename;
- scanner-coordinated deletion, stored-byte removal, and durable `Deleted` metadata.

### Security hardening and CI

- durable controlled-field audit events for upload, scanning, malware detection,
  failures, download, and delete;
- validated/generated request correlation IDs persisted into worker activity;
- structured workflow logs without file bytes, raw malware, credentials, or tokens;
- startup validation for security-critical configuration;
- least-privilege GitHub Actions CI covering exact SDK setup, restore, Compose
  validation, Release build, the complete Testcontainers test suite, formatting,
  and direct/transitive NuGet vulnerability audit;
- no CI dependency on repository secrets or developer User Secrets.

## Architecture and trust boundaries

The API and background worker are one deployable process. PostgreSQL is authoritative
for workflow state; directory contents alone never authorize access. Storage is
local, protected, and outside static web content. ClamAV receives streams rather than
paths.

Trust boundaries and component behavior are documented in
[architecture.md](architecture.md). Attacks, implemented mitigations, and residual
risk are documented in [threat-model.md](threat-model.md).

## Implemented API

Base path: `/api/v1`.

| Method | Route | Authentication | Result |
|---|---|---|---|
| `POST` | `/auth/register` | Anonymous, rate-limited | Creates an Identity user |
| `POST` | `/auth/login` | Anonymous, rate-limited | Issues a short-lived JWT |
| `GET` | `/auth/me` | Required | Returns the current user |
| `POST` | `/files` | Required, rate-limited | Accepts one valid upload with `202` |
| `GET` | `/files` | Required | Lists caller-owned metadata |
| `GET` | `/files/{fileId}` | Required | Returns caller-owned metadata |
| `GET` | `/files/{fileId}/download` | Required | Streams caller-owned `Clean` bytes |
| `DELETE` | `/files/{fileId}` | Required | Removes bytes and marks metadata deleted |
| `GET` | `/health/live` | Anonymous | Process liveness |
| `GET` | `/health/ready` | Anonymous | Process, database, and scanner readiness |

Existing file routes accept either a JWT user or the configured `X-Api-Key` service
identity. The service remains a normal owner: it cannot use auth-management routes,
see foreign files, or gain administrative permissions. `/auth/me` is JWT-only.

Errors use ProblemDetails with stable machine-readable codes. Cross-owner file IDs
do not reveal that a record exists.

## File and state policy

- Allowed: `.pdf`, `.png`, `.jpg`, `.jpeg`, `.docx`.
- Maximum: 10 MiB of bytes actually read from the file section.
- Client MIME is untrusted metadata.
- Only `Clean` is downloadable.
- `Infected` bytes are removed.
- Retryable `ScanFailed` content stays quarantined; no inconclusive result becomes
  clean.

```text
PendingScan -> Scanning -> Clean
                        -> Infected
                        -> ScanFailed -> PendingScan (bounded retry)

PendingScan / Scanning / Clean / Infected / ScanFailed -> Deleted
```

## Verification strategy

- Unit tests cover deterministic file validation, filename sanitization, multipart
  limits, ClamAV protocol parsing, and health behavior.
- PostgreSQL-backed HTTP integration tests cover authentication, ingestion,
  authorization, file lifecycle, auditing, rate limits, retry/recovery, and failure
  cleanup.
- Real ClamAV integration classifies clean and safely assembled EICAR test content.
- GitHub Actions runs the complete suite from a clean Ubuntu checkout using the
  existing Testcontainers architecture.

Test counts are intentionally not recorded here because they change as coverage
grows.

## Release plan

### Completed phases

1. Foundation, configuration, health, PostgreSQL, and ClamAV.
2. Identity/JWT authentication and secure streaming ingestion.
3. Durable ClamAV scanner worker, retries, and crash recovery.
4. Owner-authorized metadata, download, and delete operations.
5. Audit, correlation, rate limiting, operational hardening, and CI.

### Current v1.1 phase: containerized API and service authentication

- run the API/worker, migrations, PostgreSQL, ClamAV, and persistent storage as one
  Compose stack;
- keep database and scanner ports internal while exposing only the API;
- add an externally configured owner-scoped service credential without breaking
  JWT callers;
- keep SDKs, quotas, retention, cloud storage, and messaging in later milestones.

Local verification results and release blockers are recorded in
`docs/ai/CURRENT_TASK.md`. No tag or release is created by this milestone.

## Release acceptance criteria

- [x] Supported uploads are streamed, bounded, validated, hashed, and quarantined.
- [x] Scanner errors fail closed and durable state survives restarts.
- [x] Clean and infected content follows the correct storage lifecycle.
- [x] Metadata, download, and delete enforce owner predicates.
- [x] Audit, correlation, and focused rate limits are implemented.
- [x] The default-branch GitHub Actions run is observed green; the secret-free
  workflow and complete local suite pass.
- [x] Storage, `.env`, User Secrets, build output, and malware samples are excluded
  from source control.
- [x] MIT license is present.
- [x] Clean-clone documentation and local final release verification are complete.
- [ ] Any optional tag or GitHub release is explicitly approved and created later.

## Definition of Done

These requirements are retained for final verification; an item is not complete
merely because an implementation or document claims it is.

- [x] Core acceptance criteria pass.
- [x] Release build succeeds without warnings selected as errors.
- [x] Unit and integration test results are recorded.
- [x] GitHub Actions is green on the default branch.
- [x] Docker images run as non-root where feasible.
- [x] ClamAV is internal-only.
- [x] Database migrations are reproducible.
- [x] `.env.example` and configuration documentation are complete.
- [x] Threat model reflects the implemented behavior.
- [x] README includes architecture, setup, API examples, security controls, and
  limitations.
- [ ] Repository has a license, description, and relevant GitHub topics.
- [ ] Tagged release is created.

A recorded demo video is intentionally outside the release scope. The written
[`demo.md`](demo.md) guide remains the repeatable demonstration artifact.

## Limitations

- A ClamAV clean result is not proof that content is harmless.
- Local storage and the in-process worker target a single application host.
- ClamAV TCP has no TLS/authentication and is internal-only in the Compose topology.
- Rate limits are in-process, not distributed.
- There is no user storage quota, retention worker, public sharing, admin API/UI,
  sandbox execution, content disarm, preview, cloud storage, or message broker.
- Production operations such as TLS termination, managed secrets, backup/restore,
  monitoring, and host hardening are deployment responsibilities.

## Future enhancements

Only after final MVP verification:

- retention and quota policies;
- managed object storage and encryption/key management;
- independent/distributed worker architecture where scale requires it;
- multiple engines, YARA, sandboxing, or content disarm;
- administrative operational views;
- metrics/tracing and production deployment guidance;
- SBOM, artifact signing, and release automation.

Setup and usage are in the [README](../README.md); the repeatable presentation flow
is in [demo.md](demo.md).
