---
name: investigate-creatioclient-issue
description: Investigate a claimed CreatioClient GitHub issue, reproduce or trace the reported behavior, establish ownership, and publish an evidence-backed diagnosis before implementation. Use after claim-creatioclient-issue and before repair-creatioclient-issue.
---

# Investigate CreatioClient Issue

Prove the failure boundary before changing code.

## Preconditions

Confirm the issue is open, assigned to the current GitHub user, linked to the expected Development branch, and
has verified `Mitigation stage = Investigating` through `creatioclient-issue-workflow`. Stop on conflicting
ownership or an unverified stage.

## Diagnose

1. Read the complete issue, comments, labels, Issue Type, Development links, and acceptance evidence.
2. Reproduce the failure or trace it to the real boundary. Prefer a deterministic failing test or minimal probe.
3. Inspect the relevant public API, implementation, tests, documentation, examples, package/runtime constraints,
   and known consumers when compatibility is involved.
4. Check every affected authentication mode, URL shape, redirect/retry path, target runtime, or ownership contract
   that the report can reach; do not generalize from one mode without evidence.
5. Classify the cause as library behavior, documentation/example/workflow, Creatio platform behavior,
   caller configuration or usage, another repository, or insufficient evidence.
6. State the smallest evidence-backed root cause, affected versions or conditions, acceptance criteria, and
   explicit exclusions.

Do not edit product code during investigation. Disposable probes are allowed when they do not alter the candidate
worktree or external systems.

## Normalize metadata

Before handing work to repair:

1. Read live repository labels and enabled organization Issue Types; do not rely on remembered ids or names.
2. Set exactly one matching Issue Type. Use `Bug` for a confirmed defect and `Task` for actionable non-defect work
   when those enabled types apply.
3. Apply at least one relevant existing label. For a confirmed defect, apply the existing `bug` label. Preserve
   other still-relevant labels and remove only labels contradicted by the diagnosis.
4. Re-read the issue and verify both Issue Type and labels. Do not invent metadata to pass the gate.

If another repository owns the defect, identify the correct owner and search for an existing issue. Create or
link downstream work only when the user authorized that external write; otherwise report the required handoff.
Do not manufacture a CreatioClient change when this repository does not own the fix.

## Handoff

Set and verify the stage through `creatioclient-issue-workflow`:

- `Fixing` when implementation is authorized and ready;
- `Waiting for human approval` when a specific decision, permission, or answer is required;
- `Investigating` only while safe evidence gathering can continue.

Return the diagnosis, evidence, verified Issue Type and labels, ownership, acceptance criteria, proposed repair
boundary, and unresolved questions.
