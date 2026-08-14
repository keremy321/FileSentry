# Current Task: Containerized API and Service Authentication

## Milestone

`feat/containerized-api-service-auth` is the first FileSentry `v1.1.0` milestone.
It makes the existing API/worker deployable as part of one Docker Compose stack and
adds an additive credential for trusted backend callers without changing `/api/v1`
or weakening JWT user behavior.

## Scope

- Publish the .NET 10 API and background scanner in a production-style multi-stage
  image that runs as a non-root user.
- Start the API/worker, PostgreSQL, a one-shot migration command, and ClamAV through
  one Compose project with persistent temp/quarantine/clean storage.
- Keep PostgreSQL and ClamAV internal to the Compose network; only the API receives
  an explicitly configured host binding.
- Add startup-validated `X-Api-Key` service authentication using constant-time
  secret comparison and a configured passwordless internal owner identity.
- Permit service identities to upload, list, read metadata, download clean owned
  files, and delete owned files. Do not permit service access to registration,
  login, `/auth/me`, foreign files, or administrative behavior.
- Preserve Identity/JWT, scanning, ownership, audit/correlation, rate limiting,
  health endpoints, and all existing tests.

## Security decisions

- The service credential is supplied only through external configuration and is
  never returned, persisted, or logged.
- A service ID is a stable GUID and remains subject to the same database owner
  predicates and upload rate limiter as a JWT subject.
- The service owner row has no password and uses a reserved internal identifier;
  migration/provisioning fails on an identity collision.
- Requests containing a Bearer header are validated as JWT even if an API-key
  header is also present, preventing fallback from a bad bearer token.
- ClamAV continues receiving bytes through `INSTREAM`; storage is never mounted
  into the scanner container.

## Out of scope

C# or Python SDKs, webhooks, quotas, retention, cloud storage, message brokers,
public sharing, admin UI, and API redesign remain later milestones.

## Verification target

Restore, Release build, full tests, formatting, Compose validation, image build,
full-stack startup/migration/health, JWT and service-authenticated live calls, a
containerized clean upload/scan, internal-only dependency ports, vulnerability
audit, and final diff/status inspection must all be executed before completion.

## Verified implementation state

- The exact pinned .NET SDK restored and built the solution in Release with zero
  warnings and zero errors.
- All 31 unit and 83 integration tests pass. Coverage includes valid/missing/invalid
  service credentials, Bearer precedence, startup validation, passwordless service
  provisioning, owner isolation, the service/JWT file lifecycle, and secret-safe
  responses/logs.
- Formatting and the direct/transitive NuGet vulnerability audit pass; no known
  vulnerable packages were reported by the configured sources.
- Compose configuration and the multi-stage API image build pass. A clean
  disposable stack applied migrations, provisioned the service owner, reached
  healthy live/ready status, and ran the API process as UID 1654.
- Only the API was published, on loopback for verification. PostgreSQL and ClamAV
  had no host mappings. Fresh application volumes were owned by `app:app` with
  mode `700`.
- Live calls verified registration/login/JWT access, valid/missing/invalid service
  authentication, JWT/service cross-owner `404` behavior, a clean PDF upload from
  `PendingScan` to `Clean`, byte-identical download, deletion, and JWT-only
  `/auth/me`.
- Inspection found no configured signing key, service key, connection string, or
  untrusted upload filename in API logs or HTTP response data.

The updated GitHub Actions workflow now also builds the API image. Its hosted run
on this uncommitted branch is not verifiable locally and remains the only
environment-specific check.

Recommended next milestone: `feat/client-sdk-foundation`.
