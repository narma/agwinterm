# Spec Delta

## Purpose

Provide responsive terminal presentation and input with one authoritative session state, whether the session runs locally or survives independently of its window.

## ADDED Requirements

### Requirement: Hosted and serverless sessions retain equivalent terminal behavior
The system SHALL support both persistent hosted sessions and serverless sessions. Serverless operation SHALL NOT launch a session host. Both modes SHALL preserve terminal text, graphics, history, input, search, selection, accessibility and control readback semantics.

#### Scenario: Serverless default-terminal handoff
- **WHEN** a pseudoconsole is handed to an in-process pane
- **THEN** the pane works without a session-host process or session transport and supports the same terminal presentation operations

#### Scenario: Hosted UI disappears
- **WHEN** a hosted pane's UI exits or crashes
- **THEN** its child continues, terminal queries receive responses and a later attachment observes output produced while disconnected

### Requirement: Session state has one authoritative owner
New sessions using the negotiated presentation transport SHALL maintain one live terminal state per session. The UI SHALL retain only bounded presentation data for hosted sessions, without parsing their output or retaining a full terminal history replica. Diagnostic evidence SHALL distinguish live owners from bounded read-only captures and legacy attachments.

#### Scenario: Many hidden sessions
- **WHEN** hosted panes become hidden while producing output
- **THEN** their histories remain authoritative in the host, UI presentation storage stays bounded and no second live terminal is created

### Requirement: Presentation updates remain coherent under pressure
Presentation SHALL identify the session incarnation, attachment and frame generations. Missing or superseded updates SHALL trigger bounded resynchronization, never mixed frames. A slow UI SHALL NOT block terminal ingestion through presentation writes. Skipping obsolete visual updates SHALL preserve authoritative terminal state and final output.

#### Scenario: Resize overtakes a pending frame
- **WHEN** a new viewport size is accepted while an old-size frame is queued
- **THEN** the UI rejects obsolete deltas and displays a coherent frame for the new viewport

#### Scenario: UI stops reading
- **WHEN** a UI receiver stalls during sustained child output
- **THEN** presentation queues remain bounded, the terminal continues processing output and reattachment recovers current state

### Requirement: Input preserves ordering without waiting for presentation
Input SHALL preserve accepted key, text, paste, mouse and focus ordering within an attachment and use the owner's current terminal modes. Sending input SHALL NOT wait for a frame acknowledgement, search completion or encoding round trip. Accepted input SHALL NOT be silently dropped, duplicated or replayed after an uncertain disconnect.

#### Scenario: Typing during output and search
- **WHEN** a user types while sustained output and history search are active
- **THEN** ordered input reaches the child independently of presentation/query backlogs and produces correctly encoded output

#### Scenario: Queue or connection failure
- **WHEN** input cannot be accepted or transport delivery becomes uncertain
- **THEN** the caller receives explicit failure or uncertainty and reconnect does not resend possibly delivered input

### Requirement: Terminal effects and reads preserve ownership
Terminal responses SHALL originate once from the owner. UI effects SHALL preserve existing permission and focus policies without duplicate execution after reconnect. History, selection, search, accessibility and control reads SHALL use consistent versioned state, with cancellation or explicit failure for unavailable results.

#### Scenario: Query and clipboard request while connected
- **WHEN** the child requests terminal capabilities and an allowed clipboard write
- **THEN** it receives one terminal reply and the connected UI performs the clipboard action once

#### Scenario: Read crosses reflow
- **WHEN** a selection or history query races with resize
- **THEN** the result uses a consistent captured generation or is explicitly invalidated, never assembled from incompatible coordinates

### Requirement: Upgrade migrates saved state without legacy runtime backends
The system SHALL ship only the Ghostty terminal backend, supporting hosted and serverless modes. Installation SHALL save and back up old state before closing old sessions and replacing binaries. The new version SHALL import supported settings, layout and attributed history without old-host attachment. Saved-state restore SHALL NOT claim to resume process execution.

#### Scenario: Upgrade from installed 0.20.19
- **WHEN** installation finds the previous UI and hosted sessions running
- **THEN** it saves state, preserves a backup, closes old sessions and restores supported saved state without adopting old hosts

#### Scenario: Saving or stopping the old installation fails
- **WHEN** state persistence or old-process shutdown cannot be verified
- **THEN** installation does not overwrite the running version and preserves recoverable state

#### Scenario: New-version UI reconnects
- **WHEN** a UI of the new version reconnects to its compatible surviving host
- **THEN** it retains session incarnation and process identity without replaying saved commands

### Requirement: Backend removal preserves WSL and remote functionality
Removing legacy Windows terminal backends SHALL preserve the bundled Rust Linux helper, WSL, ZMX, remote sessions and their recovery behavior.

#### Scenario: Existing WSL or remote workflow after upgrade
- **WHEN** the user opens or restores a supported WSL/ZMX or remote session
- **THEN** its helper, connection and recovery functionality remains available with the Ghostty backend

### Requirement: Performance acceptance covers both operating modes
Release acceptance SHALL compare hosted and serverless modes separately against their pinned baselines using repeatable input-to-echo, input-to-present, throughput, CPU and memory measurements. Results SHALL include tail latency and instrumentation limits. A reproducible regression beyond the predeclared measurement tolerances SHALL block rollout; latency improvement SHALL require evidence rather than inference from fewer terminal instances.

#### Scenario: Memory trade-off
- **WHEN** comparing a candidate against its pinned baseline
- **THEN** report memory overhead and prioritize latency, CPU and throughput over modest bounded memory growth
- **AND** block rollout on leaks or unbounded memory growth. Growth above 5% alone does not fail acceptance.

#### Scenario: Faster parsing but slower visible echo
- **WHEN** terminal CPU falls but measured input-to-present tail latency exceeds the accepted baseline tolerance
- **THEN** the candidate fails performance acceptance until the presentation path is corrected

#### Scenario: Improvement claimed
- **WHEN** the change is described as improving input latency
- **THEN** repeated paired measurements support that claim and identify the workloads and timing endpoint measured
