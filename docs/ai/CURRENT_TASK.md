# Current Task: C# Client SDK Foundation

## Milestone

`feat/client-sdk-foundation` is the second FileSentry `v1.1.0` milestone. It adds a
reusable .NET 10 client library for the existing `/api/v1/files` service API without
changing server workflow or authentication behavior.

## Scope

- Add a packable `src/FileSentry.Client` project to the solution with pre-release
  NuGet metadata, no publishing credentials, and no unnecessary dependencies.
- Provide asynchronous `UploadAsync`, `GetFileAsync`, `ListFilesAsync`,
  `WaitForScanAsync`, `DownloadAsync`, and `DeleteAsync` operations.
- Attach the configured `X-Api-Key` to each SDK request without mutating shared
  `HttpClient.DefaultRequestHeaders`.
- Stream caller-owned upload content and response-owned download content without
  buffering whole files or exposing server storage details.
- Model public file metadata/status and preserve bounded ProblemDetails fields in
  typed exceptions without exposing the configured credential.
- Poll at configurable intervals with a bounded timeout, stop on every terminal
  state, and fail safely on unknown or malformed status responses.
- Add deterministic fake-handler tests plus a PostgreSQL-backed real API/worker
  integration path that uploads, reaches `Clean`, downloads byte-identically, and
  deletes through the SDK.

## Security decisions

- The server remains authoritative for hashing, file validation, ownership,
  scanning, and clean-download authorization; the SDK duplicates none of those
  decisions.
- `Clean` is the only status a consumer should use to continue to download or
  process content. `Infected`, `ScanFailed`, and `Deleted` are terminal but never
  successful; unknown states produce a protocol failure rather than being treated
  as clean or polled indefinitely.
- Uploads are never retried automatically, and caller-provided upload streams are
  not owned or disposed by the SDK.
- A returned download stream owns its HTTP response and releases it when disposed.
- API keys, upload bytes, and download bytes are not logged or included in SDK
  exception text. Any reflected configured key is redacted from parsed error data.

## Out of scope

NuGet publishing, Python SDK, webhooks, automatic upload retries, dependency-
injection extensions, quotas/retention, cloud storage, UI, API v2, and scanning
changes remain later milestones.

## Verification target

Restore, zero-warning Release build, full tests, formatting, direct/transitive
NuGet vulnerability audit, package creation/metadata inspection, final diff check,
and Git status inspection must pass. The real API integration path must demonstrate
an SDK upload reaching `Clean` and a byte-identical streamed download.

## Verified implementation state

- The public SDK exposes `UploadAsync`, `GetFileAsync`, `ListFilesAsync`,
  `WaitForScanAsync`, `DownloadAsync`, and `DeleteAsync` with cancellation support.
- Uploads are multipart streamed without taking ownership of the caller stream;
  downloads remain unread until consumed and release their HTTP response when the
  returned stream is disposed.
- Fourteen deterministic SDK tests cover per-request credentials, streaming,
  response models, download lifetime, delete, clean/infected/failed polling,
  cancellation, ProblemDetails mapping/redaction, and malformed/unknown responses.
- A PostgreSQL-backed API/worker integration test verified SDK upload from
  `PendingScan` to `Clean`, byte-identical streamed download, and deletion.
- The exact pinned SDK restored and built all four projects in Release with zero
  warnings and zero errors. All 45 unit and 84 integration tests pass, and formatting
  verification succeeds.
- The direct/transitive NuGet audit reports no known vulnerable packages from the
  configured sources.
- `FileSentry.Client.1.1.0-preview.1.nupkg` packs successfully with the SDK README,
  MIT expression, repository metadata, `net10.0` assembly, and no runtime package
  dependencies. The disposable package was inspected and removed; nothing was
  published.

The updated hosted CI run remains unverified because this task does not commit or
push.

Recommended next milestone: `feat/sdk-integration-examples`.
