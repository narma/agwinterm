# Proposal

## Why

C6 lets scripts arrange a working set without guessing UI order or repeating destructive commands. Existing session moves are single-target, workspace focus is one nullable ID, and the flagged sidebar has one names-only layout; these paths need explicit placement, batching and retained focus membership.

## What Changes

- Add workspace-wide flagging including hidden sessions and collapsed workspace creation.
- Add deterministic before/after placement for new/moved sessions, plus explicit bounded batch close/move with per-target outcomes.
- Add flat/plain/tree flagged layouts and multiple focused workspaces with an independent filter switch and additive persistence migration.
- Keep existing single-target commands and repeated-selector refusals. Preserve the local names-only flagged default and per-window sidebar settings.

## Capabilities

### New Capabilities

- `working-set-organization`: Ordered session placement, batch outcomes and persistent sidebar working sets.

### Modified Capabilities

- `control-cli-contract`: Clarify that repeated single-value selectors still refuse while batch execution requires a distinct explicit argument.

## Impact

Native session/workspace mutation and sidebar rendering/navigation, control server/host, CLI, additive restore state, menus, help and agent documentation. C0/C1 are prerequisites; C5 is archived and committed as `d5ccf20`. Existing UI navigation and empty-workspace behavior remain readiness checks.

Reference: [agterm v0.38.1 flagged-layout tests](https://github.com/umputun/agterm/blob/v0.38.1/agtermTests/SidebarFlaggedLayoutTests.swift) establish flat labels with workspace suffix, plain names and grouped tree. Local default and per-window scope deliberately follow agwinterm rather than upstream's global default.
