# Authoritative agent instructions

`AGENTS.md` is the authoritative instruction file for coding agents in this repository. A more specific nested
`AGENTS.md` may add rules for its subtree but must not contradict this file.

# Project context

CreatioClient is the .NET Standard 2.0 library published as `creatio.client`. It provides authenticated HTTP,
file-transfer, and WebSocket access to Creatio for external .NET applications.

Repository: <https://github.com/Advance-Technologies-Foundation/creatioclient>

NuGet: <https://www.nuget.org/packages/creatio.client>

Important paths:

- `creatioclient/` — the package source targeting `netstandard2.0`
- `creatioclient/CreatioClient.cs` — public HTTP, file, and WebSocket entry points
- `creatioclient/ICreatioClient.cs` — established synchronous compatibility interface
- `creatioclient/IAsyncCreatioClient.cs` — additive cancellation-aware response interface
- `creatioclient/CreatioAuthenticationHandler.cs` — cookie, OAuth/bearer, NTLM, redirect, and CSRF pipeline
- `creatioclient/WsListenerSignalR.cs` — modern SignalR WebSocket listener
- `creatioclient/WsListenerNetFramework.cs` — legacy raw WebSocket listener
- `creatioclient.Tests/` — NUnit tests targeting `net8.0` and `net10.0`
- `creatioclient.example/` — console usage examples
- `.github/workflows/ci.yml` — cross-platform build, tests, and scoped authentication coverage
- `.github/workflows/release.yml` — tag-driven NuGet and GitHub release

# Intended design

CreatioClient is a compatibility-sensitive .NET Standard HTTP and WebSocket library. Prefer the smallest
change that preserves established public and observable behavior. Keep authentication, credential
forwarding, redirect, retry, cancellation, response ownership, and disposal rules explicit.

Do not expand `ICreatioClient` casually: adding a member breaks third-party implementations. Prefer an
additive interface or concrete API when compatibility requires it. Keep the pooled per-client `HttpClient`
and `CreatioAuthenticationHandler`; do not introduce a second transport stack.

# Architecture and compatibility

Each `CreatioClient` owns one lazily initialized `HttpClient` with this pipeline:

`CreatioClient` -> `CreatioAuthenticationHandler` -> `HttpClientHandler`

Supported authentication modes have distinct contracts:

- Username/password uses Creatio login cookies and CSRF and may renew an expired session.
- OAuth client credentials obtains bearer tokens from the configured authorization endpoint. Preserve the
  constructor/factory compatibility and the documented renewal behavior of the current implementation.
- A caller-supplied raw bearer token is forwarded as supplied after normalizing a single `Bearer` prefix; the
  client cannot independently renew it.
- NTLM delegates scoped Windows credentials to `HttpClientHandler`.

Keep all authentication inside the existing handler pipeline. Authenticated requests and followed redirects
must remain on the configured Creatio origin, except for the existing same-host HTTP-to-HTTPS upgrade. Never
forward credentials to an unrelated origin.

Cookie-authenticated modifying requests echo the CSRF cookie under the name issued by Creatio. Preserve both
modern `CRT_CSRF` and legacy `BPMCSRF` behavior. Do not invent a CSRF header when the server issued no token.

`CreatioClient` is disposable. The caller owns every `HttpResponseMessage` returned by the async API. File and
network streams must have explicit ownership, and retries or authentication replay must recreate requests and
content rather than resending disposed instances.

The synchronous API is a compatibility facade over the asynchronous transport. Preserve its string, file,
boolean, exception, and protocol-error response behavior. `HttpWebResponse` compatibility is intentionally
narrow; do not expand it into a second transport implementation.

`_useUntrustedSsl = true` is a legacy opt-in/default compatibility behavior for self-signed on-premise systems.
Respect the public certificate-policy arguments, and do not use untrusted TLS against external systems in tests.

`SetRetryPolicy` treats `maxAttempts` as the total attempt count. Values below one become one. `Simple` uses a
fixed delay and `Progressive` increases it. Keep retries bounded and cancellation-aware.

WebSocket behavior differs by target Creatio runtime: SignalR is used for modern hosts and the raw listener for
legacy hosts. Both listeners reconnect and reuse fixed receive buffers. Changes must preserve cancellation,
message framing, reconnection, and bounded memory behavior.

The application URL may contain an IIS virtual-directory path such as `/Creatio1`. Any URL composition must
preserve that complete application root. Absolute request URLs remain a compatibility input unless a deliberate
breaking change is authorized.

