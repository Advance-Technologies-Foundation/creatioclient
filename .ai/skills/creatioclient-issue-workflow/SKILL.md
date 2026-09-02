---
name: creatioclient-issue-workflow
description: Coordinate any authorized Advance-Technologies-Foundation/creatioclient change from a documented GitHub issue through visible claim, diagnosis, repair, and a closing pull request. Use when the user asks to take, triage, fix, implement, resolve, or deliver a CreatioClient change. Do not mutate GitHub or files during brainstorming, explanation, planning, or review-only requests.
---

# CreatioClient Issue Workflow

Keep the workflow visible in GitHub and small enough to explain as:

`document issue -> claim and branch -> investigate -> repair and verify -> pull request -> merge closes issue`

Every pull request must be backed by one open issue in `Advance-Technologies-Foundation/creatioclient` before its
Development branch is created.

## Ensure the issue exists

1. When the user supplies an issue number or URL, read and use that issue.
2. When the user authorizes a change without an issue, search open and closed repository issues for the same
   problem or requested outcome. Reuse a matching open issue; do not create a duplicate or repurpose an unrelated
   issue.
3. When no matching issue exists, create one before claiming or branching. Document the observed problem or
   requested outcome, evidence or reproduction when available, expected behavior, acceptance criteria, and
   explicit exclusions. Do not create an empty or placeholder issue.
4. Keep the issue open throughout investigation and repair. The linked pull request closes it on merge through
   `Fixes #<number>`; do not manually close it early.

The user's authorization to implement or fix a repository change includes creating its required repository issue.
It does not authorize writes to another repository or reopening a closed issue.

## Route the request

- For `take`, `triage`, `fix`, `implement`, or `resolve`, ensure the issue exists, then begin with the
  `claim-creatioclient-issue` skill.
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
- The pull request's `Fixes #<number>` reference closes the issue only when the pull request merges.

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
