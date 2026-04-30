---
name: Bug report
about: Something in creatio.client is not working correctly
title: "[BUG] "
labels: bug
assignees: ''
---

## Summary

<!-- One sentence: what went wrong. -->

## Environment

| Field | Value |
|-------|-------|
| creatio.client version | <!-- e.g. 1.0.34 --> |
| .NET version | <!-- e.g. net8.0, netstandard2.0 host --> |
| OS | <!-- e.g. Windows 11, Ubuntu 22.04, macOS 14 --> |
| Creatio version / cloud | <!-- e.g. 8.2.2 Freedom, cloud.creatio.com --> |
| Auth method used | <!-- Cookie / OAuth 2.0 / NTLM --> |
| `isNetCore` flag | <!-- true / false --> |
| `useUntrustedSsl` flag | <!-- true / false (default is true) --> |

## Steps to Reproduce

1. 
2. 
3. 

Minimal code snippet that triggers the issue:

```csharp
// paste your code here
var client = new CreatioClient("https://...", "user", "pass");
// ...
```

## Expected Behaviour

<!-- What should have happened. -->

## Actual Behaviour

<!-- What actually happened. Include the full exception message + stack trace if applicable. -->

```
// paste exception / error output here
```

## HTTP Request / Response (if relevant)

<!-- If the bug is network-related, capture the raw request and response
     (strip credentials before pasting). Fiddler or browser DevTools Network tab work well. -->

```
// HTTP details here
```

## Workaround (if any)

<!-- If you found a workaround, describe it — this helps triage. -->

## Additional Context

<!-- Any other details, links to related issues, or screenshots. -->
