# creatio.client
Easy connector .net standard connector for creatio

For call creatio configuration service from you application just write code:
```
var client = new CreatioClient(<AppUrl>, <UserName>, <UserPassword>);
string request = client.CallConfigurationService(<ServiceName>, <MethodName>, <RequestData>);
```

For call any creatio enpoint you can Get request:
```
string data = client.ExecuteGetRequest(<Url>);
```
or POST:
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