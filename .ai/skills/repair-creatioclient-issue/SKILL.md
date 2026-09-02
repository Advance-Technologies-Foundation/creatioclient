---
name: repair-creatioclient-issue
description: Implement, validate, review, and deliver the smallest evidence-backed repair for a diagnosed CreatioClient issue. Use only after claim and investigation are complete and the user authorized implementation.
---

# Repair CreatioClient Issue

Implement the smallest complete change that satisfies the investigation's acceptance criteria.

## Preconditions

Confirm the issue is open, assigned to the current GitHub user, linked to exactly one active Development branch,
has an evidence-backed diagnosis, has verified Issue Type and relevant labels, and has verified
`Mitigation stage = Fixing` through `creatioclient-issue-workflow`. Stop on a mismatch rather than bypassing claim
or investigation.

Continue in the claimed isolated worktree and branch. Refresh the canonical default branch before implementation
and incorporate it safely without touching the primary checkout or unrelated user changes.

## Repair

1. Restate the failure, acceptance criteria, compatibility boundary, and smallest sufficient end-to-end fix.
2. Add or refine a deterministic regression test that fails for the diagnosed reason. If such a test is not
   practical, record the concrete proof boundary before editing production code.
3. Implement only the confirmed repair and required tests, documentation, examples, or compatibility work.
4. Run focused validation while iterating. For behavior changes, run both supported targets (`net8.0` and
   `net10.0`); run scoped authentication coverage when authentication changed, and only run live E2E when the
   user explicitly requested it.
5. Review all uncommitted changes, then create the first meaningful commit. Do not create a placeholder commit.
6. Run the repository's `agentic-code-review` pre-PR gate on the complete branch diff, resolve Blocker and High
   findings, and rerun affected validation.
7. Push the claimed branch and immediately open a draft pull request linked with `Fixes #<number>`. Include the
   root cause, observable behavior change, compatibility notes, and exact validation evidence.

## QA and completion

1. When implementation is complete and validation or final review begins, set and verify
   `Mitigation stage = QA`.
2. If a genuine product decision or permission is required, set and verify `Waiting for human approval`, publish
   the exact question, and stop. Return to `Fixing` or `QA` when resolved.
3. After any substantive pushed commit, follow the repository's incremental agentic-review gate. Before marking
   the pull request ready, review the current complete diff, verify the reviewed head is still current, and ensure
   required CI is green.
4. Mark the draft ready only when the repair is complete, validation is recorded, and no blocking findings remain.
   Do not wait for human review or merge unless the user or repository policy requests it.

Report the issue, branch, worktree, pull request, stage, commits, validation, review findings, skipped checks, and
remaining dependency or decision.
