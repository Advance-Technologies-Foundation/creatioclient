# Architecture Review: `creatio.client`

> **Date:** 2026-05-29  
> **Scope:** Architectural analysis, modernisation roadmap, and feature proposals based on review of `creatioclient`, `clio`, and `ATF.Repository` source code along with Creatio platform API documentation.

---

## Table of Contents

1. [Ecosystem Context](#ecosystem-context)
2. [Current Architecture Problems](#current-architecture-problems)
3. [Proposed Features](#proposed-features)
4. [Prioritised Roadmap](#prioritised-roadmap)

---

## Ecosystem Context

`creatio.client` sits at the foundation of the ATF .NET toolchain:

```
creatio.client          — HTTP transport + auth  (this library)
    ↑ used by
ATF.Repository          — DataService ORM (LINQ → DataService JSON)
    ↑ used by
clio                    — CLI: package deployment, environment management, log streaming
```

Key findings from analysing all three repositories and the Creatio Academy documentation:

| Repository | What it covers | What it delegates to creatio.client |
|---|---|---|
| **ATF.Repository** | LINQ → DataService translation, change tracking, lazy loading | `ExecutePostRequest` only — every DML call |
| **clio** | Package install/export, workspace compilation, real-time log streaming | All HTTP calls + WebSocket |

**Critical gap:** ATF.Repository covers DataService (Creatio proprietary protocol) but has no OData v4 support at all. `creatio.client` could fill that gap as a first-class OData client.

**Blocking issue:** Both ATF.Repository and clio are entirely synchronous because `creatio.client` exposes no usable async API. Upgrading `creatio.client`'s async story unblocks the entire ecosystem.

---

## Current Architecture Problems

### 🔴 Critical

#### 1. Socket exhaustion — `HttpClient` created per call

`ExecutePostRequest`, `ExecuteDeleteRequest`, `ExecutePatchRequest`, `UploadFileAsync`, and `UploadStaticFileAsync` each create `new HttpClient(new HttpClientHandler())`. Under any meaningful call rate this exhausts ephemeral TCP ports and causes `SocketException`.

**Fix:** A singleton `HttpClient` (or `IHttpClientFactory`) with auth injected via `DelegatingHandler`.

```csharp
// Target design — one client, auth through handler pipeline:
private readonly HttpClient _httpClient;

public CreatioClient(string appUrl, ..., HttpClient? httpClient = null) {
    _httpClient = httpClient ?? new HttpClient(BuildHandlerPipeline());
}

private HttpMessageHandler BuildHandlerPipeline() =>
    new CreatioAuthHandler(this) {
        InnerHandler = new SocketsHttpHandler {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }
    };
```

#### 2. Sync-over-async — potential deadlock

Several call sites block on `Task` results inside a synchronous context:

| Location | Issue |
|---|---|
| `CreateOAuth20Client` → `.Result` | Deadlock in ASP.NET Classic `SynchronizationContext` |
| `Login()` → `NtlmLogin().GetAwaiter().GetResult()` | Same |
| `UploadFile` → `UploadFileAsync(...).GetAwaiter().GetResult()` | Same |
| `ExecutePostRequest` → `client.PostAsync(...).Result` | Same |

**Fix:** Full async overloads on all public methods. Synchronous variants wrap via `Task.Run(...).GetAwaiter().GetResult()` — predictable, doesn't deadlock.

#### 3. JSON injection in `Login()`

```csharp
// Current — string concatenation:
string authData = @"{""UserName"":""" + _userName + @""", ""UserPassword"":""" + _userPassword + @"""}";
```

A password containing `"` breaks the JSON payload. Fix: use `JsonConvert.SerializeObject`.

---

### 🟠 Serious

#### 4. Two HTTP stacks with no clear reason

`HttpWebRequest` (legacy) and `HttpClient` (modern) run in parallel. `ExecuteGetRequest` uses `HttpWebRequest`; `ExecutePostRequest` uses `HttpClient`. This doubles the number of auth code paths and makes `ATFWebRequestExtensions` a dead-end abstraction.

**Target:** Unify on `HttpClient`. Keep `HttpWebRequest` only if a concrete platform compatibility reason is discovered and documented.

#### 5. Auth logic duplicated in every method

Every method manually injects `Authorization: Bearer` or `BPMCSRF`. The correct pattern is a `DelegatingHandler`:

```csharp
internal sealed class CreatioAuthHandler : DelegatingHandler {
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct) {
        if (!string.IsNullOrEmpty(_token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        else {
            var csrf = _cookieContainer?.GetCookies(_baseUri)["BPMCSRF"];
            if (csrf != null)
                request.Headers.TryAddWithoutValidation("BPMCSRF", csrf.Value);
        }
        // Cookies flow automatically through HttpClientHandler.CookieContainer
        return await base.SendAsync(request, ct);
    }
}
```

#### 6. `Thread` instead of `Task` for WebSocket listeners

```csharp
Thread thread = new Thread(() => { ws.StartListening(); });
thread.Start();
```

No way to observe the thread's lifetime or surface exceptions. Should return a `Task` so callers can `await` completion or handle faults.

#### 7. `Thread.Sleep` in `Retry<T>`

Blocks a thread-pool thread for the entire backoff period. Should use `Task.Delay` in an async variant:

```csharp
private static async Task<T> RetryAsync<T>(Func<Task<T>> func, int maxRetries,
    int delaySeconds, RetryPolicy policy, CancellationToken ct = default) {
    for (int attempt = 0; attempt < maxRetries; attempt++) {
        try { return await func(); }
        catch when (attempt < maxRetries - 1) {
            int multiplier = policy == RetryPolicy.Progressive ? attempt + 1 : 1;
            await Task.Delay(delaySeconds * 1000 * multiplier, ct);
        }
    }
    return await func();
}
```

---

### 🟡 Code Quality

#### 8. No async methods on `ICreatioClient`

Pattern for all public methods should follow:

```csharp
Task<string> CallConfigurationServiceAsync(string serviceName, string serviceMethod,
    string requestData, CancellationToken ct = default);
Task<string> ExecuteGetRequestAsync(string url, CancellationToken ct = default);
Task<string> ExecutePostRequestAsync(string url, string requestData,
    CancellationToken ct = default);
Task DownloadFileAsync(string url, string filePath, string requestData,
    CancellationToken ct = default);
Task<bool> DownloadAttachmentAsync(string schemaName, Guid recordId,
    string filePath, CancellationToken ct = default);
```

#### 9. `Console.WriteLine` in library code

Progress reporting via `Console.WriteLine` cannot be suppressed or redirected by callers. Replace with `IProgress<UploadProgress>` (optional parameter) and/or an event following the existing `MessageReceived` pattern.

#### 10. `UploadStaticFileAsync` URL bug

```csharp
// Current (bug — missing & before folderName):
string url2 = url + "&fileName=" + fileName + $"folderName={folderName}";

// Fix:
string url2 = url + "&fileName=" + fileName + $"&folderName={folderName}";
```

#### 11. Ambiguous constructor signatures

```csharp
public CreatioClient(string appUrl, string bearerToken, bool isNetCore = false)
public CreatioClient(string appUrl, string userName, string userPassword, bool isNetCore = false)
```

Both accept `(string, string)` as first two parameters — callers cannot tell which is which by signature alone. Prefer explicit static factory methods:

```csharp
public static CreatioClient WithBearerToken(string appUrl, string token, bool isNetCore = false)
public static CreatioClient WithCredentials(string appUrl, string user, string pass, bool isNetCore = false)
```

---

## Proposed Features

### Feature 1: `CreatioUrlBuilder` — single registry of all Creatio endpoints

Both `clio` and `ATF.Repository` hardcode URL strings independently. Centralising in `creatio.client` ensures consistency and hides versioning quirks (workspace prefix `/0/`, `.NET Core` vs `.NET Framework` routing):

```csharp
var urls = new CreatioUrlBuilder("https://myapp.creatio.com");

// DataService (what ATF.Repository uses)
urls.DataService.Select      // → /0/dataservice/json/SyncReply/SelectQuery
urls.DataService.Insert      // → /0/dataservice/json/SyncReply/InsertQuery
urls.DataService.Batch       // → /0/dataservice/json/SyncReply/BatchQuery

// OData with fluent filter builder
string url = urls.OData("Contact")
    .Filter(f => f.Eq("Type.Code", "Customer").And().Contains("Name", "John"))
    .Select("Id", "Name", "Email")
    .Expand("Account")
    .OrderByDescending("ModifiedOn")
    .Top(100).Skip(0)
    .Build();

// Configuration services (what clio uses)
urls.ServiceModel("ProcessEngineService.svc", "RunProcess")
urls.Rest("FileApiService", "UploadFile")
urls.Rest("FeatureService", "GetFeatureState")

// Meta
urls.Login               // /ServiceModel/AuthService.svc/Login
urls.OAuthDiscovery      // /0/.well-known/openid-configuration
urls.OData.Metadata      // /0/odata/$metadata
urls.OData.Batch         // /0/odata/$batch
```

The builder internalises Creatio-specific quirks:
- Date literals must be bare ISO 8601 with `Z` — no `datetime''` prefix
- No `IN` operator → auto-expands to `(Id eq '...' or Id eq '...')`
- `$expand` only on lookup properties, not detail collections
- `ForceUseSession: true` added to every request automatically

---

### Feature 2: OData v4 Client — gap not covered by ATF.Repository

ATF.Repository uses DataService exclusively. OData v4 is needed for external integrations, iPaaS connectors, and any partner expecting a standards-compliant interface.

```csharp
// Typed read with auto-pagination via IAsyncEnumerable
await foreach (var record in client.OData
    .Query("Contact")
    .Where(f => f.Eq("Type.Code", "Customer")
                 .And().GreaterThan("ModifiedOn", DateTimeOffset.UtcNow.AddDays(-7)))
    .Select("Id", "Name", "Email")
    .Expand("Account")
    .AsAsyncEnumerable(pageSize: 1000, ct)) {
    // auto-loops through $skip/$top, yields each page as items arrive
}

// Single page with total count
ODataPage<JsonElement> page = await client.OData
    .Query("Contact")
    .Top(50).WithCount()
    .GetAsync(ct);
// page.Value : List<JsonElement>, page.TotalCount : int?

// CRUD
string newId = await client.OData.CreateAsync("Contact", payload, ct);
await client.OData.UpdateAsync("Contact", recordId, patch, ct);   // PATCH → 204
await client.OData.DeleteAsync("Contact", recordId, ct);           // DELETE → 204

// $batch — up to 100 operations per round-trip
var batch = client.OData.NewBatch();
batch.Patch("Account", id1, new { Phone = "111" });
batch.Patch("Account", id2, new { Phone = "222" });
batch.Delete("Contact", id3);
BatchResult[] results = await batch.ExecuteAsync(ct);

// Schema discovery
ODataMetadata meta = await client.OData.GetMetadataAsync(ct);
// → meta.Entities["Contact"].Properties — all fields with types
```

---

### Feature 3: DataService Client — typed layer complementing ATF.Repository

For callers who need the power of DataService (aggregation, IN-filter, complex filter groups) without pulling in the full ATF.Repository ORM:

```csharp
// SelectQuery builder
var query = new SelectQueryBuilder("Contact")
    .Columns("Id", "Name", "Email", "Account.Name")
    .FilterGroup(FilterLogic.And, fg => fg
        .Compare("Type.Code", FilterCompareType.Equal, "Customer")
        .Compare("Name", FilterCompareType.StartsWith, "H"))
    .OrderBy("Name", OrderDirection.Ascending)
    .Pageable(rowCount: 20, rowOffset: 0);

DataServiceResult result = await client.DataService.SelectAsync(query, ct);

// Batch — all DML in one round-trip (same pattern ATF.Repository uses internally)
var batch = client.DataService.NewBatch();
batch.Insert("Contact", new Dictionary<string, object> { ["Name"] = "John" });
batch.Update("Contact", id, new Dictionary<string, object> { ["Phone"] = "111" });
batch.Delete("Contact", id2);
BatchQueryResult batchResult = await client.DataService.ExecuteBatchAsync(batch, ct);

// System settings and feature flags (ATF.Repository patterns, exposed for non-ORM callers)
bool useNewShell = await client.DataService.GetSysSettingValueAsync<bool>("UseNewShell", ct);
bool featureOn   = await client.DataService.GetFeatureStateAsync("MyFeatureCode", ct);
```

---

### Feature 4: Session & Token Lifecycle Management

The most common operational failure for long-running apps: session expiry (Creatio default: 60–720 min) or OAuth token expiry (default: 3600 s). Neither ATF.Repository nor clio handles this automatically.

Implemented transparently as `DelegatingHandler` entries in the `HttpClient` pipeline:

```csharp
// Cookie/session auth — auto re-login on 401
// OAuth — token refresh N minutes before expiry
// NTLM — auto re-negotiate on 401

var client = new CreatioClient(appUrl, user, password,
    options => options.WithAutoRelogin()
                      .WithSessionExpiry(TimeSpan.FromMinutes(60)));

var client = CreatioClient.WithOAuth(appUrl, authUrl, clientId, clientSecret,
    options => options.WithTokenRefreshBuffer(TimeSpan.FromMinutes(2)));
```

---

### Feature 5: Process Engine Client

Both ATF.Repository (`IAppProcessContext`) and clio call `ProcessEngineService.svc`. Should be a first-class citizen of `creatio.client`:

```csharp
// Fire-and-forget
await client.Processes.RunAsync("LeadQualificationProcess",
    new { LeadId = leadId }, ct);

// Wait for output parameter
string resultJson = await client.Processes.RunWithResultAsync(
    schemaName: "GetContactDataProcess",
    resultParameter: "ResultJson",
    parameters: new { ContactId = contactId },
    ct);
```

---

### Feature 6: Testability — `HttpClient` constructor injection

Currently impossible to unit-test code using `creatio.client` without a real Creatio instance. ATF.Repository works around this with `MemoryDataProviderMock` (heavyweight). The correct fix is at transport level:

```csharp
// Production
var client = new CreatioClient(appUrl, token);

// Tests — inject a mock HttpMessageHandler
var mockHandler = new MockHttpMessageHandler();
mockHandler.When("/0/odata/Contact")
    .RespondWithJson(new { value = new[] { new { Id = Guid.NewGuid(), Name = "Test" } } });

var client = new CreatioClient(appUrl, token,
    httpClient: new HttpClient(mockHandler));

// DI registration (ASP.NET Core)
services.AddCreatioClient(options => {
    options.AppUrl = config["Creatio:Url"];
    options.BearerToken = config["Creatio:Token"];
});
// In tests: replace via IHttpClientFactory test host pattern
```

---

## Prioritised Roadmap

### Phase 1 — Foundation (no breaking changes, minor version bump)

| # | Change | Who benefits | Effort |
|---|---|---|---|
| 1 | Singleton `HttpClient` + `DelegatingHandler` for auth/CSRF | All callers | Medium |
| 2 | Full async API on `ICreatioClientAsync` extending `ICreatioClient` | ATF.Repository, clio | Medium |
| 3 | `CreatioUrlBuilder` | clio, ATF.Repository, all | Low |
| 4 | Session/token auto-refresh via `DelegatingHandler` | Long-running apps | Medium |
| 5 | Constructor `HttpClient` injection for testability | All | Low |
| 6 | Fix `Login()` JSON injection | Security | Low |
| 7 | Fix `UploadStaticFileAsync` URL bug | Any user of static file upload | Low |
| 8 | `IProgress<T>` replacing `Console.WriteLine` | Library consumers | Low |

### Phase 2 — OData layer (new namespace, additive)

| # | Change | Who benefits | Effort |
|---|---|---|---|
| 9 | `ODataFilterBuilder` with Creatio quirk handling | External integrations | Medium |
| 10 | Typed `ODataClient` — CRUD + `IAsyncEnumerable` pagination | External integrations | High |
| 11 | `ODataBatchBuilder` — $batch | Bulk integrations | Medium |
| 12 | `OData.GetMetadataAsync()` — schema discovery | Tooling, code-gen | Medium |

### Phase 3 — DataService layer (new namespace, additive)

| # | Change | Who benefits | Effort |
|---|---|---|---|
| 13 | `SelectQueryBuilder`, `BatchQueryBuilder` | Non-ORM DataService callers | High |
| 14 | `ProcessEngineClient` — RunAsync, RunWithResultAsync | clio, ATF.Repository | Low |
| 15 | `GetSysSettingValueAsync`, `GetFeatureStateAsync` | ATF.Repository pattern users | Low |

### Phase 4 — Breaking changes (major version bump)

| # | Change | Reason |
|---|---|---|
| 16 | Remove `UploadFile_original` (dead code) | Cleanup |
| 17 | Remove `[Obsolete]`-tagged constructors | DX clarity |
| 18 | Add `ExecuteDeleteRequest` to `ICreatioClient` | Interface completeness |
| 19 | Unify HTTP stack — remove `HttpWebRequest` paths | Maintainability |
| 20 | Multi-target `netstandard2.0;net8.0` | `HttpMethod.Patch`, `System.Text.Json` |

---

## Backward Compatibility Strategy

All Phase 1–3 changes are **additive**:

- Existing constructors and method signatures are untouched.
- New async methods are added via `ICreatioClientAsync : ICreatioClient` — callers using `ICreatioClient` are unaffected; implementors (mocks) only need to implement the new interface if they want async support.
- Internal refactoring (singleton `HttpClient`, `DelegatingHandler`) is transparent — the public API surface does not change.
- Deprecated patterns (ambiguous constructors, sync-only factory) receive `[Obsolete]` warnings guiding migration without compile errors.

Breaking changes are deferred to Phase 4 (major version bump, semver contract honoured).

---

## Appendix: Creatio API Quick Reference

```
Auth Login:          POST   /ServiceModel/AuthService.svc/Login
OAuth Token:         POST   {IdentityServiceURL}/connect/token
OAuth Discovery:     GET    /0/.well-known/openid-configuration

OData base:          GET    /0/odata/{Entity}?$filter=...&$select=...
OData metadata:      GET    /0/odata/$metadata
OData batch:         POST   /0/odata/$batch

DataService Select:  POST   /0/dataservice/json/SyncReply/SelectQuery
DataService Insert:  POST   /0/dataservice/json/SyncReply/InsertQuery
DataService Update:  POST   /0/dataservice/json/SyncReply/UpdateQuery
DataService Delete:  POST   /0/dataservice/json/SyncReply/DeleteQuery
DataService Batch:   POST   /0/dataservice/json/SyncReply/BatchQuery
Schema Metadata:     POST   /0/DataService/json/SyncReply/RuntimeEntitySchemaRequest

Process Run:         GET    /0/ServiceModel/ProcessEngineService.svc/{Schema}/Execute
Custom Service:      POST   /0/rest/{ServiceName}/{MethodName}

File Upload:         POST   /0/rest/FileApiService/UploadFile
File Download:       GET    /0/rest/FileService/Download/{Schema}/{Id}
File Delete:         POST   /0/rest/GridUtilitiesService/DeleteRecords
OAuth Client Create: POST   /0/rest/OAuthConfigService/AddClient

WebSocket (.NET Core):  wss://{CreatioURL}/msg (SignalR)
WebSocket (.NET Fx):    wss://{CreatioURL}/0/Nui/ViewModule.aspx.ashx
```

### Creatio OData v4 Known Quirks

| Quirk | Correct form | Wrong form |
|---|---|---|
| Date filter | `ModifiedOn gt 2024-05-23T00:00:00Z` | `datetime'2024-05-23'` |
| No `IN` operator | `(Id eq '...' or Id eq '...')` | `Id in ('...','...')` |
| `$expand` scope | Lookup (FK) properties only | Detail (collection) properties |
| CSRF header | `BPMCSRF: {cookie value}` on every mutating request | — |
| Session header | `ForceUseSession: true` on every request | — |
| Pagination | `$top`/`$skip` offset only, max 20 000 rows | No cursor/`nextLink` |
| Max filter nodes | 100 nodes per `$filter` expression | — |
