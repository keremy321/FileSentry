# Current Task: C# Client SDK Release Preparation

## Milestone

`chore/client-sdk-release` prepares `FileSentry.Client` for its first public NuGet
preview without publishing, tagging, or changing the server API.

## Scope

- Review every exported SDK type for naming, nullability, compatibility, and
  stream/HTTP ownership clarity using the console and ASP.NET examples as evidence.
- Keep the first package version at `1.1.0-preview.1` while the public API receives
  its first external feedback.
- Finalize NuGet metadata, package README, XML API documentation, portable symbols,
  and Source Link metadata without adding runtime dependencies.
- Ensure CI restores, builds, tests, formats, audits, and packs the SDK without any
  publishing credential.
- Build and inspect real `.nupkg` and `.snupkg` artifacts, then remove them.

## Compatibility and security decisions

- Preserve the existing API and `/api/v1` wire contract. No breaking member rename,
  signature change, or server behavior is justified by the working examples.
- Caller-provided upload streams and injected `HttpClient` instances remain owned
  by the caller. A downloaded stream owns its HTTP response and must be disposed.
- Only exact `Clean` permits content use. Unknown statuses remain protocol errors,
  and `Clean` reduces risk without guaranteeing harmless content.
- API keys remain external configuration, per-request headers, and redacted from
  parsed API errors. No credential or publishing automation is added.

## Out of scope

NuGet publication, publishing credentials, tags/releases, stable `1.1.0`, Python or
other SDKs, new server endpoints, webhooks, integrations, AI features, and API v2
remain later work.

## Verification target

Restore, zero-warning Release build, all tests, formatting, direct/transitive NuGet
audit, package creation, archive/metadata/source-symbol inspection, `git diff
--check`, and final status inspection must pass. Generated package artifacts must
be removed after inspection.

## Verified implementation state

- The exported interface, concrete client, options, records, enum, exception
  hierarchy, and stream ownership were reviewed against both runnable examples.
  No method or wire-contract break was justified. The abstract base exception's
  accidental public constructor was narrowed to conventional `protected` access;
  external derived exceptions remain supported.
- `1.1.0-preview.1` remains the recommended first public version because the API
  has strong local/integration evidence but no public-consumer feedback yet.
- Package metadata now includes title, description, authors, MIT expression,
  project/repository URLs and commit, tags, release notes, README, XML API docs,
  portable symbols, and Source Link. There are no package dependencies.
- Final package inspection found seven intended `.nupkg` entries and five intended
  `.snupkg` entries: client DLL/XML docs/README plus package metadata, and the
  portable PDB plus symbol metadata. No server, example, test, configuration,
  secret, or build-junk entry was present; the README matched its source and the
  PDB contained the expected GitHub Source Link mapping.
- The pinned SDK restored and produced a zero-warning Release build. All 53 unit
  and 84 integration tests pass, formatting succeeds, and the configured NuGet
  sources report no known vulnerable direct or transitive packages.
- CI already builds/tests the full solution and packs the client without a
  publishing credential. The two inspected local package artifacts were removed;
  nothing was published, tagged, or released. Hosted CI for these uncommitted
  changes remains unverified.

Recommended next step: publish `1.1.0-preview.1` only after review, hosted CI, and
explicit NuGet publication authorization.
