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

- Use [OAuth 2.0](https://academy.creatio.com/docs/8.x/dev/development-on-creatio-platform/integrations-and-api/authentication/oauth-2-0-authorization/identity-service-overview)
    ```csharp
   var client = new CreatioClient(<AppUrl>, <ClientId>, <ClientSecret>, <UserName>, <UserPassword>);
    ```

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