## Purpose

Provide low-latency terminal ownership beside the UI while a parser-free broker retains processes and bounded recovery evidence independently of window lifetime.

## ADDED Requirements

### Requirement: One UI-side terminal owner
Experimental journal sessions SHALL have one live Ghostty in the UI owner process and no terminal parser in the PTY broker. Other backends SHALL retain their existing behavior.

#### Scenario: Interactive journal session
- **WHEN** a journal session exchanges input and output
- **THEN** the UI owner performs terminal parsing and input encoding, and broker counters report no native terminal

### Requirement: Bounded ordered journal
The broker SHALL order output and resize events, retain their exact sequence, and apply backpressure at its retention limit rather than discard uncovered events. Normal input SHALL NOT wait for checkpoint persistence or a UI acknowledgement round trip.

#### Scenario: Slow or absent owner
- **WHEN** retained data reaches the configured limit
- **THEN** memory remains bounded and uncovered output remains recoverable

### Requirement: Checkpoint-consistent recovery
Checkpoints SHALL identify the exact processed journal prefix and session incarnation. The broker SHALL trim that prefix only after successfully storing its checkpoint. Recovery SHALL restore the checkpoint and apply the subsequent events in order without replaying user input or duplicating external effects.

#### Scenario: Failed checkpoint publication
- **WHEN** checkpoint persistence fails or its identity is stale
- **THEN** the prior checkpoint and uncovered journal remain available

#### Scenario: Owner crash
- **WHEN** the owner crashes while the broker survives
- **THEN** the shell survives and restoration may pause terminal replies, without resending previously accepted user input

### Requirement: Window-independent owner lifetime
Closing the last experimental owner window SHALL retain terminal parsing and replies without ongoing rendering. Reopening SHALL reuse the running owner. Explicit session termination SHALL still release its PTY and terminal resources.

#### Scenario: Background terminal query
- **WHEN** a child requests terminal state after all windows close
- **THEN** the surviving Ghostty owner answers using current state

### Requirement: Measured architecture comparison
The prototype SHALL remain opt-in until paired measurements compare legacy, host-owner and UI-owner paths. Measurements SHALL cover input/output latency, fixed-work CPU, throughput, memory and checkpoint interference; inconclusive gates SHALL remain explicitly inconclusive.

#### Scenario: Checkpoint during output
- **WHEN** a checkpoint is captured while the child produces output and accepts input
- **THEN** the report includes input tail latency and checkpoint capture duration without omitting the affected samples
