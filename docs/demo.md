# Demonstration Guide

This walkthrough demonstrates implemented security controls without committing
credentials or a standalone malware sample. Complete the [README clean-clone
setup](../README.md#clean-clone-setup) first.

## 1. Show dependency and API health

```powershell
docker compose --env-file deploy/.env -f deploy/docker-compose.yml ps
Invoke-WebRequest http://localhost:5029/health/live
Invoke-WebRequest http://localhost:5029/health/ready
```

Expected: PostgreSQL and ClamAV are running, liveness succeeds, and readiness
succeeds only when both dependencies respond.

## 2. Authenticate

Use the registration/login example in the README to create `$headers`. Show that a
file request without the bearer token returns `401`.

## 3. Upload and scan a clean file

Choose a small valid PDF, PNG, JPEG, or DOCX no larger than 10 MiB:

```powershell
$upload = Invoke-RestMethod -Method Post -Headers $headers `
    -Uri 'http://localhost:5029/api/v1/files' `
    -Form @{ file = Get-Item 'C:\path\to\clean-sample.pdf' }
$upload
```

Poll the returned ID:

```powershell
$fileId = $upload.fileId
do {
    Start-Sleep -Seconds 1
    $metadata = Invoke-RestMethod -Headers $headers `
        -Uri "http://localhost:5029/api/v1/files/$fileId"
    $metadata.status
} while ($metadata.status -in @('PendingScan', 'Scanning'))
```

Expected: `PendingScan`/`Scanning` transitions to `Clean`. Download then succeeds:

```powershell
Invoke-WebRequest -Headers $headers `
    -Uri "http://localhost:5029/api/v1/files/$fileId/download" `
    -OutFile '.\verified-download.pdf'
```

Explain that download requires both ownership and conclusive clean state.

## 4. Show validation failure

Rename arbitrary non-PDF bytes to `.pdf` and upload them. Expected: a ProblemDetails
response with `INVALID_FILE_SIGNATURE` or `FILE_TYPE_MISMATCH`; no reusable record
or orphaned file remains. A path-like filename also cannot influence storage paths,
which use generated IDs.

## 5. Show object-level authorization

Register a second user and use that user's token to request the first user's file
ID. Metadata, download, and delete attempts return the same `404 FILE_NOT_FOUND`
shape as an unknown ID, preventing object-existence disclosure.

## 6. Show infected classification safely

Do not add a raw EICAR file to the repository. Run the existing real-ClamAV
integration test, which assembles the standard test value from fragments in memory:

```powershell
dotnet test tests/FileSentry.IntegrationTests/FileSentry.IntegrationTests.csproj `
    --configuration Release `
    --filter 'FullyQualifiedName~InStream_RealDaemon_ClassifiesCleanAndAssembledEicar'
```

Expected: the same real daemon classifies ordinary bytes as clean and the assembled
test value as infected. The broader scanner integration suite verifies infected
stored bytes are removed and scanner failures never become `Clean`.

## 7. Show CI evidence

Open the repository's successful `CI` workflow run and point out restore, Compose
validation, Release build, the complete Testcontainers suite, formatting, and NuGet
audit. CI uses no repository secrets.

## Closing statement

ClamAV reduces risk; it does not prove content harmless. The demonstration should
end with the limitations in the README and threat model rather than presenting
antivirus as a guarantee.
