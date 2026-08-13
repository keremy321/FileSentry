# Current Task: GitHub Actions CI Pipeline

## Objective

Add a focused GitHub Actions workflow that proves FileSentry restores, builds, and
passes its complete automated verification from a clean checkout without developer
User Secrets or repository credentials.

The completed application behavior and existing Testcontainers architecture must
remain unchanged.

## Workflow Scope

- Run for pull requests and pushes to `main`, with manual dispatch available.
- Grant read-only repository contents permission.
- Cancel superseded runs for the same workflow/ref.
- Use an Ubuntu hosted runner and a pinned .NET 10 SDK.
- Restore `FileSentry.slnx`.
- Validate Docker Compose configuration with explicit ephemeral test-only values.
- Pre-pull the PostgreSQL and ClamAV images already used by integration tests.
- Build the solution in Release configuration.
- Run the full unit and integration suite using the existing Testcontainers setup.
- Verify formatting and audit direct/transitive NuGet dependencies.

## Dependency Strategy

Integration tests continue to create disposable PostgreSQL and ClamAV containers;
the workflow must not create parallel service containers or depend on a checked-in
`.env`. PostgreSQL test passwords and JWT keys remain generated at test runtime.
Compose validation receives only non-secret, step-scoped placeholder values.

## Security and Maintainability

- Use only stable official GitHub Actions required for checkout and SDK setup.
- Pin the SDK version and keep action versions explicit.
- Do not grant write permissions, consume repository secrets, or persist test
  credentials.
- Do not deploy, publish images, create releases, or introduce cloud resources.
- Keep the workflow as one readable verification job unless an actual independent
  job boundary is needed.

## Verification

Locally run restore, Release build, full tests, formatting verification, NuGet
vulnerability audit, Docker Compose config validation, and diff/status checks.
Inspect the workflow YAML and report hosted GitHub runner execution as unverified
until the workflow has actually run after push.

## Out of Scope

- deployment, Docker publishing, or GitHub releases;
- CodeQL or additional security platforms;
- cloud infrastructure, frontend work, or unrelated refactoring.

Do not create a commit or push.

## Verified Completion

Implemented and locally verified on 2026-08-14. The workflow uses one read-only
Ubuntu job, an exact .NET 10 SDK, the existing PostgreSQL and ClamAV Testcontainers
architecture, and step-scoped non-secret values for Docker Compose validation.

Restore, Release build, all 106 tests, formatting verification, NuGet vulnerability
audit, Docker Compose validation, and diff checks completed successfully locally.
The workflow was inspected, but its first execution on a GitHub-hosted runner remains
unverified until the branch is pushed.

Recommended next milestone: `docs/release-hardening`.
