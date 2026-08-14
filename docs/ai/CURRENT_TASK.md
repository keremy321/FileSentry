# Current Task: Final MVP Verification

## Outcome

Final local verification completed on 2026-08-14 from an isolated clone of commit
`8bf1218`. FileSentry is **not yet ready to tag as `v1.0.0`**: the demo video
required by the Definition of Done is not linked, and the current default-branch
GitHub Actions result plus remote repository description/topics were not observable
from this environment.

No application feature, commit, push, tag, release, or remote setting was created
or changed.

## Acceptance criteria

| # | Status | Evidence |
|---|---|---|
| 1 | PASS | Live and automated checks proved streaming upload, 10 MiB enforcement, SHA-256/format validation, generated storage, quarantine, all supported formats, `400 FILE_TYPE_MISMATCH`, and `413 FILE_TOO_LARGE`. |
| 2 | PASS | With ClamAV stopped through all three attempts, a live upload remained `ScanFailed` before and after scanner recovery; automated retry, persistence, and stale-claim tests passed. |
| 3 | PASS | A live clean PDF was promoted and downloaded byte-for-byte; a valid PDF stream containing runtime-assembled canonical EICAR became `Infected`, storage state `Deleted`, with no bytes under clean or quarantine. |
| 4 | PASS | Owner metadata/download/delete worked; a second authenticated user received identical `404` results for metadata, download, and delete. PostgreSQL-backed endpoint tests passed. |
| 5 | PASS | Live audit rows recorded upload/scan/clean/malware/failure/download/delete with file, actor where applicable, and persisted correlation IDs. Deterministic limits returned `AUTH_RATE_LIMITED` and `UPLOAD_RATE_LIMITED`. |
| 6 | NOT VERIFIED | The least-privilege, secret-free workflow is present and all 106 tests pass locally, but the current hosted GitHub Actions result was not observable (`gh` unavailable and the unauthenticated repository API returned `404`). |
| 7 | PASS | Tracked-file inspection found no `deploy/.env`, runtime `data`, build/test output, private keys, JWT-like values, or contiguous raw EICAR sample; ignore checks cover `.env` and runtime storage. |
| 8 | PASS | `LICENSE` contains the MIT license. |
| 9 | PASS | The documented setup was exercised in an isolated clone without inherited `.env`, storage, package cache, or developer configuration; restore/build/test/format/migrations/Compose and API startup succeeded. |
| 10 | NOT APPLICABLE | A tag or GitHub release is a later explicitly approved action and was prohibited for this milestone. |

## Definition of Done

| Item | Status | Evidence |
|---|---|---|
| Core acceptance criteria pass | NOT VERIFIED | All locally testable core behavior passed; the hosted-CI acceptance criterion remains unverified. |
| Release build succeeds without warnings selected as errors | PASS | Isolated `Release` build completed with 0 warnings and 0 errors. |
| Unit and integration test results are recorded | PASS | 31 unit and 75 integration tests passed; 106 total, 0 failed, 0 skipped. |
| GitHub Actions is green on the default branch | NOT VERIFIED | Current hosted status was not accessible from this environment. |
| Docker images run as non-root where feasible | PASS | Runtime inspection showed PostgreSQL as UID 999 and `clamd`/`freshclam` as the unprivileged `_rpc` account; the upstream ClamAV init/tail helpers remain root. |
| ClamAV is internal-only | PASS | Compose binds ClamAV only to `127.0.0.1`; live inspection confirmed the loopback binding. |
| Database migrations are reproducible | PASS | All five migrations applied to fresh PostgreSQL; a repeat update reported the database current. |
| `.env.example` and configuration documentation are complete | PASS | The example contains local placeholders only; README documents `.env`, User Secrets, validated settings, migrations, and startup. |
| Threat model reflects implemented behavior | PASS | Threat, control, and residual-risk statements match inspected code and live results. |
| README includes architecture, setup, API examples, security controls, and limitations | PASS | All required sections are present, including that a clean result is not proof of harmlessness. |
| Repository has a license, description, and relevant GitHub topics | NOT VERIFIED | MIT license is present; remote description and topics were not observable. |
| Demo video is linked | FAIL | `docs/demo.md` contains a repeatable script but no video link. |
| Tagged release is created | NOT APPLICABLE | Tagging/releasing was explicitly prohibited and belongs after release approval. |

## Executed verification

- `dotnet tool restore`: restored `dotnet-ef` 10.0.11.
- `dotnet restore FileSentry.slnx`: passed from an empty isolated package cache.
- `dotnet build FileSentry.slnx --configuration Release`: passed, 0 warnings and
  0 errors.
- `dotnet test FileSentry.slnx --configuration Release`: passed 31 unit and 75
  integration tests (106 total), including real PostgreSQL and ClamAV containers.
- `dotnet format FileSentry.slnx --verify-no-changes`: passed.
- `dotnet ef migrations list --project src/FileSentry.Api`: listed all five
  migrations; `dotnet ef database update --project src/FileSentry.Api` applied them
  to fresh PostgreSQL and a repeat run was a successful no-op.
- `docker compose --env-file deploy/.env -f deploy/docker-compose.yml config
  --quiet`: passed with an isolated ignored environment file.
- NuGet direct/transitive vulnerability audit: no vulnerable packages reported by
  the configured sources.
- Fresh PostgreSQL and ClamAV containers became healthy; the API started from the
  isolated clone and returned live/ready `200` under normal conditions.
- `git diff --check`, local Markdown-link validation, and final status/diff
  inspection passed.

## Release blockers

1. Record an observable green GitHub Actions run for current `main`/release commit.
2. Add the required demo video link, or explicitly remove that requirement through
   a deliberate project-plan decision.
3. Verify or configure the remote repository description and relevant topics.

After those items are resolved, repeat only the affected remote/documentation checks
and approve the separate `v1.0.0` tag/release action.
