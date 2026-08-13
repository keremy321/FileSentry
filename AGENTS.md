# FileSentry Agent Instructions

This file contains stable repository-wide instructions for coding agents. Keep it concise and update it only when a lasting project rule changes.

## Required Reading Order

Before making changes:

1. Read `docs/ai/CURRENT_TASK.md` completely.
2. Read `docs/ai/PROJECT_CONTEXT.md` completely.
3. Inspect the affected source files, project files, configuration, tests, and Git status.
4. Read relevant README sections only when the two AI documents do not answer a question.

Do not repeatedly summarize these files. Treat them as authoritative working context.

## Authority Order

When instructions differ, use this order:

1. the user's current request;
2. `docs/ai/CURRENT_TASK.md`;
3. this file;
4. `docs/ai/PROJECT_CONTEXT.md`;
5. `README.md`;
6. existing implementation patterns.

Report a material conflict instead of silently choosing an incompatible interpretation.

## Working Method

- Inspect before editing.
- Preserve unrelated user changes and existing correct behavior.
- Make the smallest coherent change that completes the current milestone.
- Do not implement future phases early.
- Do not add abstractions without a current use case.
- Do not create commits, push branches, publish images, deploy, or modify remote resources unless explicitly requested.
- Use non-destructive commands and never discard user work.
- Update documentation only when behavior, setup, or a lasting decision changes.

## Verification

For every implementation task:

1. restore dependencies;
2. build in Release mode;
3. run relevant tests;
4. run safe milestone-specific checks;
5. inspect the final diff and Git status;
6. report what was and was not verified.

Never claim a command or test passed unless it was actually run successfully. If Docker, network access, credentials, or another dependency is unavailable, validate what is possible and state the exact blocker.

## Project Boundaries

- The system is an ASP.NET Core modular monolith, not a microservice system.
- The API, background scanner, persistence, and security components remain in the same deployable application for the initial release.
- PostgreSQL is the source of truth for workflow state.
- File storage remains outside the web root.
- ClamAV is an internal infrastructure dependency.
- A frontend, cloud storage, message broker, Kubernetes, LLM features, OCR, and document preview are outside the initial release.

## Security Invariants

These rules must never be weakened:

- Treat every uploaded byte, filename, extension, MIME type, and identifier as untrusted.
- Store uploads in quarantine until a conclusive clean scan result exists.
- Scanner error, timeout, or unknown response must never mean `Clean`.
- Never build storage paths from user-controlled filenames.
- Never expose quarantine or clean directories as static web content.
- Authenticate callers and enforce ownership on every file read, download, and delete operation.
- Do not reveal whether another user's file identifier exists.
- Enforce size limits while streaming; do not buffer full uploads in memory.
- Do not log file contents, bearer tokens, passwords, secrets, or raw malware samples.
- Do not commit credentials, `.env`, runtime uploads, database files, antivirus databases, or generated build artifacts.
- ClamAV TCP port 3310 has no transport security. Bind it to loopback only while the API runs on the host; remove the host mapping after the API is containerized.

## C# and ASP.NET Conventions

- Target .NET 10 unless the task explicitly changes it.
- Enable nullable reference types and implicit usings.
- Use file-scoped namespaces and `sealed` classes where inheritance is not intended.
- Prefer constructor injection, asynchronous I/O, cancellation tokens, and options validation at startup.
- Use UTC timestamps.
- Use `ProblemDetails` for HTTP errors and stable machine-readable error codes.
- Keep controllers thin; business and infrastructure logic belong in focused services.
- Use EF Core migrations for schema changes, but never create an empty or premature migration.
- Do not mix EF Core package major versions.
- Prefer framework functionality over unnecessary third-party packages.

## Tests

- Unit-test deterministic validation, parsing, policies, and state transitions.
- Integration-test HTTP behavior and real infrastructure boundaries where practical.
- Use real PostgreSQL and ClamAV containers for at least one integration path once those behaviors exist.
- Include negative and fail-closed behavior, not only success cases.
- Do not commit the raw EICAR string as a standalone file; assemble it safely in a test when required.

## Completion Report

At the end of a task, report:

- outcome;
- files changed;
- important decisions;
- packages added or changed;
- commands/tests run and their results;
- unverified behavior or blockers;
- safe next step.

