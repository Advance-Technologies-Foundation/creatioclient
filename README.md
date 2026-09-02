# Introduction
Creatio Client is a user-friendly connector for Creatio, implemented using **.NET Standard 2.0**
It provides convenient methods for calling various Creatio services and subscribing to WebSocket messages.

## Installation
Use [NuGet](https://www.nuget.org/packages/creatio.client) to install Creatio Client
```
dotnet add package creatio.client
```

## Initialization

You can initialize CreatioClient in three(3) different ways

- Use [Cookie-based authentication](https://academy.creatio.com/docs/8.x/dev/development-on-creatio-platform/integrations-and-api/authentication/authentication-basics/overview)
    ```csharp
    var client = new CreatioClient(<AppUrl>, <UserName>, <UserPassword>);
    ```

    Password login sends the current local time zone offset in the same format as the Creatio browser.
    To preserve a caller-selected offset, pass it to the constructor:
    ```csharp
    var client = new CreatioClient(<AppUrl>, <UserName>, <UserPassword>, timeZoneOffset: -120);
    ```
    You can also set `client.TimeZoneOffset` explicitly before the first request or login. The value is
    UTC minus local time in minutes, matching JavaScript `Date.getTimezoneOffset()`.

- Use [OAuth 2.0](https://academy.creatio.com/docs/8.x/dev/development-on-creatio-platform/integrations-and-api/authentication/oauth-2-0-authorization/identity-service-overview)
    ```csharp
	var client = new CreatioClient(<AppUrl>, <AuthApp>, <ClientId>, <ClientSecret>);
    ```
	`CreateOAuth20Client(<AppUrl>, <AuthApp>, <ClientId>, <ClientSecret>)` remains available for
	backwards compatibility. Client-credentials clients refresh an expired access token and replay the
	request once when Creatio returns HTTP 401. Clients constructed with a pre-issued bearer token do not
	refresh it because they do not own the client credentials.
	`TimeZoneOffset` is a password-login field and is not sent during OAuth client-credentials authentication.

- Use [NTLM user authentication](https://learn.microsoft.com/en-us/troubleshoot/windows-server/windows-security/ntlm-user-authentication)
    ```csharp
    string appUrl = "https://someName. creatio. com";
    CreatioClient client = new(appUrl, true, CredentialCache. DefaultNetworkCredentials);
    ```

## Usage
To call creatio configuration service from your application, use this example:
```
var client = new CreatioClient(<AppUrl>, <UserName>, <UserPassword>);
string request = client.CallConfigurationService(<ServiceName>, <MethodName>, <RequestData>);
```

To execute GET request:
```
string data = client.ExecuteGetRequest(<Url>);
```

To execute POST request:
```
string data = client.ExecutePostRequest(<Url>, <RequestData>);
```

To execute PATCH request (OData v4 partial update of a single record):
```
string data = client.ExecutePatchRequest(<Url>, <RequestData>);
```

To execute a PUT request:
```
string data = client.ExecutePutRequest(<Url>, <RequestData>);
```

For cancellation-aware access to the complete HTTP response, use the async counterparts:
```csharp
using var client = new CreatioClient(<AppUrl>, <UserName>, <UserPassword>);
using HttpResponseMessage response = await client.ExecuteGetRequestAsync(
    <Url>, cancellationToken: cancellationToken);

HttpStatusCode status = response.StatusCode;
HttpResponseHeaders headers = response.Headers;
string content = await response.Content.ReadAsStringAsync();
```

`CreatioClient` implements the additive `IAsyncCreatioClient` interface. The original
`ICreatioClient` interface is unchanged so existing third-party implementations remain binary compatible.

The caller owns every `HttpResponseMessage` returned by an async operation and must dispose it.
`CreatioClient` owns its shared `HttpClient` and should also be disposed when it is no longer needed.
The existing synchronous methods remain available and retain their string, file, and exception behavior.
Cookie-authenticated requests and OAuth client-credentials requests automatically renew expired
authentication once and replay the request;
if the replay is still unauthorized, its response is returned without another login attempt.
Cookie authentication also echoes the CSRF token under the name issued by Creatio: current
`CRT_CSRF` is preferred, legacy `BPMCSRF` remains supported, and no header is invented when the
environment issues neither cookie. OAuth requests remain bearer-only and never add CSRF headers.

Browser and service integrations can reuse the same session without implementing another login client:
```csharp
client.ImportSessionCookies(existingCookies);
IReadOnlyList<CreatioSessionCookie> cookiesForBrowserStorage = client.ExportSessionCookies();
```
The detached cookies preserve browser-relevant `HttpOnly`, `Secure`, `SameSite`, and expiry metadata and
contain authentication secrets. Protect them like credentials.

Bearer integrations that need an explicit certificate policy can use the four-argument constructor:
```csharp
using CreatioClient client = new(appUrl, bearerToken, useUntrustedSsl: false, isNetCore: true);
```

Use `UploadImageAsync` for the Creatio Image API's single-request binary upload. The caller supplies the
fully resolved Image API URL and owns the returned response:
```csharp
using HttpResponseMessage upload = await client.UploadImageAsync(
    imageApiUrl, imageBytes, "logo.png", "image/png", cancellationToken: cancellationToken);
```
For synchronous protocol errors, `WebException.Response` remains castable to `HttpWebResponse` and
preserves the status, description, headers, request URI, method, and buffered error body used by known
consumers. It is a bounded compatibility view; use the async API when other response metadata is required.
Authenticated request URLs must use the configured Creatio origin or a same-host HTTP-to-HTTPS upgrade;
the client rejects unrelated cross-origin requests rather than forwarding bearer, cookie, CSRF, or Windows credentials.

The CI coverage gate enforces 100% line and branch coverage for the authentication handler pipeline.
DTO property bags, WebSocket listeners, the compatibility-only `HttpWebRequest` extension surface, and
the synchronous facade are excluded from that narrowly defined unit scope; they are covered by API,
characterization, and live E2E gates instead of being counted toward an artificial package-wide percentage.

Subscribe to WebSocket messages:
```csharp

const string logFile = "ws.json";
CreatioClient client = new(app, username, password, true, true);

client.ConnectionStateChanged += (sender, state) => {
    Console.WriteLine($"Connection state changed to: {state}");
};

JsonSerializerOptions opts = new() {
    WriteIndented = true,
};
client.MessageReceived += (sender, message) => {
    var msgObject = new {
        Header = message.Header,
        Id = message.Id,
        Body = JsonSerializer.Deserialize(message.Body, typeof(object), opts)
    };
    System.IO.File.AppendAllText(logFile,JsonSerializer.Serialize(msgObject, opts), Encoding.UTF8);
};
client.StartListening(CancellationToken.None);
Console.ReadLine();
```
# nuget.org
https://www.nuget.org/packages/creatio.client
