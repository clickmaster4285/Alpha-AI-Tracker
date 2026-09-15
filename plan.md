# Plan: Client Terms Acceptance Flow

Status: plan-only; no code changes yet. Awaiting review before implementation.

## Objective
Implement the client-side Terms & Conditions workflow so the desktop app fetches the active server terms on boot and version upgrades, stores them in SQLite, prompts the employee in a locked modal flow, and only lets the app continue after all pending terms are accepted.

## Scope summary
The requirement is a fullscreen, always-on-top, non-closeable/non-minimizable modal named for the client terms experience. It must:
- fetch the active terms from the server (`is_active = 1`)
- compare against local SQLite entries
- persist only new or changed terms as pending local rows with `is_accepted` = 0
- show a loading state while checking the server
- gracefully fall back to locally stored pending terms when offline
- render each term in a guided step-by-step acceptance flow
- prevent the UI from being dismissed until all terms are accepted
- support a locked window state with required OS permissions / window flags (always on top, always open, non-closeable, non-minimizable)

## Planned workstreams

### 1) Client contract and storage design
- Define the client-side model for `client_terms` in SQLite.
- Confirm required fields: term identity (server version/id), source/version hash, text payload, `is_accepted`, status timestamps, and sync metadata.
- Add migration logic that preserves historical accepted terms while creating pending rows for new or changed terms.
- Decide how term equality is calculated: compare server term id / version / fingerprint / normalized body.

### 2) Server/client sync contract
- Confirm the server endpoint/response shape for active terms and versioning.
- Add client fetch logic that runs on boot and on app upgrade detection.
- Implement a compare-and-diff flow: fetch all active terms, compare to local rows, insert only missing or changed rows as `is_accepted = 0`.
- Avoid re-creating accepted terms that already match local content.

### 3) Client bootstrap gating and startup flow
- Trigger the terms check at app startup and after version upgrades.
- Ensure the modal opens before the main client dashboard/worker flow continues.
- If server is unreachable, use the locally stored pending terms queue instead of blocking indefinitely.
- Ensure pending terms are enforced until all are accepted.

### 4) Intense modal UX / locked window behavior
- Create a dedicated client modal window with:
  - maximized full-screen presentation
  - always on top
  - no close button
  - no minimize button
  - no window dismissal while terms remain pending
  - loading state while checking server
- Use a step-by-step UI pattern: one term at a time or sequential card flow.
- Add accept action per term; block closing until the final term is accepted.

### 5) Permission / OS-level requirements
- Identify the minimum runtime permissions and window flags required for the platform.
- Map platform-specific requirements for Linux/Windows/macOS if the environment differs.
- Ensure the modal opens in a mode that cannot be bypassed by user interaction.
- Document any dependency on system/window manager behavior or user policy.

### 6) Validation and risk control
- Verify the SQLite migration / compare logic against realistic scenarios:
  - first run
  - reboot with no internet
  - new server version
  - changed term text
  - already accepted term
  - app upgrade with existing accepted records
- Validate the locked modal behavior in an installed build when feasible.
- Ensure no unrelated client startup logic is blocked beyond the T&C gate.

## Proposed file touchpoints
Likely impacted areas (subject to validation during implementation):
- `client/` SQLite schema and migration code
- `client/Services/` startup/bootstrap services
- `client/Views/` and `ViewModels/` for the terms modal
- app startup and window lifecycle code
- any server DTO/API contract for active terms

## Risks / decisions to confirm during review
- Whether the server already exposes a versioned active-terms list or needs a new endpoint/contract.
- Whether accepted status should be per user, per machine, or per employee device.
- Whether the modal should be shown once per pending queue or once per reboot only.
- Whether terms are required to be accepted in a strict sequential order or can be accepted individually in any order.
- Whether version upgrade detection is based on app version string, build number, or a dedicated terms version field.

## Execution plan after review
1. Confirm server contract and exact model fields.
2. Implement SQLite schema + migration + diff logic.
3. Add client boot-time fetch and fallback logic.
4. Build the locked full-screen terms modal and acceptance flow.
5. Validate startup, offline fallback, and upgrade scenarios.
6. Report final evidence and any remaining blockers.

This plan intentionally avoids code changes until reviewed and approved.
