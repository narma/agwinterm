# Spec Delta

## Purpose

Expose effective runtime settings and executable provenance so scripts can diagnose configuration and version differences without guessing or mutating unrelated state.

## ADDED Requirements

### Requirement: Keymap readback describes effective bindings
Keymap readback SHALL return effective normal bindings, leader chord/bindings, reserved key behavior and parser diagnostics. Default bindings and user overrides/unmaps SHALL be reflected. Custom command listing SHALL remain distinct and unchanged.

#### Scenario: Overridden default
- **WHEN** a user overrides one default chord and unmaps another then reloads the keymap
- **THEN** readback reports the overridden action and omits the unmapped binding while preserving leader metadata

### Requirement: Config reload shares native application and diagnostics
Config reload SHALL use the same live application path as native UI reload and config set. It SHALL report changed keys, effective theme and diagnostics for invalid, unknown or normalized entries rather than silently claiming every input was applied. Existing restart/new-session-only settings SHALL retain their documented lifecycle.

#### Scenario: Invalid edited setting
- **WHEN** a config file contains an invalid cursor value or unknown key
- **THEN** reload exposes the diagnostic and effective configuration through readback without treating the invalid text as an applied value

### Requirement: Version comparison distinguishes missing evidence
Extended version diagnostics SHALL report the running build, executable path, installation channel and available on-disk executable version. Comparison SHALL be match, different or unknown. Missing files, unavailable metadata and portable installations SHALL be represented honestly. The existing offline CLI version behavior SHALL remain available.

#### Scenario: No version resource
- **WHEN** the executable has no readable version metadata
- **THEN** diagnostics report unavailable metadata and unknown comparison, never a match

#### Scenario: Replaced executable
- **WHEN** the file at the running executable path reports a different version
- **THEN** diagnostics show both versions and different comparison without installing or restarting anything

### Requirement: Diagnostic operations use explicit bounded contracts
Diagnostic and presentation requests SHALL validate selectors, types, duplicate and unsupported fields. Read-only diagnostic requests SHALL not change focus, terminal contents or configuration. Reload SHALL not implicitly install, update or execute user commands.

#### Scenario: Unsupported diagnostic option
- **WHEN** an unknown argument is sent to keymap readback
- **THEN** it refuses rather than ignoring the option or executing a command