# C# and tests

- Use NUnit and FluentAssertions.
- Add XML documentation for public types and members.
- Follow Microsoft C# naming: PascalCase public members/types, `_camelCase` private fields, and camelCase locals.
- Preserve the existing region organization when editing established source files.
- Use UTF-8 for text serialization and Newtonsoft.Json for existing JSON contracts unless a deliberate migration
  is in scope.
- Prefer `Guid.Empty` validation for `Guid` values.
- Test supported targets (`net8.0` and `net10.0`) when behavior changes.
- Use deterministic loopback handlers or servers for transport tests. Do not depend on a public Creatio
  environment unless the user explicitly requests live E2E validation.
- Preserve caller ownership of response-returning `HttpResponseMessage` values and recreate request
  messages and content for retries or authentication replay.
- Never log credentials, bearer tokens, authentication cookies, CSRF values, or authorization headers.

# Known maintenance hazards

- Blocking `.Result` and `.GetAwaiter().GetResult()` calls exist to preserve synchronous API behavior. Keep new
  network behavior asynchronous and expose it through the async API; do not spread sync-over-async internally.
- `ExecuteDeleteRequest` is concrete-only. Do not add it to `ICreatioClient` without accepting the source-breaking
  effect on third-party implementations.
- `UploadFile_original` is compatibility-only legacy multipart code. Do not extend it.
- Inspect `UploadStaticFileAsync` query-string construction carefully; historically its `folderName` separator
  has been malformed.
- WebSocket listeners use fixed receive buffers and raw threads. Do not assume arbitrary message size or Task-based
  lifetime semantics without redesigning and testing that contract.
- Username/password credentials remain in client fields for possible re-login. Never log or serialize the client.

# Delivery and release

Version comes from the release tag; do not hard-code a package version in the project file. Release tags use
`X.Y.Z` or `vX.Y.Z`, and the workflow publishes NuGet plus a GitHub release. Never expose the
`CREATIOCLIENT_NUGET_API_KEY` repository secret.

For a user-facing feature, update `README.md` and the example when useful. Before opening a PR, run the relevant
focused tests, both supported test targets, the scoped authentication coverage gate when that pipeline changed,
and any explicitly requested live E2E validation. Report skipped E2E rather than implying it ran.

# Agentic code review

Tool-neutral skill and reviewer instructions live under `.ai/`. Treat those files as the only behavioral source
of truth. `.codex/` and `.claude/` contain discovery and configuration adapters only; keep their frontmatter and
descriptions aligned, but do not duplicate full instructions there.

Use the repository's `.ai/skills/agentic-code-review` skill for branch, pull-request, pre-merge, comprehensive,
or multi-perspective review requests. Codex discovers it through `.codex/skills`; Claude discovers it through
`.claude/skills`. Both tools use the same canonical reviewer roles under `.ai/agents` through their respective
thin agent adapters.

Agentic review is required at these delivery gates:

1. Before opening a pull request, review the complete diff against its base branch.
2. After a substantive commit is pushed to an open pull request, review that commit's diff. Documentation,
   comments, generated files, and formatting-only commits may be skipped with the reason recorded.
3. Before declaring a pull request ready to merge, review the current complete diff again and verify that
   the reviewed head is still the pull request head.

Comprehensive review uses independent perspectives for:

- code quality and maintainability
- security and credential boundaries
- performance and resource lifetime
- tests and cross-runtime compatibility
- bugs and edge cases
- alignment with the stated intent
- KISS and unnecessary design machinery

Give every reviewer the same intent brief, base/head commits, changed-file list, relevant diff, and repository
instructions. Reviewers are read-only: they may run tests and create disposable probes, but must not edit the
candidate change. Validate findings against the source before reporting them.

Findings use `Blocker`, `High`, `Medium`, or `Low` severity and include a tight file/line location, trigger,
impact, evidence, and smallest viable fix. Blocker and High findings must be resolved before delivery;
Medium and Low findings are advisory unless they contradict the requested outcome or a compatibility or
security invariant.

Green CI and Sonar are evidence, not substitutes for review. Report which perspectives ran, which tests were
run, any skipped validation, and residual risk.

# KISS completion check

Before calling implementation complete, restate its intent and end-to-end flow, identify unnecessary moving
parts, and confirm that a maintainer can understand the result without reconstructing hidden state or duplicate
sources of truth.
