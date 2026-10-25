# Proposal

## Why

Automation can currently receive exit code zero for a refused JSON request, lose unsupported options silently, or direct focus/resize/custom-command work at the active session despite an explicit target. Correct these outcomes before adopting the event, overlay and dialog capabilities in stage C0 of [the adoption plan](../../../docs/plans/2026-10-25-control-capabilities.md).

## What Changes

- **BREAKING**: ordinary CLI requests return failure for `ok:false` even under `--json`; documented offline-version and picker behavior remains unchanged.
- **BREAKING**: reject unsupported options, malformed values and duplicate single-value selectors before sending a request, instead of silently ignoring or overwriting them.
- Honor explicit session/pane/window targets for `session focus`, `session resize` and `command run`; refuse invalid targets without falling back to the active session.
- Preserve existing untargeted active-session behavior for these three operations, Windows/WSL execution context, stable pane IDs and established command modes.
- Add behavioral regression coverage and update CLI/API/agent documentation.

## Capabilities

### New Capabilities

- `control-cli-contract`: argument validation and truthful process outcomes for control CLI calls.
- `control-target-routing`: explicit target resolution and background-session behavior for focus, resize and custom-command execution.

These are new OpenSpec capability records for an existing implementation; the project has no main specs yet.

### Modified Capabilities

None.

## Impact

Affected areas: `src/Agwinterm.Ctl`, control dispatch and host interfaces in `src/Agwinterm.Pty`, Win32 control-host/custom-command routing, nearby Pty tests and isolated integration coverage. No new runtime dependency or transport is needed. Help, `AgentSkill.cs` and `docs/control-api.md` must describe compatibility changes.

Only C0 is proposed here. C1–C8 remain separate future changes in the roadmap. No event streaming, new UI, zmx, browser renderer, restore-semantic change, automatic Lite mirror or release is included. Existing unrelated working-tree changes must be preserved.
