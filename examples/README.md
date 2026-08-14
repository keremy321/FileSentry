# FileSentry integration examples

These projects reference `src/FileSentry.Client` directly. No NuGet package is
published or required.

The required security sequence is:

```text
Upload -> Wait for terminal scan status -> continue only if Clean -> Download/process
```

`Infected`, `ScanFailed`, `Deleted`, timeout, cancellation, and unexpected responses
all stop processing. A `Clean` result reduces risk; it does not guarantee that a
file is harmless.

## Start FileSentry

Create the ignored local configuration and replace every placeholder with a local
test value as described in the repository README:

```powershell
Copy-Item deploy/.env.example deploy/.env
docker compose --env-file deploy/.env -f deploy/docker-compose.yml up --detach --build
docker compose --env-file deploy/.env -f deploy/docker-compose.yml ps --all
```

Wait until the API, PostgreSQL, and ClamAV are healthy and the migration service has
exited successfully. Then configure the examples with the same service key from
`deploy/.env` without printing it:

```powershell
$env:FILESENTRY_BASE_URL = 'http://127.0.0.1:8080'
$env:FILESENTRY_SERVICE_API_KEY = '<same-local-service-key-as-deploy/.env>'
```

Do not commit the key or pass it as a command-line argument.

## Console example

The console example streams one local file to FileSentry, displays only its ID and
scan status, and consumes the download into `Stream.Null` only after `Clean`:

```powershell
dotnet run --project examples/FileSentry.ConsoleExample -- 'C:\path\to\document.pdf'
```

It returns a nonzero exit code for infection, scan failure, timeout, cancellation,
configuration errors, transport errors, or API rejection. It never prints the key
or file content.

## ASP.NET Core example

The minimal backend uses `HttpClientFactory`, reads the inbound multipart file
section as a stream, and does not save or parse it locally. Start it on a dedicated
loopback port:

```powershell
$env:ASPNETCORE_URLS = 'http://127.0.0.1:5090'
dotnet run --project examples/FileSentry.AspNetExample
```

From another terminal with the same environment configuration:

```powershell
Invoke-WebRequest -Method Post -Uri 'http://127.0.0.1:5090/scan' `
    -Form @{ file = Get-Item 'C:\path\to\document.pdf' } `
    -OutFile '.\clean-document.pdf'
```

The endpoint returns file bytes only after FileSentry reports `Clean`. Non-clean
states and upstream failures return controlled ProblemDetails and no file content.

After the examples finish, remove their process-scoped configuration and stop the
stack when it is no longer needed:

```powershell
Remove-Item Env:\FILESENTRY_BASE_URL,Env:\FILESENTRY_SERVICE_API_KEY,Env:\ASPNETCORE_URLS `
    -ErrorAction SilentlyContinue
docker compose --env-file deploy/.env -f deploy/docker-compose.yml down
```
