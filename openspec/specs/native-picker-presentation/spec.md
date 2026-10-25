# native-picker-presentation Specification

## Purpose

Give native picker callers deliberate initial selection and accessible item status markers while retaining exact item identity and existing filtering behavior.

## Requirements

### Requirement: Initial selection uses an exact item ID
Pick open SHALL accept CLI --selected-id and wire selectedId. A supplied ID SHALL exactly match an input item, including case and empty ID support. The initial filtered list SHALL select that item when present; if the initial query excludes it, selection SHALL fall back to the first match. Later query edits SHALL retain existing selection-reset behavior.

#### Scenario: Duplicate labels with distinct IDs
- **WHEN** two items have the same label and selectedId names the second item
- **THEN** initial Enter chooses the second item's exact ID and original index without confusing labels

### Requirement: Item status metadata is bounded and validated
Items SHALL accept optional status display text, color in #RRGGBB form and shape dot|square|diamond. Status SHALL follow existing display-text limits; unknown shapes and invalid colors SHALL refuse before native creation. Omission SHALL preserve ordinary picker appearance. A supplied color without shape SHALL use dot; a shape without color SHALL use the normal foreground color.

#### Scenario: Invalid marker
- **WHEN** an item supplies an unsupported shape or malformed color
- **THEN** open refuses and no pending picker or native window is created

### Requirement: Status presentation preserves filtering and accessibility
Native rows SHALL visibly render the marker shape/color and expose status as part of their accessible row text. Filtering SHALL continue to search labels only; metadata SHALL not alter ranking, ID or returned original index. Keyboard selection and chosen-row text SHALL remain available without relying on color alone.

#### Scenario: Status is not a hidden search term
- **WHEN** query matches only an item's status or subtitle
- **THEN** that item is not included solely because of metadata, while its status remains readable when the row is shown

### Requirement: Existing picker lifetime remains compatible
Picker payload/exit shapes, exact-ID polling, cancellation, native focus policy, input bounds and completed-result retention SHALL remain as documented. Picker and ask SHALL mutually refuse overlapping modal ownership of the same window; neither SHALL replace the other's pending request.

#### Scenario: Ask owns the window
- **WHEN** a picker is opened in a window with a pending ask form
- **THEN** picker open refuses without changing the pending form or terminal input destination
