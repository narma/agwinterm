# control-cli-contract Specification

## Purpose

Let scripts distinguish refused control requests from success and reject ambiguous CLI input before it can affect a terminal.

## Requirements

### Requirement: Ordinary request exit codes reflect the reply
The CLI SHALL exit 0 for a valid successful ordinary reply and 1 for a refused reply, transport failure or invalid reply envelope. JSON output SHALL NOT change that decision. Local usage failures SHALL exit 2.

#### Scenario: Refused JSON request
- **WHEN** an ordinary request receives `{"ok":false,"error":"session not found"}` with `--json`
- **THEN** stdout contains the raw JSON reply and the process exits 1

#### Scenario: Successful JSON request
- **WHEN** an ordinary request receives a valid `ok:true` reply with `--json`
- **THEN** stdout retains the raw JSON reply and the process exits 0

#### Scenario: Invalid reply
- **WHEN** a request receives malformed JSON or an envelope without a boolean `ok`
- **THEN** the process exits 1 and reports a protocol diagnostic on stderr rather than claiming success

### Requirement: Documented special outcomes remain distinct
The CLI SHALL preserve offline version diagnostics, picker outcome codes, UTF-8 redirected output, local help behavior and documented timeout uncertainty. A failure code SHALL NOT claim a timed-out mutation was rolled back.

#### Scenario: Offline version
- **WHEN** version is requested with no server listening
- **THEN** the CLI still reports its own version and exits 0 as documented

#### Scenario: Picker cancelled
- **WHEN** a blocking picker finishes cancelled
- **THEN** the picker retains its documented payload and exit code 2

#### Scenario: Destructive command help
- **WHEN** `session close --help` is invoked
- **THEN** help is printed locally without sending a close request

### Requirement: Unsupported options fail before dispatch
Each CLI operation SHALL validate its accepted options and value shapes before local installation or control dispatch. Unknown options, missing required values, invalid numeric values and unsupported selector combinations SHALL produce exit 2 with a diagnostic and no side effect. Documented accepted aliases SHALL remain accepted.

#### Scenario: Unsupported pane selector
- **WHEN** `session type hello --pane right` is invoked although that operation targets panes through `--target`
- **THEN** the CLI exits 2 without sending input and identifies the unsupported option

#### Scenario: Invalid resize number
- **WHEN** `session resize --grow-left abc` is invoked
- **THEN** the CLI exits 2 without sending a resize request

#### Scenario: Omitted value
- **WHEN** a command is given a bare `--window` or `--target` that requires a value
- **THEN** it fails before dispatch instead of selecting a fallback target

### Requirement: Single-value selectors cannot be overwritten silently
The CLI SHALL reject repeated target, window or transport selectors before dispatch, even if the values are equal. Alternative transport spellings SHALL NOT permit conflicting or duplicate selection. Repeated single-value selectors SHALL NOT imply batch execution; batches SHALL require the distinct explicit batch argument.

#### Scenario: Repeated target
- **WHEN** `session close --target A --target B` is invoked
- **THEN** the CLI exits 2 and neither session is closed

#### Scenario: Conflicting transport aliases
- **WHEN** both `--pipe` and `--socket` select a transport in one call
- **THEN** the CLI exits 2 without connecting to either endpoint

### Requirement: Valid existing input remains usable
Validation SHALL preserve documented positional text, stdin handling, boolean flags, command modes and accepted aliases. It SHALL distinguish flags from valued options so a flag does not consume the following positional text. Existing command-specific conflicts SHALL continue to refuse.

#### Scenario: Flag followed by text
- **WHEN** a supported boolean flag precedes valid positional text for its operation
- **THEN** the text remains positional and is neither swallowed nor substituted for a selector

#### Scenario: Stdin conflict
- **WHEN** `session type --stdin` is combined with positional text
- **THEN** the existing ambiguity refusal is preserved and nothing is typed

#### Scenario: Existing launch modes
- **WHEN** valid PowerShell or direct session launch options are supplied
- **THEN** validation retains their documented Windows launch semantics and direct-mode wait refusal
