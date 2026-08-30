# CLAUDE.md — creatioclient

This file helps AI coding assistants (Claude Code, Copilot, etc.) understand the project deeply.

---

## Project Purpose

**creatioclient** is a .NET Standard 2.0 library (`creatio.client` on NuGet) published by
[Advance Technologies Foundation](https://github.com/Advance-Technologies-Foundation). It is a
thin HTTP/WebSocket client for integrating external .NET applications with the
[Creatio](https://www.creatio.com/) CRM/BPM platform.

The library handles:
- Authentication (Cookie/session, OAuth 2.0 client-credentials, NTLM)
- REST calls to Creatio OData and configuration services
- Chunked file upload and file download (ALM packages, attachments, static files)
- WebSocket real-time subscriptions (two implementations: SignalR for modern .NET Core hosts,
  raw WebSocket for legacy .NET Framework hosts)

NuGet: https://www.nuget.org/packages/creatio.client  
Repo: https://github.com/Advance-Technologies-Foundation/creatioclient

---

## Repository Layout

```
creatioclient/                         # solution root
  creatioclient.sln
  README.md
  CLAUDE.md                            # this file
  .github/
    workflows/
      ci.yml                           # build + test on push/PR (ubuntu, macos, windows)
      release.yml                      # tag-driven NuGet publish
    copilot-instructions.md            # GitHub Copilot custom instructions
    prompts/
      release.prompt.md                # /release slash command automation script
    ISSUE_TEMPLATE/
      bug_report.md
      feature_request.md
    pull_request_template.md

  creatioclient/                       # main library project (netstandard2.0)
    creatioclient.csproj               # PackageId: creatio.client
    CreatioClient.cs                   # primary public class — all HTTP + WS entry-points
    ICreatioClient.cs                  # public interface (always code against this)
    IAsyncCreatioClient.cs             # additive cancellation-aware response API
    CreatioAuthenticationHandler.cs    # password/cookie, OAuth, and NTLM auth pipeline
    ATFWebRequestExtensions.cs         # legacy public compatibility surface only
    RetryPolicy.cs                     # enum: Simple | Progressive
    IWsListener.cs                     # internal WebSocket listener interface
    WsListenerSignalR.cs               # SignalR-over-WebSocket listener (isNetCore=true)
    WsListenerNetFramework.cs          # raw WebSocket listener (isNetCore=false)
    Dto/
      FileUploadInfo.cs                # upload metadata (schema, column, parentId, extras)
      FileUploadResponseDto.cs         # server response after chunk upload
      Header.cs                        # WsMessage header
      NegotiateResponse.cs             # SignalR negotiate response
      SignalRWrapper.cs                # SignalR hub message envelope
      TokenResponse.cs                 # OAuth 2.0 token response
      WsMessage.cs                     # WebSocket message body

  creatioclient.example/               # console app showing usage patterns
    Program.cs

  creatioclient.Tests/                 # NUnit tests (net8.0 + net10.0)
    LegacyHttpBehaviorCharacterizationTests.cs
    ModernHttpClientTransportTests.cs
    PublicApiCompatibilityTests.cs
    LegacyCreatioEndToEndTests.cs       # opt-in tests against a real Creatio instance
```

---

## Architecture Overview

### Authentication Flow

There are three mutually exclusive auth paths, all lazy-initialised on the first real request:

| Mode | Constructor | Mechanism |
|------|-------------|-----------|
| Cookie/session | `new CreatioClient(url, user, pass)` | POST to `/ServiceModel/AuthService.svc/Login`, stores `.ASPXAUTH` + `BPMCSRF` cookies |
| OAuth 2.0 | `CreatioClient.CreateOAuth20Client(...)` | client-credentials grant to identity server; stores bearer token in `_oauthToken` |
| NTLM | `new CreatioClient(url, ssl, ICredentials)` | GET to `/Login/NuiLogin.aspx?ntlmlogin` with `NetworkCredential` |

CSRF protection: every modifying request includes the `BPMCSRF` cookie value in the
`BPMCSRF` header (extracted from the cookie jar).

### HTTP transport

Each `CreatioClient` instance owns one lazily initialized, pooled `HttpClient`. Its pipeline is:

`CreatioClient` → `CreatioAuthenticationHandler` → `HttpClientHandler`

- Password authentication logs in once, shares the cookie container, and adds `BPMCSRF`.
- OAuth adds the bearer token in the delegating handler.
- NTLM/Windows credentials are scoped to the configured Creatio origin through `CredentialCache`
  and handled by the primary `HttpClientHandler`.
- Authenticated requests and followed redirects are restricted to the configured origin or a
  same-host HTTP-to-HTTPS upgrade so credentials are not forwarded to unrelated origins.

`CreatioClient` is disposable. Callers own and must dispose each `HttpResponseMessage` returned by
the response-returning async API. `ATFWebRequestExtensions` remains public only for compatibility;
the production `CreatioClient` transport does not call it.

Synchronous protocol failures use a narrowly scoped `HttpWebResponse` compatibility view because Clio
casts `WebException.Response` to that concrete type. It preserves the status, description, headers,
request URI, method, and bounded error body. Do not treat it as general-purpose `HttpWebResponse`
emulation; use the async response API when other metadata is needed.

### SSL

`_useUntrustedSsl = true` by default — all certificate validation callbacks return `true`.
This is intentional for on-premise Creatio instances with self-signed certs, but should be
set to `false` when connecting to trusted production endpoints.

### Retry Policy

`SetRetryPolicy(maxAttempts, delaySec, RetryPolicy)` configures instance-level retry behaviour for
`ExecutePostRequest`, `ExecuteGetRequest`, `ExecuteDeleteRequest`, and the chunk loop inside
`UploadAttachmentAsync`.

- The count is a **total attempt count**, not a number of retries: `maxAttempts = 1` makes a
  single attempt with no retry. Values below 1 are clamped to 1 (a `0` never silently skips the
  request).
- `RetryPolicy.Simple` — fixed delay of `delaySec` seconds between attempts
- `RetryPolicy.Progressive` — delay multiplied by attempt number (1×, 2×, 3×, …)

Default: 1 attempt (no retries), 1-second delay.

### WebSocket Listeners

`StartListening(CancellationToken)` spawns a background `Thread` (not a Task) and picks the
listener based on `isNetCore`:

- `WsListenerSignalR` (`isNetCore = true`) — negotiates with `/msg/negotiate`, sends the
  SignalR JSON handshake (`{"protocol":"json","version":1}`), parses `SignalRWrapper`
  envelopes.
- `WsListenerNetFramework` (`isNetCore = false`) — connects directly to
  `/0/Nui/ViewModule.aspx.ashx`, parses raw `WsMessage` JSON.

Both listeners auto-reconnect on any exception (1-second back-off, re-login, re-negotiate).

The 8 MB receive buffer (`_buffer = new byte[8192 * 1024]`) is reused per listener instance.
Large payloads that span multiple frames are accumulated via `_currentPosition`.

---

## Key Classes and Contracts

### `ICreatioClient` (always code to this interface)

| Method | Description |
|--------|-------------|
| `Login()` | Explicit login (normally called lazily) |
| `CallConfigurationService(service, method, data)` | POST to `/{workspace}/rest/{service}/{method}` |
| `ExecuteGetRequest(url)` | GET with cookie/OAuth auth |
| `ExecutePostRequest(url, data)` | POST with cookie/OAuth auth |
| `ExecuteDeleteRequest(url, data)` | DELETE with cookie/OAuth auth (not on interface yet — impl only) |
| `UploadFile(url, filePath)` | Chunked upload (1 MB chunks) |
| `UploadAttachmentAsync(uploadInfo)` | Typed attachment upload via `FileApiService/UploadFile` |
| `DownloadFile(url, filePath, data)` | Download to local file |
| `DownloadAttachment(schemaName, recordId, filePath)` | Download via `FileService/Download` |
| `StartListening(ct)` | Subscribe to WebSocket messages |
| `SetRetryPolicy(count, delay, policy)` | Configure retry behaviour |
| Events: `MessageReceived`, `ConnectionStateChanged` | Real-time WS events |

### `IAsyncCreatioClient`

`IAsyncCreatioClient` extends `ICreatioClient` without adding members to the established interface,
which preserves existing third-party implementations. Its HTTP methods accept `CancellationToken`
and return caller-owned `Task<HttpResponseMessage>` values so status, headers, and content remain
observable. File-download methods finish streaming to disk before returning the response.

### URL Construction

- Configuration services: `{AppUrl}/{WorkspaceId}/rest/{serviceName}/{methodName}`
  where `WorkspaceId` is always `"0"`.
- File API: `{AppUrl}/0/rest/FileApiService/UploadFile`
- File download: `{AppUrl}/0/rest/FileService/Download/{schemaName}/{recordId}`
- Login: `{AppUrl}/ServiceModel/AuthService.svc/Login`
- Ping: `{AppUrl}/0/ping`

The `AppUrl` is normalised (trailing slash stripped) at construction time.

### `FileUploadInfo` DTO

Required fields for `UploadAttachmentAsync`:
- `EntitySchemaName` — the file attachment schema (e.g., `"ContactFile"`)
- `ColumnName` — column holding the file data (typically `"Data"`)
- `FilePath` — absolute path on the local filesystem
- `ParentColumnName` — FK column name (e.g., `"Contact"`)
- `ParentColumnValue` — the parent record `Guid`
- `AdditionalParams` — optional extra query-string key/value pairs

---

## Coding Conventions

- **Microsoft C# naming conventions** throughout: `PascalCase` for types/methods/properties,
  `_camelCase` for private fields, `camelCase` for locals.
- **XML doc comments** on all public API members (interface + constructors at minimum).
- **`#region` blocks** are used extensively (existing style — maintain them when editing).
- **Regions used**: `Constants: Private`, `Fields: Private`, `Properties: Public/Private/Internal`,
  `Events: Public`, `Methods: Private/Protected/Public`, `Constructors: Public/Private`.
- All public constructors are in the `Constructors: Public` region.
- Existing synchronous HTTP methods return raw strings or retain their established file/boolean
  contracts. Do not change those signatures or observable behavior. Add response-returning async
  operations to `IAsyncCreatioClient` instead of expanding `ICreatioClient`.
- Prefer `Guid.Empty` checks over null checks for `Guid` parameters.
- `Encoding.UTF8` for all string serialisation.
- `JsonConvert.DeserializeObject<T>` (Newtonsoft.Json) for all JSON parsing.
- Keep synchronous compatibility wrappers, but implement new network behavior through the
  cancellation-aware async response API.

---

## Known Issues and Areas to Be Careful About

### 1. SSL disabled by default
`_useUntrustedSsl = true` is the default. Never set this in CI tests against external endpoints
without understanding the security implications.

### 2. Blocking async over sync
Several methods call `.Result` or `.GetAwaiter().GetResult()` on async operations. This is
intentional for the synchronous API surface, but can cause deadlocks inside ASP.NET Framework
`SynchronizationContext`. Callers should use the `*Async` overloads where available.

### 3. `ExecuteDeleteRequest` is not on `ICreatioClient`
The synchronous method exists on `CreatioClient` but was not added to the established interface.
Its response-returning counterpart is available through `IAsyncCreatioClient`; do not expand
`ICreatioClient`, because doing so breaks third-party implementors.

### 4. `UploadStaticFile` URL bug
In `UploadStaticFileAsync`, the URL is built as:
```csharp
string url2 = url + "&fileName=" + fileName + $"folderName={folderName}";
```
The `&` before `folderName` is missing — it produces `...fileNamefoo.zipfolderName=bar`.
This is a known bug. The fix is to insert `&` before `folderName=`.

### 5. `UploadFile_original` is compatibility-only code
`UploadFile_original` is an older multipart upload implementation kept for reference. It is not
called anywhere and should not be used. Do not extend it.

### 6. WsListenerSignalR 8 MB buffer is fixed
The `_buffer` field is `byte[8_388_608]`. Messages larger than this will corrupt or truncate.
There is no dynamic buffer expansion.

### 7. Thread-based WebSocket listeners
Both WS listeners run on a raw `Thread` (not `Task`). This means thread-pool pressure is not
an issue, but shutdown behaviour is tied to the `CancellationToken` and the thread runs
indefinitely until cancelled. Always pass a real `CancellationToken` — never `CancellationToken.None`
in production code.

### 8. Login stores credentials in memory
`_userName` and `_userPassword` are stored as plain strings in `CreatioClient` fields for the
lifetime of the client instance. Avoid logging the client object.

---

## CI/CD

| Workflow | Trigger | What it does |
|----------|---------|--------------|
| `ci.yml` | push/PR to `main` | Build and test on ubuntu, macos, windows; enforce scoped coverage |
| `release.yml` | push of tag `X.Y.Z` or `vX.Y.Z`, or manual dispatch | Build, pack, publish to NuGet.org; create GitHub Release |

Version is injected at build time via `/p:Version=X.Y.Z` — the `.csproj` carries no hardcoded
version. The git tag is the single source of truth for the version.

Required secret: `CREATIOCLIENT_NUGET_API_KEY` (NuGet API key in repo settings).

---

## Adding New Features — Checklist

1. Preserve `ICreatioClient`; add new response-returning HTTP operations to `IAsyncCreatioClient`.
2. Implement in `CreatioClient.cs` following existing `#region` and naming conventions.
3. Use the shared `HttpClient` and existing authentication pipeline; do not add `WebRequest` calls.
4. Respect `_useUntrustedSsl` in `HttpClientHandler` changes.
5. Keep bearer, cookie/BPMCSRF, and NTLM handling inside their existing pipeline layers.
6. Reject cross-origin authenticated requests and redirects.
7. Add input validation (`ArgumentNullException`, `ArgumentException`, `FileNotFoundException`)
   for public methods, mirroring the pattern in `ValidateUploadInfo`.
8. Recreate request messages and content for every configured retry attempt.
9. Update `README.md` with usage examples if the feature is user-facing.
10. Run NUnit tests on net8.0 and net10.0 plus the live Creatio E2E suite before opening a PR.
