---
name: agentic-code-review
description: Run a comprehensive multi-perspective review of CreatioClient branch, pull-request, commit, or architecture changes. Use for pre-PR, pre-merge, branch, PR, comprehensive, parallel, or agentic review requests; do not use for ordinary implementation unless a repository review gate is due.
---

# Agentic Code Review

Review the requested change against its stated outcome and CreatioClient's compatibility, authentication, and
transport invariants. Findings are the deliverable; do not modify the candidate change.

## Workflow

1. Read `AGENTS.md` from the repository root.
2. Restate one shared intent brief containing the requested outcome, explicit constraints, and proof boundary.
3. Resolve the review scope:
   - branch or pre-PR: diff the merge base with `origin/main` against `HEAD`;
   - pull request: verify the live base and head, then use the exact head diff;
   - incremental: review only the named commit or newly pushed commit range;
   - architecture: review the proposal plus the smallest relevant source context.
4. Record the base/head SHAs, commits, diff stat, and changed files. Include uncommitted and staged changes only
   when the user puts them in scope.
5. From the repository root, read `.ai/skills/agentic-code-review/references/reviewer-roles.md`.
6. Run independent reviewers in parallel when subagents are available and authorized. Always cover code
   quality, security, performance, testing, bugs, intent, and KISS. Keep intent and KISS as separate reviewers.
7. Give every reviewer the same intent brief, repository rules, exact scope, changed files, and relevant diff.
   Reviewers may run read-only checks and disposable probes but must not edit the candidate worktree.
8. Verify every important finding yourself against current source and discard speculative or duplicate reports.
9. For a final gate, run or inspect the relevant test matrix and confirm the reviewed head has not moved.

## CreatioClient review invariants

- Existing synchronous contracts, exceptions, response bodies, and public members remain compatible unless a
  deliberate breaking change is explicitly authorized.
- Do not add members to `ICreatioClient` without treating third-party implementations as affected consumers.
- Cookie, OAuth client-credentials, raw bearer, and NTLM modes retain their distinct login and renewal behavior.
- Credentials, cookies, CSRF values, and bearer tokens never cross an untrusted origin or appear in output.
- Redirects, retries, and authentication replay are bounded and recreate disposable request content safely.
- Cancellation reaches network and streaming operations; response and stream ownership remains explicit.
- The per-client pooled `HttpClient` and authentication handler remain the single transport pipeline.
- Tests use NUnit and FluentAssertions and cover `net8.0` and `net10.0`; live E2E is opt-in.

## Report

Report findings first, ordered by `Blocker`, `High`, `Medium`, then `Low`. Each finding includes the lens,
file and tight line range, trigger, evidence, impact, and smallest viable fix. Then report open questions that
affect confidence, reviewed base/head, perspectives used, tests run or skipped, and residual risk. If there are
no material findings, say so directly.
