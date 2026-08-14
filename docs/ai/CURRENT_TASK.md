# Current Task: SDK Integration Examples

## Milestone

`feat/sdk-integration-examples` is the third FileSentry `v1.1.0` milestone. It
demonstrates that `FileSentry.Client` can be integrated safely into a command-line
tool and an ASP.NET Core backend without a published package or changes to
`/api/v1`.

## Scope

- Add project-reference-based console and minimal ASP.NET Core examples under
  `examples/` and include them in the solution.
- Read `FILESENTRY_BASE_URL` and `FILESENTRY_SERVICE_API_KEY` from external
  configuration with clear fail-fast validation.
- Demonstrate the required `Upload -> Wait -> Clean -> Download/process` sequence.
- Stream local console input, inbound ASP.NET multipart content, and clean download
  content without printing, parsing, or locally persisting untrusted bytes.
- Handle infected, failed, timeout, cancellation, protocol, transport, and API
  failures without exposing credentials or response content.
- Add only evidence-backed, backward-compatible SDK ergonomics: a public client
  interface for DI/testability and a public options-validation method for startup
  validation.
- Add lightweight tests for configuration failures and the invariant that the
  ASP.NET forwarding service never downloads a non-clean result.

## Security decisions

- Both examples treat all submitted content as untrusted and perform no local file
  parsing. The ASP.NET example forwards the first multipart file section directly
  from the request stream rather than using buffered form model binding.
- Only exact `Clean` metadata permits `DownloadAsync`. `Infected`, `ScanFailed`,
  `Deleted`, timeout, cancellation, and unknown/protocol states stop processing.
- Examples never print or log the API key or file bytes. Expected failures return
  controlled messages/ProblemDetails rather than upstream exception detail.
- `HttpClientFactory` owns the ASP.NET client's HTTP lifetime, attaches credentials
  only through the SDK, and disables automatic redirects on its primary handler.
- The examples reference the local SDK project. No package is published or consumed
  from NuGet.

## Out of scope

NuGet publishing, Python SDK, Google Drive, CV parsing or AI, webhooks, quotas,
retention, new scanner behavior, UI frameworks, and API v2 remain later work.

## Verification target

Restore, zero-warning Release build, full tests, formatting, direct/transitive
NuGet audit, and final diff/status inspection must pass. A disposable full Compose
stack must verify the console clean flow, ASP.NET clean forwarding, invalid service
credential handling, and an actual non-clean/infected response without exposing
secrets.

## Verified implementation state

- Both project-reference examples build in the solution. The console streams a
  local file through upload, polling, and clean-only download; the ASP.NET example
  streams an inbound multipart section through an injected client and returns bytes
  only after exact `Clean`.
- Six focused example tests verify clear configuration failures, credential
  redaction, clean download, and that `Infected`, `ScanFailed`, and `Deleted` never
  trigger download. All 51 unit and 84 integration tests pass.
- A disposable all-container stack reached healthy API, PostgreSQL, and ClamAV
  states. The console completed a real clean scan/download and rejected an invalid
  key with a controlled 401 message. The ASP.NET example returned byte-identical
  clean content and rejected a runtime-assembled EICAR PDF stream with controlled
  422 ProblemDetails and no content or credential exposure.
- The pinned .NET 10 SDK restored all projects and produced a zero-warning Release
  build. Formatting and `git diff --check` pass, and the direct/transitive NuGet
  audit reports no known vulnerable packages from the configured sources.
- No package was published and no server API, authentication, persistence, or
  scanner behavior changed. Hosted CI for these uncommitted changes is unverified.

Recommended next milestone: `chore/client-sdk-release`.
