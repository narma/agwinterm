# Proposal

## Why

Program overlays currently have to close to stop capturing input, and floating size uses one percentage for both axes. Roadmap C2 adds reversible visibility and Windows-native geometry while preserving P5 slot ownership and process outcomes.

## What Changes

- Add hide/show/toggle for session and pane slots, preserving terminal IDs, processes and results; expose requested and effective visibility.
- Add independent DIP width/height, percent fallback per axis, explicit full-size reset and effective geometry readback for session overlays only.
- Add open cwd, follow and background-color options, resolved in the target Windows/WSL profile.
- Publish committed visibility and resize lifecycle events through C1.
- Preserve session-wide result, block/wait, replacement versus pane-slot refusal, stable IDs across swaps, and full-pane pane overlays.

## Capabilities

### New Capabilities

- `program-overlay-controls`: overlay visibility, session floating geometry and launch customization with native input/rendering and lifetime guarantees.

### Modified Capabilities

None. C1 explicitly reserves overlay hide/show for this stage; its observation identity and delivery contracts remain unchanged. C0 target-routing rules remain unchanged.

## Impact

Depends on completed C0 and C1. Touches CLI validation, control wire/host seam, overlay slot state, shared surface/input/layout/rendering, tree snapshots, native tests and Windows/WSL integration acceptance. No new runtime dependency, persistence of ephemeral overlays, release or live installation. C3 dialogs, C4 HUD and floating pane overlays remain outside this change.
