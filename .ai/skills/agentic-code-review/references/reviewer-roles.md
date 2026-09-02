# CreatioClient reviewer roles

Every reviewer returns only actionable, evidenced findings or `No findings`.

## Code quality and maintainability

Check public API shape, compatibility, separation between `CreatioClient` and
`CreatioAuthenticationHandler`, duplication, complexity, error clarity, documentation, and testability. Prefer
the smallest design consistent with existing contracts.

## Security

Check authentication state, token and cookie lifetime, CSRF, TLS behavior, same-origin enforcement, redirects,
URL resolution, secret exposure, file boundaries, serialization, and dependencies. Describe a concrete trigger
and impact; do not invent CVEs or theoretical attacks without evidence.

## Performance and resource lifetime

Check connection pooling, sync-over-async, cancellation, concurrency, locks, repeated authentication, request
and response buffering, streaming, allocations, disposal, and WebSocket lifetime. Report only plausible,
material regressions.

## Testing

Check that tests fail without the change and cover success, protocol failure, retry or replay, cancellation,
concurrency, boundary inputs, public API compatibility, and relevant target frameworks. Prefer deterministic
loopback HTTP tests using NUnit and FluentAssertions.

## Bugs and edge cases

Trace malformed and empty inputs, HTTP status and redirect behavior, repeated 401 responses, cancellation races,
concurrent callers, request-content reuse, response ownership, virtual-directory URLs, encoding, large payloads,
and disposal paths.

## Intent

Compare the complete flow with the shared intent brief. Find omitted requirements, scope drift, compatibility
loss, or components that do not compose into the requested outcome. Do not replace the user's product decision
with reviewer preference.

## KISS

Identify abstractions, state, flags, recovery mechanisms, or duplicate transports that are not required by a
stated behavior or credible failure mode. The suggested simplification must preserve compatibility, security,
and verification requirements.
