# working-set-organization Specification

## Purpose

Organize local session working sets with deterministic placement, explicit batch results and persistent sidebar filters without changing terminal identity or silently widening targets.

## Requirements

### Requirement: Workspace operations include hidden members
`workspace flag on/off/toggle` SHALL affect every session of the resolved workspace regardless of sidebar visibility or collapse. Toggle SHALL invert each member individually. `workspace new --collapsed` SHALL create an empty collapsed workspace. Unknown or ambiguous workspaces and quick targets SHALL refuse before mutation.

#### Scenario: Hidden members
- **WHEN** a collapsed workspace containing hidden sessions is flagged on
- **THEN** all members become flagged while session selection and terminal input remain unchanged

### Requirement: Relative placement resolves before mutation
Session new/move SHALL accept exactly one before/after session anchor in the routed window. The anchor SHALL determine the destination workspace; an explicit workspace SHALL agree with it. Anchors SHALL resolve exact session ID, unique ID prefix or unique name; split-only and auxiliary targets SHALL refuse. A new workspace cannot be created implicitly for an anchored placement.

#### Scenario: Place across workspaces
- **WHEN** a session moves before an anchor in another workspace
- **THEN** it becomes the anchor's immediate predecessor with stable session/pane IDs; an ambiguous anchor refuses without moving anything

### Requirement: Batch execution requires an explicit bounded list
Batch close/move SHALL use CLI --targets comma-separated selectors or a wire targets array, distinct from single --target. Lists SHALL contain 1..128 nonempty selectors. Duplicate selectors or aliases resolving to the same session SHALL refuse the whole request before mutation. A move anchor SHALL not also be a batch member.

#### Scenario: Duplicate aliases
- **WHEN** a batch includes a session's ID and its uniquely matching name
- **THEN** it refuses before changing any session, rather than applying the operation twice

### Requirement: Batch outcomes describe actual per-target work
After request-wide validation, eligible batch targets SHALL execute in input order. Unresolved or unsupported targets SHALL have individual errors while other targets proceed. The reply SHALL include requested selector, resolved session ID, changed flag and error per item, plus completed/partial outcome. A valid partial batch SHALL be accepted with exit 0; request-wide refusal SHALL exit 1. No rollback SHALL be implied.

#### Scenario: Mixed close result
- **WHEN** one batch target exists and another is missing
- **THEN** the existing session closes once and enters recent history, the missing target reports an error, and the reply is explicitly partial

### Requirement: Batch placement preserves input order
A batch move to a workspace SHALL append successes in input order, or insert them as one ordered group immediately before/after an anchor. Invalid destinations SHALL refuse before any member moves. Existing single-session move directions and ordinary new-session defaults SHALL remain supported.

#### Scenario: Ordered insertion
- **WHEN** B then A are moved after C
- **THEN** the resulting order is C, B, A with no duplicate membership or changed pane identity

### Requirement: Flagged view has three explicit layouts
Flagged layout SHALL be plain (session names), flat (session names with workspace suffix), or tree (workspace groups containing only flagged members). Plain SHALL remain the local default. Layout SHALL be per window, persisted, and independent of ordinary sidebar mode. Flagged views SHALL ignore workspace focus filtering and omit groups with no flags.

#### Scenario: Change flagged layout
- **WHEN** layout changes from plain to flat to tree
- **THEN** labels/groups update while selection, flags, terminal contents and ordinary workspace membership remain unchanged

### Requirement: Workspace focus membership is independent of filtering
Users SHALL be able to add/remove/toggle multiple workspace memberships and separately enable/disable the filter. Disabling the filter SHALL retain membership. Clear SHALL remove all memberships and disable filtering. Removing a workspace SHALL remove only its membership. An enabled empty set SHALL show all workspaces. Explicit selection outside the enabled set SHALL disable filtering without erasing membership.

#### Scenario: Temporarily show everything
- **WHEN** two workspaces are focused, the filter is disabled and later enabled
- **THEN** both remembered members return to the filtered view with their prior order and collapse state

### Requirement: Navigation uses the effective visible order
Ordinary session navigation SHALL use the effective sidebar session order, respecting workspace filtering, flags and collapsed groups. Workspace navigation SHALL retain wrapping, collapsed-workspace eligibility and refusal in flagged mode. Empty workspace selection SHALL retain its existing placement-target semantics without creating a shell.

#### Scenario: Navigate filtered work
- **WHEN** next/previous is invoked while only a subset is visible
- **THEN** it steps within that subset without selecting a hidden session or discarding focus membership

### Requirement: Working-set persistence is additive and validated
Restore state SHALL add optional focus-membership, filter-enabled and flagged-layout fields. A legacy single focused workspace SHALL migrate to a one-member enabled set. Unknown IDs, duplicates and invalid layout values SHALL be normalized safely. Readback SHALL expose effective settings and visible session order. Existing fields and default restore compatibility SHALL remain available.

#### Scenario: Restore legacy and new state
- **WHEN** either an old single-focus file or a new disabled multi-focus file is loaded
- **THEN** old focus remains effective, while new disabled membership survives without unexpectedly hiding workspaces
