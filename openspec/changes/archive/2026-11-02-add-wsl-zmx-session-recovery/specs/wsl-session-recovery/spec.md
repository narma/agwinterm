# Spec Delta

## Purpose

Restore WSL ZMX attachments without duplicating live workloads or replaying commands into running programs, while retaining existing native-session and closed-history behavior.

## ADDED Requirements

### Requirement: Durable bindings preserve the complete execution context
Live checkpoints SHALL persist validated endpoint identity, resolved distribution/user, daemon incarnation, ownership and pane binding alongside existing layout and restore metadata. Changes to the default WSL distribution, default user or profile SHALL not silently retarget a saved binding. Save failures SHALL be visible and SHALL not claim durable recovery.

#### Scenario: Defaults change between launches
- **WHEN** the default distribution or Linux user changes after a checkpoint
- **THEN** recovery uses the recorded endpoint or reports it unavailable without substituting the new default

#### Scenario: Checkpoint cannot be saved
- **WHEN** a new live binding cannot be written to durable storage
- **THEN** the running workload is preserved and diagnostics identify that automatic recovery is not durably recorded

### Requirement: Recovery prefers the existing Windows transport
Recovery SHALL first attempt to adopt the matching surviving Rust-hosted transport. If successful, it SHALL reuse that attachment without launching another WSL or ZMX client. Adoption SHALL validate the binding rather than treating an unrelated process with a reused pane ID as the same workload.

#### Scenario: UI restarts during an agent turn
- **WHEN** the UI restarts while its Rust-hosted WSL attachment survives
- **THEN** the same Linux process and attachment continue and no new shell, client or agent is launched

### Requirement: Lost transports reattach only verified live workloads
When the Windows transport is confirmed absent or unusable, recovery SHALL probe the saved Linux endpoint and reattach only its matching live incarnation. It SHALL distinguish missing daemon, unavailable endpoint, incompatible dependency and uncertain state. Reconnect SHALL be serialized per binding and cancellable by explicit detach or close.

#### Scenario: Windows host was lost
- **WHEN** the Rust host is lost but the saved ZMX daemon remains live
- **THEN** a new Rust-hosted WSL transport attaches to the same Linux process without recreating the workload

#### Scenario: Endpoint probe times out
- **WHEN** probing a distribution or daemon times out
- **THEN** the pane retains its recovery metadata and reports unavailable or uncertain state without creating or terminating a workload

#### Scenario: Close races with reconnect
- **WHEN** a reconnect completes after the user detached or closed that binding
- **THEN** the late attachment is discarded without resurrecting the pane or killing an unrelated incarnation

### Requirement: Live recovery suppresses every replay source
Adoption of an existing transport and attachment to a surviving ZMX daemon SHALL both suppress saved-buffer seeding, agent-resume invocation, pinned restore-command execution and captured-command replay. Their saved metadata SHALL remain available for explicit later recovery. Suppression SHALL not depend solely on whether the Windows transport was adopted.

#### Scenario: New transport reaches an existing agent
- **WHEN** a newly created Windows transport attaches to an already running agent with saved resume and restore commands
- **THEN** none of those commands is typed into the agent and the saved buffer is not prepended to the daemon's replay

### Requirement: Lost workloads require explicit relaunch
A missing saved daemon SHALL leave a recoverable pane with a visible lost-workload state and retry or explicit fresh-start action. The system SHALL NOT automatically rerun saved commands or claim live-process recovery after WSL shutdown or Windows reboot. Explicit relaunch SHALL establish a fresh incarnation and execute at most one selected launch source.

#### Scenario: Machine rebooted
- **WHEN** the app restores a binding after its Linux workload was lost on reboot
- **THEN** it restores layout and metadata but waits for explicit relaunch rather than reporting the former process alive

#### Scenario: Resume is explicitly selected
- **WHEN** the user explicitly relaunches a missing workload using its saved agent-resume binding
- **THEN** that source runs once in the recorded context and neither pinned nor captured commands also run

### Requirement: Ambiguous mutations cannot trigger duplicate work
Lost replies and timeouts during creation, attachment, termination or relaunch SHALL be reconciled against operation and incarnation evidence before further mutation. Unknown completion SHALL remain explicit. Automatic retries SHALL not create another workload, resend a resume command or terminate a replacement daemon.

#### Scenario: Creation reply is lost
- **WHEN** creation may have succeeded but its reply was lost
- **THEN** recovery queries the original operation and adopts proven results or reports uncertainty without submitting a second creation

### Requirement: Recovery preserves pane-local identity and terminal continuity
Live bindings SHALL follow stable pane IDs through swaps, split survivor promotion and window restoration. One pane's failure SHALL not block recovery of other panes. Screen/history recovery SHALL avoid duplicate replay and retain effective terminal modes, dimensions and observed leadership; stale callbacks SHALL not change a newer attachment.

#### Scenario: Swapped split recovers partially
- **WHEN** a swapped split contains one live daemon and one unavailable daemon
- **THEN** each binding stays with its original pane, the live pane resumes normally and the unavailable pane reports its own failure

### Requirement: Existing checkpoints and Recent retain their semantics
Checkpoints without ZMX metadata SHALL retain existing restore behavior. Recent reopening SHALL restore supported local state without adopting a historical live binding or replaying foreground commands. A reopened ZMX-enabled profile SHALL create a fresh workload identity. Clearing history or restore checkpoints SHALL not terminate live daemons.

#### Scenario: Reopen a closed live-mode session
- **WHEN** a closed-session history entry is reopened using its enabled WSL profile
- **THEN** it creates a fresh workload with restored supported settings and retained pins, without attaching the old daemon or executing those pins as part of Recent recovery

#### Scenario: Old checkpoint
- **WHEN** a checkpoint contains no live binding metadata
- **THEN** existing agent-resume, pinned-command and opt-in capture behavior remains unchanged

### Requirement: Disabling live mode never silently converts saved work
Disabling ZMX for new sessions SHALL not terminate or migrate existing live bindings. Saved live bindings SHALL remain recoverable or explicitly unavailable. Corrupt or unsupported binding metadata SHALL not be interpreted as an ordinary shell checkpoint. Downgrade limitations SHALL be documented without promising old versions understand new fields.

#### Scenario: Feature disabled after checkpoint
- **WHEN** the user disables live mode for new panes and restarts
- **THEN** existing bindings are recovered with their recorded semantics or shown unavailable, never replayed into replacement plain shells

### Requirement: Recovery outcomes distinguish survival from restart
UI and machine-readable diagnostics SHALL distinguish transport adoption, daemon reattachment, fresh explicit relaunch, missing workload, unsupported dependency and unavailable or uncertain recovery. They SHALL expose the selected host/provider and endpoint without describing saved scrollback or resumed agent conversation as a surviving process.

#### Scenario: Conversation resumed after loss
- **WHEN** an explicit relaunch resumes an agent conversation in a new process
- **THEN** diagnostics report a fresh relaunch with conversation resume rather than live adoption
