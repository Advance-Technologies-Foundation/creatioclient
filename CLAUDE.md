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
    ATFWebRequestExtensions.cs         # extension methods on HttpWebRequest
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

### HTTP Client Duality

The library has a deliberate mixed usage of two HTTP stacks:

- `HttpWebRequest` (via `WebRequest.CreateHttp`) — used for GET requests, legacy file upload
  (`UploadAlmFile`, `UploadAlmFileByChunk`), ping, and login. Extension methods live in
  `ATFWebRequestExtensions`.
- `HttpClient` (with a fresh `HttpClientHandler` per call) — used for POST/DELETE, modern
  file uploads (`UploadFileAsync`, `UploadAttachmentAsync`, `UploadStaticFileAsync`).

> IMPORTANT: `HttpClient` instances are NOT pooled (a known issue). Each call in
> `ExecutePostRequest` / `ExecuteDeleteRequest` creates and disposes a full handler/client.
> This is safe for correctness but can exhaust ephemeral ports under high call rates. Do NOT
> change this to a static/shared `HttpClient` without also handling cookie and OAuth header
> injection carefully.

### SSL

`_useUntrustedSsl = true` by default — all certificate validation callbacks return `true`.
This is intentional for on-premise Creatio instances with self-signed certs, but should be
set to `false` when connecting to trusted production endpoints.

### Retry Policy

`SetRetryPolicy(retryCount, delaySec, RetryPolicy)` configures instance-level retries for
`ExecutePostRequest`, `ExecuteGetRequest`, `ExecuteDeleteRequest`, and the chunk loop inside
`UploadAttachmentAsync`.

- `RetryPolicy.Simple` — fixed delay of `delaySec` seconds between attempts
- `RetryPolicy.Progressive` — delay multiplied by attempt number (1×, 2×, 3×, …)

Default: 1 retry, 1-second delay.

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
- Return type is `string` (raw JSON) for all HTTP methods — callers parse the JSON themselves.
  This is an intentional design choice; do not change existing signatures without a major version bump.
- Prefer `Guid.Empty` checks over null checks for `Guid` parameters.
- Use `WebRequest.CreateHttp(url)` (not `WebRequest.Create(url)`) — the latter can return a
  `FileWebRequest` on macOS/Linux and causes an `InvalidCastException`.
- `Encoding.UTF8` for all string serialisation.
- `JsonConvert.DeserializeObject<T>` (Newtonsoft.Json) for all JSON parsing.
- No `async/await` in the public API except `UploadFileAsync`, `UploadAttachmentAsync`,
  `UploadStaticFileAsync` — the rest use `.Result` or `.GetAwaiter().GetResult()` intentionally
  to keep the public API synchronous and cross-target-framework compatible.

---

## Known Issues and Areas to Be Careful About

### 1. Zero test coverage
There are no automated tests. Every change must be manually verified against a real Creatio
instance. When adding tests, target `netstandard2.0`-compatible test runner (NUnit 4.x is already
mentioned in the copilot instructions).

### 2. HttpClient not pooled (socket exhaustion risk)
`ExecutePostRequest`, `ExecuteDeleteRequest`, `UploadFileAsync`, and `UploadStaticFileAsync` each
create a new `HttpClientHandler` + `HttpClient` inside the call. Under high-throughput scenarios
this will exhaust TCP ports. Prefer `IHttpClientFactory` or a shared `HttpClient` in the future,
but be careful about cookie container and OAuth header sharing.

### 3. SSL disabled by default
`_useUntrustedSsl = true` is the default. Never set this in CI tests against external endpoints
without understanding the security implications.

### 4. Blocking async over sync
Several methods call `.Result` or `.GetAwaiter().GetResult()` on async operations. This is
intentional for the synchronous API surface, but can cause deadlocks inside ASP.NET Framework
`SynchronizationContext`. Callers should use the `*Async` overloads where available.

### 5. `ExecuteDeleteRequest` is not on `ICreatioClient`
The method exists on `CreatioClient` but was not added to the interface. Adding it is safe but
constitutes a minor breaking change for implementors of the interface.

### 6. `UploadStaticFile` URL bug
In `UploadStaticFileAsync`, the URL is built as:
```csharp
string url2 = url + "&fileName=" + fileName + $"folderName={folderName}";
```
The `&` before `folderName` is missing — it produces `...fileNamefoo.zipfolderName=bar`.
This is a known bug. The fix is to insert `&` before `folderName=`.

### 7. `UploadFile_original` is dead code
`UploadFile_original` is an older multipart upload implementation kept for reference. It is not
called anywhere and should not be used. Do not extend it.

### 8. WsListenerSignalR 8 MB buffer is fixed
The `_buffer` field is `byte[8_388_608]`. Messages larger than this will corrupt or truncate.
There is no dynamic buffer expansion.

### 9. Thread-based WebSocket listeners
Both WS listeners run on a raw `Thread` (not `Task`). This means thread-pool pressure is not
an issue, but shutdown behaviour is tied to the `CancellationToken` and the thread runs
indefinitely until cancelled. Always pass a real `CancellationToken` — never `CancellationToken.None`
in production code.

### 10. Login stores credentials in memory
`_userName` and `_userPassword` are stored as plain strings in `CreatioClient` fields for the
lifetime of the client instance. Avoid logging the client object.

---

## CI/CD

| Workflow | Trigger | What it does |
|----------|---------|--------------|
| `ci.yml` | push/PR to `main`/`master` | Build + optional test on ubuntu, macos, windows |
| `release.yml` | push of tag `X.Y.Z` or `vX.Y.Z`, or manual dispatch | Build, pack, publish to NuGet.org; create GitHub Release |

Version is injected at build time via `/p:Version=X.Y.Z` — the `.csproj` carries no hardcoded
version. The git tag is the single source of truth for the version.

Required secret: `CREATIOCLIENT_NUGET_API_KEY` (NuGet API key in repo settings).

---

## Adding New Features — Checklist

1. Add/update the method on `ICreatioClient` with XML doc comments.
2. Implement in `CreatioClient.cs` following existing `#region` and naming conventions.
3. Use `WebRequest.CreateHttp` (not `WebRequest.Create`) for any `HttpWebRequest` construction.
4. Respect `_useUntrustedSsl` in any new `HttpWebRequest` or `HttpClientHandler`.
5. Apply auth headers/cookies consistently (check `_oauthToken` first, fall back to cookie).
6. Include CSRF token (`BPMCSRF`) for any state-changing request.
7. Add input validation (`ArgumentNullException`, `ArgumentException`, `FileNotFoundException`)
   for public methods, mirroring the pattern in `ValidateUploadInfo`.
8. Wrap the request body in the `Retry<T>` pattern if the operation is idempotent.
9. Update `README.md` with usage examples if the feature is user-facing.
10. Manually test against a real Creatio instance before opening a PR.
