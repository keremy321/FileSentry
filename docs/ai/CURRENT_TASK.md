# Current Task: ASP.NET Core Identity and JWT Authentication

## Objective

Add PostgreSQL-backed ASP.NET Core Identity, account registration and login,
short-lived JWT bearer tokens, and a minimal authenticated identity endpoint.

The completed infrastructure and health-check milestone must remain intact.

## In Scope

- GUID-based `ApplicationUser` with `CreatedAtUtc`;
- Identity support in the existing `FileSentryDbContext`;
- initial Identity EF Core migration;
- `POST /api/v1/auth/register`;
- `POST /api/v1/auth/login`;
- `GET /api/v1/auth/me` protected by bearer authentication;
- strongly typed, startup-validated JWT configuration;
- a 15-minute HMAC-SHA256 access token containing `sub`, `email`, `jti`, and timestamps;
- Identity password policy and failed-login lockout;
- built-in, per-IP authentication endpoint rate limiting;
- stable `application/problem+json` authentication errors;
- development OpenAPI bearer metadata;
- unit and real-PostgreSQL integration verification.

## Configuration

The committed `Jwt` section contains only non-secret issuer, audience, and token
lifetime values. `Jwt:SigningKey` must be supplied through User Secrets or an
environment variable and must contain at least 32 bytes.

The PostgreSQL connection string remains externalized at
`ConnectionStrings:PostgreSql`.

## Security Behavior

- Passwords are hashed and verified only through ASP.NET Core Identity.
- Email addresses are normalized by Identity and uniquely indexed by normalized value.
- Unknown email, incorrect password, and locked-out login attempts receive the same external `INVALID_CREDENTIALS` response.
- Five failed password attempts lock an account for five minutes.
- Authentication endpoints allow ten attempts per IP per 60-second window by default and reject excess attempts with `AUTH_RATE_LIMITED`.
- Tokens require the configured issuer, audience, signature, and expiry, with a 30-second clock skew.
- Missing, malformed, unsigned, expired, incorrectly signed, wrong-issuer, and wrong-audience tokens fail closed.
- Health endpoints remain anonymous.

## Out of Scope

- refresh tokens, roles, administrators, email confirmation, password reset, or external identity providers;
- uploads, storage, hashing, format validation, or file entities;
- ClamAV `INSTREAM`, scan workers, scan attempts, retries, or audit events;
- file ownership and authorization endpoints;
- frontend or deployment changes.

## Verification

The milestone requires a clean restore and Release build, all unit and
integration tests, a clean NuGet vulnerability audit, successful migration list
and database update commands, startup configuration validation, and a manual
register/login/JWT/`me` flow against local PostgreSQL and ClamAV.

Do not create a commit unless explicitly requested.
