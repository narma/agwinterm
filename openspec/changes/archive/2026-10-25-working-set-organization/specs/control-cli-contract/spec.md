# Spec Delta

## MODIFIED Requirements

### Requirement: Single-value selectors cannot be overwritten silently
The CLI SHALL reject repeated target, window or transport selectors before dispatch, even if the values are equal. Alternative transport spellings SHALL NOT permit conflicting or duplicate selection. Repeated single-value selectors SHALL NOT imply batch execution; batches SHALL require the distinct explicit batch argument.

#### Scenario: Repeated target
- **WHEN** `session close --target A --target B` is invoked
- **THEN** the CLI exits 2 and neither session is closed

#### Scenario: Conflicting transport aliases
- **WHEN** both `--pipe` and `--socket` select a transport in one call
- **THEN** the CLI exits 2 without connecting to either endpoint
