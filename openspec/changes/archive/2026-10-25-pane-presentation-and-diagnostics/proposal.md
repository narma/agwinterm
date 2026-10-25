# Proposal

## Why

C8 finishes the adopted roadmap with pane-local presentation and honest runtime diagnostics. The project already has session image backgrounds, status colors and separate Windows light/dark theme choices; those should be reused and verified rather than reintroduced.

## What Changes

- Add persistent per-pane presentation overrides: solid fallback color, bounded native text watermark, status indicator color/shape, explicit inheritance and clearing.
- Verify and document existing optional light/dark themes and single-theme fallback, including terminal/chrome updates.
- Expose effective keymap readback, shared config reload with diagnostics and running versus on-disk executable version metadata.
- Preserve existing session image backgrounds, status priority/notifications, custom-command listing and offline CLI version behavior.

## Capabilities

### New Capabilities

- `native-pane-presentation`: Pane-owned appearance and optional system-theme selection.
- `runtime-diagnostics`: Effective bindings, config reload feedback and executable version comparison.

### Modified Capabilities

None. HUD is passive transient content and remains a separate capability; pane watermarks are persisted presentation. Existing CLI version diagnostics retain their contract.

## Impact

Native pane rendering/sidebar indicators, additive PaneState restore/recent fields, existing theme/config/keymap application, optional host API, CLI/help/agent docs and native/managed acceptance. Depends on C0 and C4; follows the separate C7 commit. No hooks executor, HTML, installation or updates are introduced.
