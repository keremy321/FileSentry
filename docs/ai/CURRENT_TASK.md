# Current Task: Final Release Readiness

## Outcome

The FileSentry MVP's technical acceptance criteria are complete. Local final
verification passed, and GitHub Actions has now passed on the merged default branch.
A demo video will deliberately not be created and is no longer a release
requirement; [`docs/demo.md`](../demo.md) remains the written repeatable guide.

FileSentry is **not yet ready to tag as `v1.0.0`** because the remote repository
description and relevant GitHub topics have not been verified. No application
feature, commit, push, tag, release, or remote setting was created or changed.

## Acceptance criteria

| # | Status | Evidence |
|---|---|---|
| 1 | PASS | Live and automated checks proved streaming upload, 10 MiB enforcement, SHA-256/format validation, generated storage, quarantine, all supported formats, `400 FILE_TYPE_MISMATCH`, and `413 FILE_TOO_LARGE`. |
| 2 | PASS | With ClamAV stopped through all three attempts, a live upload remained `ScanFailed` before and after scanner recovery; automated retry, persistence, and stale-claim tests passed. |
| 3 | PASS | A live clean PDF was promoted and downloaded byte-for-byte; a valid PDF stream containing runtime-assembled canonical EICAR became `Infected`, storage state `Deleted`, with no bytes under clean or quarantine. |
| 4 | PASS | Owner metadata/download/delete worked; a second authenticated user received identical `404` results for metadata, download, and delete. PostgreSQL-backed endpoint tests passed. |
| 5 | PASS | Live audit rows recorded upload/scan/clean/malware/failure/download/delete with file, actor where applicable, and persisted correlation IDs. Deterministic limits returned `AUTH_RATE_LIMITED` and `UPLOAD_RATE_LIMITED`. |
| 6 | PASS | The least-privilege, secret-free workflow passed successfully on merged `main`; the same complete 106-test suite passes locally. |
| 7 | PASS | Tracked-file inspection found no `deploy/.env`, runtime `data`, build/test output, private keys, JWT-like values, or contiguous raw EICAR sample; ignore checks cover `.env` and runtime storage. |
| 8 | PASS | `LICENSE` contains the MIT license. |
| 9 | PASS | The documented setup was exercised in an isolated clone without inherited `.env`, storage, package cache, or developer configuration; restore/build/test/format/migrations/Compose and API startup succeeded. |
| 10 | INCOMPLETE | No tag or GitHub release has been created; that remains a separate explicitly approved action. |

## Definition of Done

| Item | Status | Evidence |
|---|---|---|
| Core acceptance criteria pass | PASS | All technical acceptance criteria passed locally and hosted CI is green on the default branch. |
| Release build succeeds without warnings selected as errors | PASS | Release verification completed with 0 warnings and 0 errors. |
| Unit and integration test results are recorded | PASS | 31 unit and 75 integration tests passed; 106 total, 0 failed, 0 skipped. |
| GitHub Actions is green on the default branch | PASS | The hosted workflow passed successfully on merged `main`. |
| Docker images run as non-root where feasible | PASS | PostgreSQL ran as UID 999 and `clamd`/`freshclam` as the unprivileged `_rpc` account; upstream ClamAV init/tail helpers remain root. |
| ClamAV is internal-only | PASS | Compose binds ClamAV only to `127.0.0.1`; live inspection confirmed the loopback binding. |
| Database migrations are reproducible | PASS | All five migrations applied to fresh PostgreSQL; a repeat update reported the database current. |
| `.env.example` and configuration documentation are complete | PASS | The example contains local placeholders only; README documents `.env`, User Secrets, validated settings, migrations, and startup. |
| Threat model reflects implemented behavior | PASS | Threat, control, and residual-risk statements match inspected code and live results. |
| README includes architecture, setup, API examples, security controls, and limitations | PASS | All required sections are present, including that a clean result is not proof of harmlessness. |
| Repository has a license, description, and relevant GitHub topics | NOT VERIFIED | MIT license is present; remote description and topics remain unverified. |
| Tagged release is created | INCOMPLETE | No tag or release exists yet, as required for this pre-tag milestone. |

The former demo-video item was intentionally removed from the Definition of Done.
No nonexistent video is claimed as complete or required for release.

## Verification record

- Isolated restore, zero-warning Release build, all 106 tests, formatting,
  migrations, Compose validation, dependency health, and the NuGet vulnerability
  audit passed during final verification.
- Clean, infected, retry-exhausted ClamAV-outage, ownership, deletion,
  audit/correlation, and authentication/upload rate-limit flows passed.
- GitHub Actions passed on the merged default branch without repository secrets or
  developer User Secrets.
- The release-readiness Release build passed with 0 warnings and 0 errors; all 106
  tests passed again. `git diff --check` passed after the documentation update.

## Remaining release blocker

Verify or configure the GitHub repository description and relevant topics. Once
that repository-level item is complete, FileSentry is ready for a separately
approved `v1.0.0` tag/release action.
