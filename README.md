# bpmclient
Easy connector .net standard connector for bpmonline

For call bpmonline configuration service from you application just write code:
```
var client = new BpmonlineClient(<AppUrl>, <UserName>, <UserPassword>);
string request = client.CallConfigurationService(<ServiceName>, <MethodName>, <RequestData>);
```

For call any bpmonline enpoint you can Get request:
```
string data = client.ExecuteGetRequest(<Url>);
```
or POST:
```
string data = client.ExecutePostRequest(<Url>, <RequestData>);
```