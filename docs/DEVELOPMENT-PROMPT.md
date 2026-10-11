# Project Pulse Development Prompt

Use this prompt when assigning one Project Pulse milestone to a coding agent.

---

You are developing **Project Pulse** in the GitHub repository:

`mialtamo/Project-Pulse`

Project Pulse is a .NET 10 isolated Azure Functions application that polls an RTA/App API for claim work, transforms claims, submits them through Azure API Management for adjudication, writes results back to RTA when enabled, and buffers work to Azure Service Bus during qualifying downstream failures.

A separate repository, `mialtamo/ProjectPulse-API`, provides the current mock RTA/App API for POC development.

Before making any changes, read:

- `README.md`
- `docs/PRODUCT-SPEC.md`
- `docs/ARCHITECTURE.md`
- `docs/SECURITY.md`
- `docs/MILESTONES.md`
- `docs/CURRENT-STATE.md`

Also inspect the current implementation relevant to the assigned milestone.

Do not assume documentation is more current than code. If code and documentation disagree, identify the discrepancy and update `CURRENT-STATE.md` as part of the milestone.

## Development rules

Implement **only the assigned milestone**.

Do not begin later milestones.

Preserve all working behavior from completed milestones unless the assigned milestone explicitly requires changing it.

Prefer incremental changes over broad refactors.

Do not redesign established architecture unless the milestone requires it.

Keep behavior configuration-driven. Do not hardcode environment-specific endpoints or credentials.

Maintain Managed Identity patterns for Azure dependencies.

Never commit:

- credentials,
- secrets,
- connection strings containing keys,
- production-only URLs that should remain configuration,
- PHI,
- real claim payloads.

Do not log claim payloads unless the milestone explicitly uses documented fake POC data.

Project Pulse itself must remain **write-only to the retry Service Bus queue** unless an assigned milestone explicitly changes that architecture. Queue consumption belongs to the separate retry/recovery component.

Preserve correlation and idempotency metadata throughout processing.

## Required workflow

1. Read the current architecture and `CURRENT-STATE.md`.
2. Inspect all source files relevant to the milestone.
3. State a concise implementation plan.
4. Implement only the assigned milestone.
5. Build the complete solution.
6. Run all existing tests.
7. Add tests required by the milestone.
8. Run regression tests for completed milestones.
9. Fix failures introduced by the change.
10. Update all documentation affected by the implementation.
11. Update `docs/CURRENT-STATE.md`.

## CURRENT-STATE.md update requirements

Record:

- milestone completed,
- implementation summary,
- files/components added or changed,
- environment variables added/changed/removed,
- Azure resources or RBAC requirements introduced,
- API contract changes,
- tests added,
- test/build results,
- known limitations,
- deferred work,
- recommended next milestone.

Do not claim a milestone is complete unless its acceptance criteria pass.

Do not begin the next milestone.

## Completion report

At completion provide a concise report containing:

- changes made,
- tests/build results,
- configuration changes required,
- Azure changes required,
- known issues,
- recommended next milestone.

---

## Assigned Milestone

**MILESTONE:** [INSERT MILESTONE NUMBER AND NAME]

**Goal:**  
[COPY GOAL FROM `docs/MILESTONES.md`]

**Required Deliverables:**  
[COPY MILESTONE DELIVERABLES]

**Acceptance Criteria:**  
[COPY MILESTONE ACCEPTANCE CRITERIA]

Implement this milestone only.
