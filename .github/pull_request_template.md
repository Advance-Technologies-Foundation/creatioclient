## Description

<!-- What does this PR do? Why? Link to the issue(s) it addresses.
     Closes #___  /  Fixes #___  /  Related to #___ -->

## Type of Change

<!-- Check all that apply -->
- [ ] Bug fix (non-breaking change that fixes an issue)
- [ ] New feature (non-breaking additive change)
- [ ] Breaking change (alters existing public API — requires major or minor version bump)
- [ ] Refactor / internal improvement (no public API change)
- [ ] Documentation / comment update
- [ ] CI/CD / build tooling change

## Changes Made

<!-- Bullet-point summary of what changed and why.
     Be specific enough that a reviewer can understand without reading every line. -->

- 
- 

## Public API Surface

<!-- Did you add, remove, or change any public types, methods, or events?
     If yes, list them. If no, delete this section. -->

```csharp
// Before (or N/A)

// After
```

- [ ] `ICreatioClient` interface updated to match new / changed method(s)
- [ ] XML doc comments added / updated for all changed public members
- [ ] No public API changed

## Breaking Change Assessment

- [ ] This PR does NOT break existing callers
- [ ] This PR DOES break existing callers — migration path: <!-- describe here -->

## Test Plan

<!-- creatioclient currently has no automated tests. Describe how you verified this change. -->

- [ ] Tested manually against a real Creatio instance (`AppUrl`: <!-- cloud / on-prem / version -->)
- [ ] Tested auth method(s): <!-- Cookie / OAuth / NTLM -->
- [ ] Tested on platform(s): <!-- Windows / Linux / macOS -->
- [ ] Unit tests added in `creatioclient.Tests/` (if the test project exists)
- [ ] No testing possible (documentation-only change)

Describe what you tested and what the result was:

```
// paste relevant output / log here
```

## Checklist

### Code quality
- [ ] Follows Microsoft C# naming conventions (`PascalCase` types, `_camelCase` private fields)
- [ ] Maintains `#region` structure (Constants, Fields, Properties, Events, Constructors, Methods)
- [ ] Uses `WebRequest.CreateHttp()` — not `WebRequest.Create()` — for any new `HttpWebRequest`
- [ ] Respects `_useUntrustedSsl` in new `HttpWebRequest` or `HttpClientHandler` code
- [ ] Auth path checks `_oauthToken` first, falls back to cookie + `BPMCSRF` header
- [ ] Idempotent HTTP calls wrapped in `Retry<T>()` helper
- [ ] Input parameters validated with `ArgumentNullException` / `ArgumentException` as appropriate
- [ ] No credentials or secrets are logged (e.g., `_userName`, `_userPassword`, `_oauthToken`)

### API consistency
- [ ] New method(s) added to `ICreatioClient` if they are intended as public API
- [ ] Default parameter values consistent with existing overloads
  (`requestTimeout = 100_000`, `retryCount = 1`, `delaySec = 1`, `chunkSize = 1 * 1024 * 1024`)

### Documentation
- [ ] `README.md` updated if the change is user-facing
- [ ] `CLAUDE.md` updated if architecture or known-issues sections need revision

### CI
- [ ] All three platform builds pass (ubuntu / macos / windows) — `ci.yml`
- [ ] No new compiler warnings introduced
