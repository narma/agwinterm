# Spec Delta

## Purpose

Collect bounded structured answers through owned Windows controls without executing caller text or interpreting terminal output.

## ADDED Requirements

### Requirement: Form input has an explicit bounded schema
`ask open` SHALL read one UTF-8 JSON form from stdin; wire `ask.open` SHALL accept it as args.form. Forms SHALL contain optional title, prompt, fields and buttons. Limits SHALL be 1 MiB encoded form, 16 fields, 64 choices per field, 512 choices total and 8 buttons. Unknown properties, duplicate properties/IDs, controls in display labels and invalid types SHALL refuse before creating UI.

#### Scenario: Invalid form leaves the owner available
- **WHEN** a form contains duplicate field IDs, an unknown field type or an oversized collection
- **THEN** CLI exits 2 without connecting and raw requests refuse before claiming the native dialog slot

### Requirement: Field values have stable types
Fields SHALL have nonempty id and label and type text, multiline, single or multi. Choice fields SHALL have nonempty choices of unique nonempty id/label pairs and optional boolean allowOther. Text answers SHALL be strings; choice answers SHALL be objects with selected IDs in input order and other text or null. Empty values SHALL be allowed. Display fields/IDs SHALL be bounded to 4096 UTF-16 units; multiline answers to 16384.

#### Scenario: Multiple choices and other text
- **WHEN** the user selects two choices and supplies other text in a multi field allowing it
- **THEN** its answer contains both exact IDs in original choice order and the entered other string; changing pages preserves all values

### Requirement: Buttons are data and logical character shortcuts
Buttons SHALL have unique nonempty id/label pairs and an optional unique single letter or digit hotkey, compared case-insensitively. Omission SHALL supply one submit button with ID submit. Alt plus that character SHALL submit using the same button ID under the active keyboard layout. A separate Cancel action SHALL always be available. Labels and answers SHALL never execute commands or interpret markup.

#### Scenario: Cyrillic button shortcut
- **WHEN** a button declares hotkey д and the user enters Alt+д with a Cyrillic layout
- **THEN** it produces the same answer as clicking that button; ordinary Cyrillic text entry does not activate buttons

### Requirement: Native navigation exposes names and state
Forms SHALL use accessible native text, multiline and choice controls with descriptive labels and selected state. Tab and Shift+Tab SHALL navigate controls; Back/Next SHALL navigate fields while retaining values. Multiline Enter SHALL insert a line break. Submit buttons SHALL be reachable from every field, and Escape SHALL cancel. Layout SHALL fit monitor work bounds and adapt to DPI.

#### Scenario: Small monitor with many fields
- **WHEN** a 16-field form is opened in a small work area
- **THEN** paged navigation makes every field and action reachable without expanding beyond the work area

### Requirement: Outcomes are exact and stable
Open SHALL return a generated request ID. `ask.result` and `ask.cancel` SHALL require its exact ID and optionally verify its owning window. Result SHALL be pending, answered with button and values, or cancelled with reason cancel, escape, closed or owner-closed. First completion SHALL win; repeated result reads and cancellation of completed requests SHALL not change it.

#### Scenario: Completion wins a cancellation race
- **WHEN** an answer is committed before cancellation of the same ID
- **THEN** result remains answered with identical button and values; another pending form is unaffected

### Requirement: Lifetime is bounded and owned
One ask or pick SHALL own a window's dialog input at a time and conflict with existing modal operations. Quick-terminal ownership SHALL refuse. Open SHALL preserve foreground policy unless follow requests activation. Owner close SHALL settle pending forms and retain results. Retention SHALL keep 8 completed asks per live window and 32 from closed windows in completion order; restart SHALL forget all asks.

#### Scenario: Owner closes while a caller waits
- **WHEN** the owning window closes while an ask is pending
- **THEN** exact-ID result reports cancelled with reason owner-closed while retained; no input is forwarded to a terminal or replacement form

### Requirement: CLI waiting follows the created identity
CLI SHALL support open with --window, --follow, --no-block, --pipe/--socket and --json; result/cancel SHALL accept exact ID, optional window/transport and json only. Blocking open SHALL poll its returned ID without a window selector or answer deadline. Answer/no-block/cancel SHALL exit 0, cancelled outcome 2, pending/refusal/transport failure 1, usage 2 and Ctrl+C 130. Payloads SHALL be JSON; ordinary wire replies SHALL retain envelopes.

#### Scenario: Selection changes while waiting
- **WHEN** the active window changes after blocking open
- **THEN** polling remains attached to the original ID and cannot read a later form in the newly active window

### Requirement: Transport loss has explicit uncertainty
Requests SHALL have bounded transport deadlines. After a known open ID, an interrupted or failed blocking wait SHALL attempt exact-ID cancellation once without masking the original failure. Failure before receiving the open ID SHALL not retry creation or cancel an unknown dialog. No-block SHALL leave the dialog pending after the caller exits.

#### Scenario: Open reply is lost
- **WHEN** a connection fails after the server creates the form but before the caller learns its ID
- **THEN** CLI reports failure and does not duplicate creation; documentation explains the possible remaining owned dialog
