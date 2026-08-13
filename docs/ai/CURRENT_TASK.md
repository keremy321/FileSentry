# Current Task: Release Documentation Hardening

## Objective

Synchronize the repository documentation with the verified FileSentry MVP and make
local setup reproducible from a clean clone. This milestone changes documentation
and release-supporting repository files only; application behavior remains intact.

## Scope

- Add a complete README covering architecture, security controls, local setup,
  configuration, migrations, API usage, tests, CI, and residual risk.
- Correct stale planned/future wording in the project plan and project context.
- Add concise architecture, threat-model, and demonstration references where they
  provide lasting value.
- Add and verify the MIT license.
- Confirm `.env.example`, ignore rules, Compose, CI, and tracked files are safe and
  consistent with the documented setup.
- Run the full reproducibility command set and inspect final status/diff.

## Accuracy Boundaries

- Docker Compose starts PostgreSQL and ClamAV; the API currently runs on the host.
- Local API startup requires a PostgreSQL connection string and JWT signing key in
  .NET User Secrets (or equivalent external configuration).
- PostgreSQL remains the workflow source of truth and storage remains outside the
  web root.
- Only conclusively `Clean`, owner-scoped files are downloadable.
- A ClamAV clean result reduces risk but never guarantees harmless content.
- Documentation must distinguish the implemented MVP from limitations and future
  enhancements without recording volatile test counts.

## Out of Scope

- application features, entities, endpoints, or scanner changes;
- admin or frontend functionality;
- cloud/deployment work, CodeQL, or release automation;
- commits, pushes, tags, GitHub releases, or remote repository changes.

## Verification

Run restore, Release build, the full test suite, formatting verification, migration
listing, Docker Compose config validation with the ignored local environment file,
NuGet vulnerability audit, and diff/status checks. Compare the documented startup
steps against actual configuration and container health where locally available.

Recommended next milestone after verified completion: `chore/final-verification`.

## Verified completion

Completed and locally verified on 2026-08-14. The repository now includes an
implementation-accurate README, MIT license, focused architecture/threat/demo
guides, a current project plan and project context, and expanded API request samples.

The pinned tool and solution restore, zero-warning Release build, full unit and
integration suite, formatting verification, migration list/database update, Docker
Compose validation and healthy startup, NuGet vulnerability audit, manual API
liveness/readiness/OpenAPI/correlation probes, link/artifact checks, and diff checks
all completed successfully. The previously implemented hosted GitHub Actions
workflow has also been observed passing without repository secrets.

No application feature, commit, push, tag, GitHub release, or remote setting was
created or changed.
