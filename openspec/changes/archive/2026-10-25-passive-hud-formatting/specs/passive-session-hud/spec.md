# Spec Delta

## Purpose

Present bounded native status panels with predictable lifetime, pane-aware placement and readable formatting while preserving terminal input and shared overlay ownership.

## ADDED Requirements

### Requirement: Existing plain HUD behavior remains available
Plain open/update SHALL retain nonblank NFC-normalized message and optional detail limits of 256 Unicode scalars each, refusing control characters. Existing position aliases, spinner styles, colors, session-target routing and idempotent close SHALL remain supported. Update SHALL replace omitted options with defaults while preserving the original background color.

#### Scenario: Legacy update
- **WHEN** a plain HUD is updated with only a message
- **THEN** detail, spinner, sizing, position and new optional presentation settings reset to defaults while its original background remains

### Requirement: Automatic expiry uses elapsed lifetime
Open/update SHALL accept finite hide-after seconds in 0..86400; omission/zero SHALL mean untimed. A successful open/update SHALL start a new monotonic lifetime, including while unselected, obscured or minimized. Expiry SHALL close only that current HUD without selecting its owner. Replacement, manual close and teardown SHALL end the old lifetime.

#### Scenario: Old deadline cannot remove a replacement
- **WHEN** a timed HUD is replaced before its deadline by an untimed HUD or program overlay
- **THEN** the old deadline cannot close the replacement; a timed background HUD still expires without changing selection

### Requirement: Sticky and frameless style have bounded geometry
Sticky SHALL remove edge margins for noncenter positions and allow requested width up to 100 percent there. Other widths SHALL retain the 10..80 percent clamp and height SHALL remain at most 80 percent of its anchor region. Frame false SHALL omit the border and reduce vertical padding while keeping an opaque background. Omitted sticky/frame SHALL be false/true.

#### Scenario: Edge caption
- **WHEN** a HUD opens sticky, frameless, top-center and size-percent 100
- **THEN** it spans its anchor's width flush to the top, keeps an opaque backing and does not claim terminal input

### Requirement: Explicit font size is native and local
Open/update SHALL accept a finite font-size in 6..72 DIP. Omission SHALL use the existing native HUD font size. Updating font size SHALL take effect in place and all measurement, wrapping and drawing SHALL use the same effective size. Terminal fonts and dimensions SHALL be unchanged.

#### Scenario: Update a readable caption
- **WHEN** a HUD changes from font-size 12 to 24
- **THEN** its native layout and geometry readback reflect the larger text without resizing its terminal

### Requirement: Pane anchoring pins a stable owner
Open/update SHALL accept pane as an exact main/split pane ID or left/right/focused selector within the resolved session. Resolution SHALL pin that exact pane ID; unknown or mismatched selectors and scratch/program/quick IDs SHALL refuse. Omission SHALL anchor to the session. A swap SHALL move the HUD with its pane; removal of that pane SHALL close the HUD.

#### Scenario: Pane moves then closes
- **WHEN** a HUD anchored to the right pane survives a swap and that pane is later removed
- **THEN** it first follows the same pane ID on the left, then closes with owner-removed instead of jumping to another pane

### Requirement: Markdown is explicit and bounded
Markdown true SHALL permit a multiline NFC message up to 4096 Unicode scalars and 128 lines, allowing newline/CR/tab but refusing other controls. Detail SHALL retain plain-text validation. Supported formatting SHALL include ATX headings, bold/italic, inline/fenced code, flat lists, quotes, rules and pipe tables bounded to 8 columns and 32 rows. Unsupported syntax SHALL remain literal; malformed markup SHALL not execute or create interactive content.

#### Scenario: Bounded table and code
- **WHEN** a Markdown message contains a small pipe table, a code fence and Unicode text
- **THEN** native rendering distinguishes them within bounded layout work; excessive table dimensions refuse before replacing a current HUD

### Requirement: Message files are caller-side input
CLI open/update SHALL accept --file PATH instead of a positional message, read at most 64 KiB of strict UTF-8 and apply the same mode-specific text validation before connecting. Missing/unreadable/invalid files or simultaneous file/message SHALL exit 2 without mutation. Windows CLI file paths from WSL SHALL follow the documented explicit wslpath conversion policy; no server-side file read or temporary message file SHALL occur.

#### Scenario: Linux file with spaces
- **WHEN** a WSL caller passes the Windows-converted path of a UTF-8 Markdown file containing spaces
- **THEN** CLI sends its content as message and the native HUD renders it without launching a renderer or changing the source file

### Requirement: Readback describes effective presentation
Existing HUD readback fields SHALL remain, with additive hideAfter, sticky, frame, markdown, requested fontSize, effectiveFontSize, anchorPaneId, visible and geometry. Geometry SHALL be client-pixel x/y/width/height from the same layout as drawing. Visible SHALL account for selected session, pane presence and obscuring covers/dashboard. Background readback SHALL not select a session to measure it.

#### Scenario: Obscured anchored HUD
- **WHEN** a scratch cover obscures the selected session or another session becomes selected
- **THEN** the HUD remains represented with visible false and its stable anchor identity, until expiry/close/removal

### Requirement: HUD remains passive and ephemeral
HUD SHALL not capture keyboard, mouse, wheel, selection or file drops, create a terminal surface, alter terminal history/grid or affect notifications. Session program overlays SHALL retain the shared-slot replacement/refusal rules, including hidden overlays. Pane overlays may coexist. HUD state SHALL not persist across restart. HTML, scripts and clickable links SHALL not be supported.

#### Scenario: Styled HUD over a terminal
- **WHEN** a user types, selects text or scrolls through a formatted HUD
- **THEN** input follows the existing terminal path and no HUD action intercepts it

### Requirement: New options validate before mutation
CLI and wire SHALL reject unknown/duplicate display options, wrong types, nonfinite/out-of-range numbers and incompatible close options before modifying state. CLI errors SHALL exit 2 before transport; host refusal SHALL use the existing error envelope and exit 1. A refused update SHALL preserve message, anchor, style and lifetime.

#### Scenario: Invalid update preserves timer
- **WHEN** a live timed HUD receives an invalid font size or pane selector
- **THEN** it remains unchanged and its original deadline remains in force
