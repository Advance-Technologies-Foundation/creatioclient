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

# nuget.org
https://www.nuget.org/packages/creatio.client