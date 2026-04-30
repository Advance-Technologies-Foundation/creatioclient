# GitHub Copilot Custom Instructions for creatioclient

## Project Identity

**creatioclient** is a **.NET Standard 2.0** library (NuGet ID: `creatio.client`, assembly name:
`Creatio.Client`) published by Advance Technologies Foundation. It is a thin HTTP/WebSocket client
for integrating .NET applications with the [Creatio](https://www.creatio.com/) CRM/BPM platform.

- NuGet: https://www.nuget.org/packages/creatio.client
- Repo: https://github.com/Advance-Technologies-Foundation/creatioclient
- Namespace root: `Creatio.Client`
- Target: `netstandard2.0` (must stay compatible — no `net8`-only APIs in library code)

---

## Architecture in One Screen

```
ICreatioClient (interface)
  └── CreatioClient (implementation)
        ├── Auth: Cookie (.ASPXAUTH + BPMCSRF) | OAuth bearer | NTLM
        ├── HTTP:  HttpWebRequest (GET, ping, legacy upload)
        │          HttpClient     (POST, DELETE, chunked upload)
        ├── Retry: Retry<T>() with RetryPolicy.Simple | Progressive
        └── WS:   WsListenerSignalR   (isNetCore=true)  ← SignalR protocol
                  WsListenerNetFramework (isNetCore=false) ← raw WS

Dto/
  FileUploadInfo         ← attachment upload metadata
  FileUploadResponseDto  ← upload chunk response
  Header / WsMessage     ← real-time message types
  NegotiateResponse      ← SignalR negotiate
  SignalRWrapper         ← SignalR hub envelope
  TokenResponse          ← OAuth token
```

---

## Coding Conventions — Always Follow These

### Naming
- Types, methods, properties: `PascalCase`
- Private fields: `_camelCase` (underscore prefix)
- Locals and parameters: `camelCase`
- Constants (private): `PascalCase` — e.g., `WorkspaceId`, `authCookieName`

### Region Structure (maintain in all files)
```
#region Constants: Private
#region Fields: Private
#region Properties: Private / Internal / Public
#region Events: Public
#region Constructors: Private / Public
#region Methods: Private / Protected / Public
```

### XML Documentation
All `public` and `interface` members need `<summary>`, `<param>`, and `<returns>` XML doc
comments. Follow the existing style in `ICreatioClient.cs`.

### Auth — always check OAuth first
```csharp
if (!string.IsNullOrEmpty(_oauthToken)) {
    // Bearer header path
} else {
    // Cookie + BPMCSRF path
}
```

### CSRF protection
Every state-changing request must call `AddCsrfToken(request)` or add the `BPMCSRF` header
from the cookie container. Never skip this.

### HttpWebRequest construction
Always use `WebRequest.CreateHttp(url)`, never `WebRequest.Create(url)`.
`WebRequest.Create` returns `FileWebRequest` for some localhost URLs on macOS/Linux, causing
`InvalidCastException`.

### SSL validation
Respect `_useUntrustedSsl` in every new `HttpWebRequest` or `HttpClientHandler`:
```csharp
if (_useUntrustedSsl) {
    request.ServerCertificateValidationCallback = (msg, cert, chain, errors) => true;
}
```

### Return types
All HTTP methods return `string` (raw JSON). Do NOT parse or wrap the response into typed
objects in the library — callers own that. This is an intentional design boundary.

### Retry
Wrap idempotent operations in `Retry<T>()`:
```csharp
return Retry<string>(() => { /* operation */ }, retryCount, delaySec, _retryPolicy);
```

### Input validation
For public methods that accept file/entity parameters, validate eagerly with null checks
and throw `ArgumentNullException`, `ArgumentException`, or `FileNotFoundException`.
See `ValidateUploadInfo()` for the pattern.

### No async in the synchronous API surface
Only `UploadFileAsync`, `UploadAttachmentAsync`, and `UploadStaticFileAsync` are genuinely
async. All other public methods are synchronous — they may call `.Result` or
`.GetAwaiter().GetResult()` internally. This is intentional for cross-framework compatibility.

---

## URL Patterns

| Purpose | Pattern |
|---------|---------|
| Configuration service | `{AppUrl}/0/rest/{serviceName}/{methodName}` |
| Login | `{AppUrl}/ServiceModel/AuthService.svc/Login` |
| Ping | `{AppUrl}/0/ping` |
| NTLM login | `{AppUrl}/Login/NuiLogin.aspx?ntlmlogin` |
| File upload (FileApiService) | `{AppUrl}/0/rest/FileApiService/UploadFile` |
| File download | `{AppUrl}/0/rest/FileService/Download/{schemaName}/{recordId}` |
| SignalR negotiate | `{AppUrl}/msg/negotiate?negotiateVersion=1` |
| SignalR WebSocket | `wss://{host}/msg?id={connectionToken}` |
| Legacy WS | `wss://{host}/0/Nui/ViewModule.aspx.ashx` |

`AppUrl` always has trailing slash stripped (`NormalizeUrl`).

---

## Known Bugs and Pitfalls — Don't Repeat These

| Location | Issue |
|----------|-------|
| `UploadStaticFileAsync` | URL concatenation bug: `&` is missing before `folderName=` |
| `HttpClient` per-call | New handler created on every call — fine for correctness, risky at high concurrency |
| WS buffer | Fixed 8 MB buffer; messages larger than this will truncate silently |
| `ExecuteDeleteRequest` | Exists on `CreatioClient` but not declared on `ICreatioClient` |
| `UploadFile_original` | Dead code — do not use or extend it |
| `_useUntrustedSsl` | Defaults to `true` — do not forget to opt out for production |

---

## Testing Conventions

There are currently **no tests** in the repo. When adding tests:
- Place them in a new project `creatioclient.Tests/` targeting `net8.0`
- Use **NUnit 4.x** with **FluentAssertions**
- Name pattern: `MethodName_Scenario_ExpectedResult`
- Use AAA layout with `// Arrange`, `// Act`, `// Assert` comments
- Abstract HTTP calls behind `ICreatioClient` for mockability

```csharp
[Test]
[Description("Verifies that NormalizeUrl strips a trailing slash")]
public void NormalizeUrl_WithTrailingSlash_ReturnsUrlWithoutSlash()
{
    // Arrange
    string input = "https://example.creatio.com/";

    // Act — call via reflection or a test subclass if needed
    string result = /* ... */;

    // Assert
    result.Should().Be("https://example.creatio.com");
}
```

---

## Release Process (for AI-assisted `/release`)

1. Check `git describe --tags --abbrev=0` for current tag.
2. Increment patch: `1.0.34` → `1.0.35`.
3. Create and push tag: `git tag 1.0.35 && git push origin 1.0.35`.
4. GitHub Actions `release.yml` triggers: build → pack → publish to NuGet with
   `/p:Version=1.0.35`.
5. The `.csproj` has no hardcoded version — the tag is the single source of truth.

Required GitHub secret: `CREATIOCLIENT_NUGET_API_KEY`.

---

## Available Commands

### `/release` — Release Management

Automates version bump and NuGet publish. See `.github/prompts/release.prompt.md` for the
full step-by-step script.

**Usage examples:**
- `/release` — interactive wizard
- `/release publish 1.0.35` — create a specific version

**Automated steps:**
1. Verify / install `gh` CLI
2. Fetch latest tag
3. Calculate next version
4. Create + push git tag
5. Create GitHub Release (triggers NuGet publish via `release.yml`)

---

## Project Files Quick Reference

| File | Role |
|------|------|
| `creatioclient/CreatioClient.cs` | All public API implementation |
| `creatioclient/ICreatioClient.cs` | Public interface — primary contract |
| `creatioclient/ATFWebRequestExtensions.cs` | `HttpWebRequest` helpers |
| `creatioclient/RetryPolicy.cs` | Retry enum |
| `creatioclient/IWsListener.cs` | WS listener contract |
| `creatioclient/WsListenerSignalR.cs` | Modern WS listener |
| `creatioclient/WsListenerNetFramework.cs` | Legacy WS listener |
| `creatioclient/Dto/*.cs` | Transfer objects |
| `creatioclient.example/Program.cs` | Usage examples |
| `.github/workflows/ci.yml` | Cross-platform build CI |
| `.github/workflows/release.yml` | Tag-driven NuGet release |

---

## Dependencies

| Package | Version | Used for |
|---------|---------|---------|
| `Newtonsoft.Json` | 13.0.3 | All JSON serialisation |

No other runtime dependencies. Test projects may add NUnit, FluentAssertions, etc.

---

## Error Handling Quick Reference

| Symptom | Likely cause |
|---------|-------------|
| `InvalidCastException` on WebRequest | Used `WebRequest.Create` instead of `WebRequest.CreateHttp` |
| `UnauthorizedAccessException "Unauthorized ..."` | Bad credentials or wrong `AppUrl` |
| `BPMCSRF` rejection (403) | CSRF token not added to request headers |
| Login succeeds but requests fail | Cookie not propagating — check `AuthCookie` is not null |
| WebSocket disconnects constantly | `CancellationToken.None` passed; check host reachability |
| Upload `dto.Success = false` | Wrong `EntitySchemaName`, `ColumnName`, or `ParentColumnValue` |

---

## Communication Style

When generating code for this project:
- Match existing `#region` structure
- Use `var` sparingly — prefer explicit types for fields and method signatures
- Do not add `using` directives not already present unless strictly necessary
- Prefer `string.IsNullOrEmpty` over null-checks on string parameters
- Do not introduce LINQ where a simple `foreach` is clearer (existing code mixes both — match context)
