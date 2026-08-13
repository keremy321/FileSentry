# Threat Model

## Scope and security objective

FileSentry protects the application host, stored content, user identities,
ownership metadata, database workflow state, audit evidence, and users who later
download files. Its primary objective is to prevent untrusted upload content from
becoming downloadable before both format validation and a conclusive clean scan.

This model covers the implemented single-host MVP. It does not claim to make
arbitrary documents safe.

## Trust boundaries

1. **Client to API:** headers, filenames, MIME claims, identifiers, multipart shape,
   and bytes are attacker-controlled.
2. **API to storage:** only fixed roots and generated internal names may cross this
   boundary.
3. **Quarantine to scanner:** valid-format content remains untrusted.
4. **Scanner response to clean storage:** only the exact complete clean response may
   authorize promotion.
5. **Authenticated user to file:** identity does not imply ownership of a supplied
   file ID.
6. **Application to dependencies:** PostgreSQL, storage, and ClamAV can be slow,
   unavailable, inconsistent, or interrupted.

## Threats and implemented controls

| Threat | Implemented control | Remaining risk |
|---|---|---|
| Executable or malformed bytes disguised as an allowed type | Extension allowlist, server-side signature/structure detection, extension-format consistency | Format validation is intentionally bounded and is not a complete parser-security proof |
| Arbitrary ZIP renamed to DOCX | Required Word Open XML package entries and relationships are validated | A structurally valid document can still contain malicious or deceptive content |
| Path traversal or filename injection | Sanitized display name; generated storage name; containment checks under fixed roots | Download clients must still handle filenames safely; framework content disposition is used |
| Oversized or memory-exhausting upload | Streaming multipart reader, actual byte counter, 10 MiB limit, bounded headers/sections | Disk and aggregate-user quotas are not implemented |
| Malware release before scanning | New records start `PendingScan` in quarantine; only `Clean` can download | Signatures can miss novel malware |
| Scanner outage, timeout, or malformed response | Fail-closed `ScanFailed`, bounded retry, no error path to `Clean` | Prolonged outage leaves quarantined files unavailable and consuming disk |
| Malware detection | `Infected` state, detection metadata retained, stored bytes promptly deleted | Deletion cannot retroactively undo any external copy made before upload |
| Crash while scanning | Durable claims/attempts and stale `Scanning` recovery | Single-host storage remains an availability dependency |
| IDOR/cross-user access | Owner ID is included in list/get/download/delete database predicates; foreign IDs behave as missing | Compromise of a user's token grants that user's permissions until expiry |
| Credential guessing | Identity password policy, lockout, generic login errors, per-IP auth limiter | In-process rate limits are not shared across multiple instances |
| Upload flooding | Per-authenticated-user fixed-window limiter | No storage quota or distributed rate limit exists |
| Sensitive logs or audit records | Structured controlled fields; no bytes, tokens, passwords, keys, or arbitrary metadata | Operators must preserve secure logging configuration and database access controls |
| Audit loss | Audit writes participate in security-relevant persistence and fail safely | Database unavailability also blocks the associated operation |
| Direct file exposure | Storage outside web root; no static-file mapping; authorized API streaming only | Host filesystem permissions and backups must be secured operationally |
| ClamAV network exposure | Port 3310 bound to `127.0.0.1` for host development | TCP has no TLS/authentication and must never be publicly exposed |

## File policy

- Maximum actual streamed size: 10 MiB.
- Accepted: PDF, PNG, JPEG (`.jpg`/`.jpeg`), and Word Open XML DOCX.
- Rejected: empty files, unknown or mismatched signatures, generic archives,
  executable/script types, SVG/HTML, legacy `.doc`, macro-enabled `.docm`, and
  malformed DOCX packages.
- Client MIME type is metadata only and never selects the detected format.

## Audit and correlation

The API returns a validated/generated correlation ID on every request and persists
the upload correlation with workflow state. Controlled audit events record upload,
scan, malware, failure, download, and deletion activity with relevant actor/file
IDs. There is deliberately no arbitrary metadata JSON or raw-content field.

## Residual risk

A ClamAV clean result means no configured signature or heuristic identified the
content at scan time. It is not a guarantee of harmlessness. Zero-day malware,
malicious document logic, vulnerable parser behavior, phishing, and social
engineering may remain.

Production operators additionally need TLS termination, managed secrets, operating
system least privilege, encrypted and tested backups, monitoring, signature-update
oversight, retention/quota policies, incident response, and dependency patching.
Those operational controls are outside this repository's local MVP.
