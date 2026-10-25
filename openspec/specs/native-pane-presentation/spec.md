# native-pane-presentation Specification

## Purpose

Allow persistent native pane presentation without changing terminal identity, status semantics or the existing system-theme behavior.

## Requirements

### Requirement: Pane presentation follows stable identity
Primary/split panes SHALL support optional solid fallback background, bounded text watermark and status color/shape overrides. Overrides SHALL belong to pane IDs, survive swaps, app restore and recent recovery, and never leak to siblings. Invalid or ambiguous targets SHALL refuse before mutation.

#### Scenario: Swap and recover a decorated pane
- **WHEN** a decorated split pane swaps sides and is later restored
- **THEN** its presentation follows the same pane ID while the sibling retains its own appearance

### Requirement: Appearance has explicit inheritance and clearing
Unset pane values SHALL inherit the theme/session presentation. Clear SHALL remove all pane overrides. A pane SHALL be able to suppress the inherited session image explicitly. Terminal explicit cell colors and OSC dynamic background SHALL retain precedence over the solid fallback. Presentation readback SHALL expose overrides and effective fallback values.

#### Scenario: Clear a local background
- **WHEN** a pane background override is cleared
- **THEN** it returns to the current theme and session image without changing sibling panes or terminal text

### Requirement: Native text watermarks remain passive and bounded
Text watermarks SHALL render natively behind terminal text, clipped to the pane, with bounded Unicode text and opacity. They SHALL not introduce HTML, links, input handling or terminal buffer content. Unsupported values SHALL refuse without partial application.

#### Scenario: Multiline watermark
- **WHEN** a Unicode multiline watermark is applied to a narrow pane
- **THEN** rendering stays clipped and terminal input, dimensions and scrollback remain unchanged

### Requirement: Status styling does not change status meaning
Status color and circle/square/diamond shape overrides SHALL affect presentation only. Aggregated sidebar indicators SHALL retain status priority and use the appearance of a pane contributing the selected status. Notification categories, wait results and status transitions SHALL remain unchanged.

#### Scenario: Style a blocked pane
- **WHEN** a blocked pane receives a new status shape and color
- **THEN** it remains blocked with the same notification and wait semantics

### Requirement: Optional system themes preserve manual fallback
Separate optional light/dark theme choices SHALL follow Windows appearance when enabled and update terminal and chrome together. With following disabled, or the selected side unset, the configured single theme SHALL apply. Existing configuration keys and defaults SHALL remain compatible.

#### Scenario: Unconfigured automatic side
- **WHEN** Windows selects a side with no configured theme
- **THEN** the single configured theme supplies both terminal and chrome appearance
