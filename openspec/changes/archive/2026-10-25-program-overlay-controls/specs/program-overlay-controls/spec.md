# Spec Delta

## Purpose

Control program overlays without destroying their processes, with predictable Windows geometry and launch context while preserving established session and pane slot contracts.

## ADDED Requirements

### Requirement: Visibility preserves the overlay lifetime
`session overlay hide|show|toggle` SHALL address the existing session or pane slot and change its requested visibility without recreating the terminal, process or ID. Empty slots SHALL refuse. Repeated hide/show SHALL succeed without false change events. Background visibility changes SHALL not select the owner.

#### Scenario: Hide running and exited programs
- **WHEN** a running overlay or an exited overlay retained by --wait is hidden then shown
- **THEN** it retains its ID, buffer, process outcome and block waiter; hiding does not complete a waiter or consume the exit banner

### Requirement: Hidden overlays do not capture ordinary input
Hidden overlays SHALL not be the active input, mouse, wheel, selection or accessibility surface. Explicit overlay-ID reads and writes SHALL still address that terminal while hidden, subject to existing input-closed and readonly checks. Showing SHALL restore ordinary surface routing without changing pane IDs or pane focus.

#### Scenario: Hidden pane overlay after swap
- **WHEN** a hidden pane overlay's owner swaps sides
- **THEN** the overlay remains attached to that owner and hidden; ordinary input reaches the underlying pane and explicit overlay-ID operations still reach the overlay

### Requirement: Tree distinguishes requested and effective visibility
Tree SHALL preserve existing overlay presence fields and add `overlayDetails`, an array of occupied slots with id, slot, ownerPaneId for pane slots, hidden, visible, exited and geometry. Visible SHALL reflect the selected session, shown cover and pane occlusion, not just the hidden flag. Geometry SHALL expose effective client-pixel x/y/width/height and requested percent/widthDip/heightDip for session slots.

#### Scenario: Background and occluded slots
- **WHEN** a session has a shown session overlay over pane overlays, or the session is not selected
- **THEN** tree reports the occupied hidden/occluded slots with visible false and stable IDs instead of omitting them

### Requirement: Session overlay dimensions are independent
Open/resize SHALL accept integer `--width` and `--height` in DIP, 1..16384, independently overriding the corresponding `--size-percent` axis. Omitted axes use percentage or full content bounds. Resize SHALL retain omitted settings; supplying size-percent SHALL reset DIP overrides before applying supplied dimensions. `resize --full` SHALL reset all sizing and conflict with other size options.

#### Scenario: Mixed axes and full reset
- **WHEN** an overlay opens with size-percent 60 and width 400, then resizes height to 300
- **THEN** width is 400 DIP and height is 300 DIP; a subsequent resize --full restores the full content region

#### Scenario: Legacy bare resize
- **WHEN** resize has no sizing options at all
- **THEN** it retains the existing full-region reset behavior; partial retention applies when at least one sizing option is supplied

### Requirement: Effective geometry fits current content bounds
Floating geometry SHALL be centered, convert DIP using the window DPI, and clamp each effective axis to current content bounds. Requested dimensions SHALL remain unchanged across resizing and DPI changes. Pane overlays SHALL remain full-pane and refuse width/height/size-percent/full or resize, including when selected by overlay ID.

#### Scenario: Small window and DPI change
- **WHEN** requested DIP dimensions exceed the content box or the window DPI changes
- **THEN** layout, PTY grid, drawing, mouse coordinates and geometry readback use the same clamped rectangle; growing the window can restore the requested size

### Requirement: Launch options use the target profile
Open SHALL accept nonempty cwd, boolean follow and background-color in #RRGGBB form. Cwd SHALL use existing Windows/WSL path conversion and the target profile's distribution/user; omission SHALL retain owner cwd. Follow SHALL select the owner's session and pane only after successful open, without promising Windows foreground activation. Omitted follow SHALL preserve current selection.

#### Scenario: Background WSL launch
- **WHEN** an overlay opens on a background WSL pane with a Linux cwd containing spaces
- **THEN** the program runs in that directory and target distribution/user; selection changes only if follow was requested

### Requirement: Background color is local to the overlay
An explicit open background color SHALL provide the overlay's default terminal background without changing the underlying pane, theme or other overlays. Terminal explicit cell colors and existing dynamic background behavior SHALL retain their meaning. Invalid colors SHALL refuse before replacing or opening a program.

#### Scenario: Replacement with invalid options
- **WHEN** open specifies an invalid color or dimension while a session overlay exists
- **THEN** the old overlay remains running and unchanged, and the caller receives failure

### Requirement: Lifetime and result compatibility remain intact
Session-wide result SHALL remain window-wide; pane result SHALL remain slot-scoped. Block SHALL wait on its own opened terminal. Session replacement SHALL close the old identity; occupied pane open SHALL refuse. Owner/pane/window removal SHALL close hidden and visible programs and release their waiters. Overlays SHALL remain ephemeral across restart.

#### Scenario: Hidden owner removal
- **WHEN** a hidden overlay's owning pane or session is removed
- **THEN** its process is disposed, its block waiter is released and later overlay-ID requests refuse instead of targeting a replacement

### Requirement: Observation describes committed overlay controls
C1 overlay events SHALL additionally carry hidden, shown and resized actions with stable overlayId, sessionId, slot and ownerPaneId where applicable. Visibility events SHALL describe explicit hidden-state transitions; selection/occlusion remain observable through tree and existing selection events. Refused and unchanged operations SHALL not emit change events.

#### Scenario: Idempotent visibility request
- **WHEN** a visible overlay receives show twice, then hide
- **THEN** only the hidden transition adds a visibility event and no close/open lifetime is fabricated

### Requirement: CLI and raw requests validate before mutation
CLI and wire SHALL reject unknown actions, incompatible options, invalid typed values, ambiguous or missing targets and mismatched pane selectors without changing overlays. CLI usage SHALL exit 2 before sending; server refusal SHALL exit 1 through the ordinary envelope. Existing open/text/copy/result/close replies SHALL retain their shapes.

#### Scenario: Invalid raw request
- **WHEN** a raw client submits a noninteger width or launch-only option on hide
- **THEN** the server refuses without changing visibility, geometry or process lifetime
