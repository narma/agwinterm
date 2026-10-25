# Proposal

## Why

C7 exposes existing Windows UI operations to scripts and adds explicit monitor-aware placement. Scripts currently cannot minimize, restore or navigate windows or reveal a targeted session directory reliably through the control API.

## What Changes

- Add window next/previous, minimize/restore, minimized creation and fullscreen on/off/toggle.
- Add monitor enumeration and explicit monitor placement with effective physical geometry readback.
- Add targeted session directory Reveal using the addressed Windows/WSL profile and cwd.
- Preserve existing window commands, quick-host restrictions and last-window lifecycle rules.

## Capabilities

### New Capabilities

- `native-window-control`: Window navigation, background-safe presentation, monitor placement and targeted directory Reveal.

### Modified Capabilities

None. Existing routing requirements concern focus, resize and custom commands; the new capability applies equivalent strict selection to these additional operations.

## Impact

Native window creation and UI operations, Windows monitor interop, control host/server, CLI/help/agent docs, owned native acceptance and WSL path checks. Depends on archived C0/C1; follows C6 as a separate implementation and commit. No release, live installation or changes to the user's monitor settings.
