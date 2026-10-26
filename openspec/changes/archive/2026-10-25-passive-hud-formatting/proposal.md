# Proposal

## Why

Roadmap C4 makes passive status panels useful for timed messages, pane-local captions and readable multiline summaries. The current native HUD already preserves terminal input and shared-slot lifetime, but only renders short plain text with fixed margins and no expiry.

## What Changes

- Add elapsed-time hide-after, sticky edge placement, frameless presentation, explicit DIP font size and stable main/split pane anchoring.
- Add an opt-in bounded Markdown subset rendered natively, plus bounded UTF-8 message-file input at the CLI with the existing Windows/WSL path policy.
- Expose effective geometry, visibility and style without changing legacy plain-text validation or program-overlay ownership.
- Extend HUD observation with expiry and stable anchor ownership; keep input, terminal dimensions/history and notifications unaffected.

## Capabilities

### New Capabilities

- `passive-session-hud`: native HUD lifetime, placement, bounded formatting and observable presentation.

### Modified Capabilities

- `control-event-observation`: extend the existing HUD lifecycle payload with expiry reason and optional anchor pane ID while retaining all other event contracts.

## Impact

Depends on C1 and C2, delivered after C3. Pty validates options and projects state; Core supplies bounded text formatting; Ctl reads message files; Win32 owns layout/rendering, pane identity and monotonic expiry. Tests include legacy HUD regression, lifetime races, geometry/readback, Unicode formatting and real WSL file paths. No HTML engine, clickable links, shell renderer, generated message files, new external dependencies, persisted HUD state or live installation.

Reference semantics checked against [agterm v0.38.1 Hud.swift](https://github.com/umputun/agterm/blob/v0.38.1/agtermCore/Sources/agtermCore/Hud.swift): sticky means flush edge placement, not persistent lifetime; zero hide-after means untimed. Native rendering permits font updates in place, unlike upstream's creation-only terminal surface font.
