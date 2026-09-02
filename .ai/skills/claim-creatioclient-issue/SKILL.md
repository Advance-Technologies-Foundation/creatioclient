---
name: claim-creatioclient-issue
description: Claim an open Advance-Technologies-Foundation/creatioclient issue before investigation or implementation by assigning the current GitHub user and creating its predictable linked development branch. Use only after the user authorizes taking or triaging an issue.
---

# Claim CreatioClient Issue

Claiming means assigning the authenticated GitHub user. The Development branch provides visibility and
navigation; it is not a distributed lock.

## Claim

1. Run the `creatioclient-issue-workflow` read-only `Mitigation stage` readiness check. Stop before any GitHub
   write if it fails.
2. Read the live issue, state, assignees, Development branches, and pull requests.
3. Resolve the authenticated login with `gh api user --jq .login`; never use a display name.
4. Handle current state:
   - closed issue: stop unless the user explicitly authorized reopening it;
   - unassigned with no Development branch: assign the current login;
   - current login assigned with the expected branch: resume idempotently;
   - current login assigned with no branch: create and link it;
   - current login assigned with a different branch: use it only after confirming it owns this work;
   - another user assigned: stop and report their linked branch, or the missing visibility if none exists;
   - multiple assignees or a branch without an assignee: stop and report the ambiguity.
5. After assigning, re-read assignees once and stop if another assignee appeared.
6. Set and verify `Mitigation stage = Investigating` using `creatioclient-issue-workflow`.
7. For a new branch, use `<login>/issue-<number>` from the live canonical default branch and link it through
   GitHub Development. Prefer `gh issue develop` when available.
8. Fetch that remote branch and create an isolated linked worktree named `issue-<number>` in the repository's
   established task-worktree area. Track the remote branch and preserve the primary checkout and unrelated
   user changes.

Do not inspect code, diagnose the report, or create a pull request until the claim is established. If a later
setup step fails, keep successful visible GitHub state, report the incomplete step, and stop; do not add rollback
machinery or a routine claim comment.

## Handoff

Return the issue URL, login, assignment result, branch, Development link, worktree, and verified stage.
Investigation may proceed only when the current user owns the issue and its Development branch exists.
