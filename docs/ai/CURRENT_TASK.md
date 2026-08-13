# Current Task: Secure File Ingestion into Quarantine

## Status

Implemented and verified on `feat/secure-file-ingestion`. The next recommended
milestone is `feat/clamav-scanner-worker`.

## Objective

Implement authenticated, streaming file ingestion into non-public quarantine
storage. Persist a `FileRecord` in the initial `PendingScan` state only after
the upload has passed bounded streaming and server-side format validation.

The completed infrastructure, health, Identity, and JWT behavior must remain
intact.

## In Scope

- `POST /api/v1/files`, protected by JWT bearer authentication;
- `FileRecord` persistence, EF configuration, and a dedicated migration;
- authenticated owner ID persistence;
- fixed trusted `data/temp` and `data/quarantine` roots outside the web root;
- generated internal storage names unrelated to client filenames;
- direct multipart stream parsing without `IFormFile` or full-file buffering;
- an actual 10 MiB limit enforced while reading the file stream;
- SHA-256 calculation during the same streaming pass;
- safe display-only original filename metadata;
- `.pdf`, `.png`, `.jpg`, `.jpeg`, and `.docx` allowlisting;
- server-side PDF, PNG, JPEG, and DOCX detection;
- DOCX verification as a Word Open XML package rather than a generic ZIP;
- extension and detected-format consistency enforcement;
- transactional database/file publication and cleanup on every unsuccessful path;
- stable `application/problem+json` upload errors;
- deterministic validation unit tests and PostgreSQL-backed HTTP/persistence tests.

Successful ingestion returns `202 Accepted` with safe metadata: file ID,
display filename, byte count, SHA-256, detected media type, `PendingScan`, and
creation timestamp.

## File Policy

- Maximum file size: 10 MiB, enforced from bytes actually read.
- Client filename, extension, MIME type, multipart headers, and bytes are untrusted.
- Client MIME type is retained only as bounded metadata and never selects format.
- Original filenames never participate in filesystem path construction.
- PDF requires its header and terminal marker.
- PNG requires its standard binary signature.
- JPEG requires its standard start signature and end marker.
- DOCX requires valid ZIP structure, required Word package parts, content-type
  declaration, office-document relationship, and a WordprocessingML document root.

## Storage and Persistence Invariants

- Temporary and quarantine roots are canonical application-controlled paths.
- Storage roots must not be inside `wwwroot`.
- Temporary and quarantine filenames are generated identifiers.
- `FileRecord` is committed only with a successfully published quarantine file.
- Validation, size, multipart, storage, database, cancellation, and other failures
  remove temporary/quarantine artifacts and do not leave a committed record.
- PostgreSQL remains the source of truth for the persisted `PendingScan` workflow state.

## Error Codes

- `INVALID_MULTIPART`
- `FILE_REQUIRED`
- `MULTIPLE_FILES`
- `EMPTY_FILE`
- `FILE_TOO_LARGE`
- `UNSUPPORTED_EXTENSION`
- `INVALID_FILE_SIGNATURE`
- `INVALID_DOCX`
- `FILE_TYPE_MISMATCH`
- `UPLOAD_FAILED`

## Out of Scope

- ClamAV `INSTREAM` or any malware scan operation;
- scanner workers, `ScanAttempt`, retries, recovery, or state transitions beyond `PendingScan`;
- clean promotion, infected handling, download, list, metadata, or delete endpoints;
- `AuditEvent`, refresh tokens, frontend, or deployment changes.

## Verification

Run repository restore, Release build/tests, EF migration listing/update, formatting,
diff checks, the NuGet vulnerability audit, and manual/integration uploads against
local PostgreSQL. Verify success for every supported format and failure/cleanup for
adversarial input without adding scan or retrieval behavior.

Do not create a commit or push.
