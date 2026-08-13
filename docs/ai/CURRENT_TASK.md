# Current Task: Infrastructure and Health Checks

## Objective

Add local PostgreSQL and ClamAV infrastructure and expose separate ASP.NET Core liveness and readiness endpoints.

The API continues to run from Visual Studio on Windows. PostgreSQL and ClamAV run in Docker. This is an infrastructure milestone only.

## In Scope

- `deploy/.env.example`
- ignored local `deploy/.env` when needed for verification
- `deploy/docker-compose.yml`
- PostgreSQL 17 with persistent named storage and health check
- official ClamAV 1.4 base image with persistent signatures
- loopback-only host bindings while the API runs outside Docker
- EF Core PostgreSQL registration
- empty `FileSentryDbContext`
- strongly typed, startup-validated ClamAV options
- direct TCP `PING`/`PONG` ClamAV health check
- `/health/live`
- `/health/ready`
- required compatible NuGet dependencies
- build, test, Compose, and health behavior verification
- small README corrections if setup/status becomes inaccurate

## Out of Scope

- Identity, users, or JWT
- file entities or other domain entities
- database migrations
- uploads or storage services
- format detection or SHA-256 hashing
- `INSTREAM` malware scanning
- scan background worker
- retries or durable scan jobs
- frontend or deployment

## Docker Requirements

Create `deploy/.env.example` with:

```env
POSTGRES_DB=filesentry
POSTGRES_USER=filesentry
POSTGRES_PASSWORD=replace-with-a-strong-local-password
POSTGRES_PORT=5432
CLAMAV_PORT=3310
```

The root `.gitignore` must contain:

```gitignore
data/
deploy/.env
```

Compose project name: `filesentry`.

### PostgreSQL Service

- Image: `postgres:17`
- Container: `filesentry-postgres`
- Environment supplied from Compose interpolation of `.env`
- Host binding: `127.0.0.1:${POSTGRES_PORT:-5432}:5432`
- Named data volume
- `pg_isready` health check
- `restart: unless-stopped`

### ClamAV Service

- Image: `clamav/clamav:1.4_base`
- Container: `filesentry-clamav`
- Persist `/var/lib/clamav` in a named volume
- Configure a sufficient `CLAMD_STARTUP_TIMEOUT`
- Host binding: `127.0.0.1:${CLAMAV_PORT:-3310}:3310`
- `restart: unless-stopped`
- Document that the host port mapping is temporary and must be removed after the API is containerized

Never bind ClamAV to all interfaces. ClamD TCP has no authentication or encryption.

## Required Packages

Inspect existing package references first. Add only missing .NET/EF Core 10-compatible packages:

- `Npgsql.EntityFrameworkCore.PostgreSQL`
- `Microsoft.EntityFrameworkCore.Design`
- `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore`

Do not install a third-party ClamAV client and do not mix EF Core major versions.

## Database Context

Create `Persistence/FileSentryDbContext.cs`:

- `sealed`;
- inherits `DbContext`;
- constructor receives `DbContextOptions<FileSentryDbContext>`;
- contains no entities or `DbSet` properties;
- registered with `UseNpgsql`.

Connection-string key:

```text
ConnectionStrings:PostgreSql
```

Startup must fail clearly if it is missing. Do not commit its password. Use User Secrets or an environment variable for local verification.

Do not create a migration during this task.

## ClamAV Options

Create `Infrastructure/Options/ClamAvOptions.cs` with section name `ClamAV` and:

- `Host`, default `127.0.0.1`;
- `Port`, default `3310`;
- `TimeoutSeconds`, default `5`.

Bind using the options pattern, validate on startup, and require:

- non-empty host;
- port from 1 through 65535;
- timeout from 1 through 30 seconds.

Commit only non-secret ClamAV configuration.

## ClamAV Health Check

Create `Infrastructure/Health/ClamAvHealthCheck.cs` implementing `IHealthCheck`.

Required behavior:

1. inject `IOptions<ClamAvOptions>`;
2. use `TcpClient`;
3. create a linked cancellation source and apply the configured timeout;
4. connect to the configured host and port;
5. send ASCII bytes for null-terminated `zPING\0`;
6. read and normalize the response;
7. return healthy only for exact `PONG`;
8. return unhealthy for timeout, connection failure, empty response, malformed response, or any other inconclusive outcome;
9. distinguish caller cancellation from internal timeout;
10. dispose resources correctly;
11. do not reveal secrets in health output.

This task must not implement `INSTREAM` or scan files.

## Endpoint Semantics

Register health checks:

| Name | Tags | Behavior |
|---|---|---|
| `self` | `live`, `ready` | Always healthy while the process can execute it |
| `postgresql` | `ready` | EF Core DbContext connectivity |
| `clamav` | `ready` | ClamD exact `PING`/`PONG` probe |

Map:

```text
GET /health/live
GET /health/ready
```

- Liveness executes only checks tagged `live`.
- Readiness executes only checks tagged `ready`.
- PostgreSQL or ClamAV failure must not make liveness fail.
- PostgreSQL or ClamAV failure must make readiness unhealthy.
- Preserve controllers and development OpenAPI.
- Add `public partial class Program;` for later integration testing.

## Verification

Run and report:

```powershell
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
docker compose --env-file deploy/.env -f deploy/docker-compose.yml config
docker compose --env-file deploy/.env -f deploy/docker-compose.yml up -d
docker compose --env-file deploy/.env -f deploy/docker-compose.yml ps
```

Allow several minutes for ClamAV's initial signature initialization. Inspect service logs if it is not ready.

Verify with the API running:

1. both dependencies available:
   - `/health/live` is healthy;
   - `/health/ready` is healthy;
2. ClamAV stopped:
   - `/health/live` remains healthy;
   - `/health/ready` becomes unhealthy;
3. ClamAV restarted and ready:
   - `/health/ready` returns to healthy.

If the environment prevents a check, do not claim it passed. Record the exact limitation.

## Acceptance Criteria

- Docker Compose syntax is valid.
- PostgreSQL becomes healthy.
- ClamAV responds to the application `PING` probe after initialization.
- API configuration contains no committed database password.
- Missing connection string or invalid ClamAV options fail startup clearly.
- `/health/live` is dependency-independent.
- `/health/ready` checks both PostgreSQL and ClamAV.
- Release build succeeds.
- Tests pass.
- No migration or future-phase implementation is added.
- Final report lists changed files, packages, commands, results, and unverified items.

## Suggested Commit Message

```text
feat: add PostgreSQL and ClamAV infrastructure health checks
```

Do not create the commit unless explicitly requested.

