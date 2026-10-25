# Spec Delta

## Purpose

Provide explicit Windows window operations and directory Reveal with reliable targeting, effective geometry and predictable focus behavior.

## ADDED Requirements

### Requirement: Window navigation follows the open library
Window next/previous SHALL wrap in open library order relative to the addressed window and restore/select its destination. Fewer than two open windows SHALL refuse. Closed entries and quick windows SHALL not participate.

#### Scenario: Navigate past a closed entry
- **WHEN** next is requested with a closed library entry between two open windows
- **THEN** the other open window is selected and the closed entry is not reopened

### Requirement: Window presentation supports background work
Minimize, restore and minimized creation SHALL avoid raising or focusing the addressed window. New minimized windows SHALL start minimized without an intervening visible activation. Fullscreen SHALL support on/off/toggle using existing native fullscreen behavior. Ordinary creation and selection SHALL retain their existing focus behavior.

#### Scenario: Create background work
- **WHEN** a new window is created with minimized requested
- **THEN** it exists in the library, is minimized and does not replace the foreground library window

#### Scenario: Restore a background window
- **WHEN** a minimized background window is restored
- **THEN** it becomes visible without taking foreground focus

### Requirement: Explicit window selectors refuse ambiguity
New operations SHALL resolve exact IDs or unique ID prefixes, with omitted/active selecting the foreground library window. Unknown, ambiguous, closed or quick targets SHALL refuse before mutation. These operations SHALL not alter existing last-window close/delete rules.

#### Scenario: Unknown window
- **WHEN** minimize addresses a missing window ID
- **THEN** it refuses and the active window remains unchanged

### Requirement: Monitor placement returns effective geometry
Monitor enumeration SHALL expose device identity, primary status, physical bounds and work area. Explicit monitor movement SHALL place the window within that monitor's work area, preserving size where possible and bounding oversized windows. Replies SHALL report effective physical rectangle, monitor, DPI and minimized/maximized/fullscreen states. Unknown monitors SHALL refuse without moving.

#### Scenario: Negative-coordinate monitor
- **WHEN** an ordinary window moves to a monitor left of the primary display
- **THEN** its returned rectangle uses actual negative desktop coordinates and fits the selected work area

#### Scenario: Minimized placement
- **WHEN** monitor movement addresses a minimized window
- **THEN** its restore placement changes within the selected work area without making it visible or active

### Requirement: Reveal uses the addressed directory
Session Reveal SHALL resolve the addressed primary/split pane within the selected window and use its current directory and profile. Windows directories SHALL use Explorer selection; WSL directories SHALL convert through the target distribution/user. Missing targets, unresolved or nonexistent directories and conversion failures SHALL return clear errors without opening an unrelated path.

#### Scenario: Background WSL pane
- **WHEN** Reveal targets a WSL pane while a Windows session is selected
- **THEN** the revealed directory is converted using the WSL pane's profile without first selecting that session

### Requirement: New operations have strict control contracts
CLI and raw requests SHALL validate option types, operation names, selector conflicts and unsupported arguments before mutation. Accepted native operations SHALL return effective results rather than merely echo requested geometry. Existing unextended commands SHALL retain their output contracts.

#### Scenario: Malformed fullscreen operation
- **WHEN** fullscreen receives an unknown operation or non-string value
- **THEN** it refuses without toggling the window
