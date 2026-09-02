---
name: creatioclient-issue-workflow
description: Coordinate an Advance-Technologies-Foundation/creatioclient GitHub issue from visible claim through evidence-backed diagnosis and verified repair. Use when the user asks to take, triage, fix, implement, or resolve a CreatioClient issue. Do not mutate GitHub or files during brainstorming, explanation, planning, or review-only requests.
---

# CreatioClient Issue Workflow

Keep the workflow visible in GitHub and small enough to explain as:

`claim -> investigate -> repair and verify`

Treat the issue number or URL as required input.

## Route the request

- For `take`, `triage`, `fix`, `implement`, or `resolve`, begin with the `claim-creatioclient-issue` skill.
- Continue with `investigate-creatioclient-issue` to prove the failure boundary and ownership.
- Use `repair-creatioclient-issue` only when the user authorized implementation. A triage-only request stops
  after publishing the diagnosis.
- For brainstorming, explanation, planning, or review-only work, remain read-only and do not claim the issue.

Use the phase skills as the procedures; do not duplicate them here.

## GitHub visibility

Use GitHub's existing primitives only:

- Assignee identifies the coordinator.
- Issue Type and labels record the evidence-backed classification.
- `Mitigation stage` records `Investigating`, `Fixing`, `QA`, or `Waiting for human approval`.
- Development links expose the active branch and draft pull request.

Do not add claim records, leases, lock files, custom refs, or another state store.

### Mitigation stage readiness

`Mitigation stage` is a pre-provisioned organization issue field. Agents must never create, delete, or
reconfigure it. Before the first GitHub write in each workflow, verify:

- organization `Advance-Technologies-Foundation`;
- exact name `Mitigation stage`;
- type `single_select`;
- visibility `all`;
- options include `Investigating`, `Fixing`, `QA`, and `Waiting for human approval`, regardless of order.

Use the GitHub issue-field REST API with `X-GitHub-Api-Version: 2026-03-10`:

1. `GET /orgs/Advance-Technologies-Foundation/issue-fields`, select the exact field name, and resolve its id
   dynamically.
2. `POST /repos/Advance-Technologies-Foundation/creatioclient/issues/NUMBER/issue-field-values` with only
   `{"issue_field_values":[{"field_id":FIELD_ID,"value":"STAGE"}]}`.
3. `GET /repos/Advance-Technologies-Foundation/creatioclient/issues/NUMBER/issue-field-values` and verify the
   stored value.

Do not use `PUT`, hardcode the field id, infer option order from the response, substitute a label or Projects
field, or claim an unverified stage change. Stop before any write if readiness fails.

## Completion

Report the issue, assignee, stage, branch or pull request, diagnosis, validation state, and any exact human
decision still required.
