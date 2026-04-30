---
name: Feature request
about: Suggest a new capability or improvement for creatio.client
title: "[FEATURE] "
labels: enhancement
assignees: ''
---

## Summary

<!-- One sentence: what capability would you like to see added or improved. -->

## Motivation

<!-- Why do you need this? What problem does it solve?
     The more context you provide, the easier it is to evaluate and implement. -->

## Proposed API / Behaviour

<!-- If you have a concrete idea for how the feature should look, show it here.
     Even rough pseudocode is helpful. -->

```csharp
// Example: new method signature
Task<MyDto> ExecuteGetRequestAsync<MyDto>(string url, int timeout = 100_000);

// Example: new constructor overload
CreatioClient client = new(appUrl, userName, password, httpClientFactory: myFactory);
```

## Alternatives Considered

<!-- Did you consider a different approach? Why did you prefer the one above? -->

## Affected Public Surface

<!-- Which class / interface / method would change?
     - [ ] ICreatioClient (public interface)
     - [ ] CreatioClient constructors
     - [ ] HTTP methods (Get / Post / Delete)
     - [ ] File upload / download
     - [ ] WebSocket / real-time
     - [ ] Retry policy
     - [ ] DTOs
     - [ ] Other: ________ -->

## Breaking Change Risk

<!-- Would this change the existing public API in a way that breaks callers?
     - [ ] No — purely additive
     - [ ] Maybe — new overload could cause ambiguity
     - [ ] Yes — existing signature changes (requires major version bump) -->

## Priority / Use Case

<!-- How important is this to you? What is your use case?
     (e.g. "We call 1 000 endpoints per minute and the non-pooled HttpClient causes port exhaustion") -->

## Additional Context

<!-- Links to Creatio API docs, related issues, screenshots, etc. -->
