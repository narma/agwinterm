# agterm-remote-sessions Specification

## Purpose

Let Agwinterm view and control existing agterm-origin terminal workloads over WSL SSH while preserving origin ownership, exact targeting and recovery without duplicate work.

## Requirements

### Requirement: Remote access uses explicit trusted configuration
Remote access SHALL use an explicitly selected WSL distribution/user and SSH destination. Rust SHALL remain the Windows PTY host. Missing authentication, host trust, dependencies or required capabilities SHALL fail visibly without modifying SSH configuration, installing software or falling back to another host or shell.

#### Scenario: Unknown host key
- **WHEN** discovery cannot authenticate the configured destination under its existing host-key policy
- **THEN** it fails within a bounded time and does not accept a key or open a password prompt automatically

### Requirement: Discovery is bounded and read only
Discovery SHALL decode a successful origin `agtermctl zmx tree --json` response and preserve endpoint, window, workspace, session and per-pane daemon identities. It SHALL distinguish an empty inventory from failed, malformed, oversized or incompatible output. Discovery SHALL neither start workloads nor focus or create local panes.

#### Scenario: Command fails with an apparent success document
- **WHEN** the SSH command exits nonzero even though stdout contains a success-shaped document
- **THEN** discovery reports failure and does not offer its sessions for attachment

#### Scenario: Unsupported page companion
- **WHEN** the origin lists a page companion with no terminal panes
- **THEN** it is identified as unsupported for terminal attachment without hiding eligible terminal sessions

### Requirement: Remote attachment is existing only
Attachment SHALL revalidate the selected origin session and bind its exact daemon incarnation through a connection that cannot redirect to a replacement. It SHALL never create a daemon, shell or workload on a miss. A peer unable to provide that guarantee SHALL be reported unsupported before attachment.

#### Scenario: Daemon disappears after discovery
- **WHEN** the selected daemon disappears before attach commits
- **THEN** the operation reports missing or uncertain and neither a replacement workload nor a create-only error process is launched

#### Scenario: Name is reused
- **WHEN** a daemon name resolves to a different incarnation after disconnection
- **THEN** recovery refuses to attach that replacement under the saved binding

### Requirement: Imported panes retain origin and local identity separately
An explicit attach SHALL import one eligible terminal session and its supported split layout into the selected local window. Local IDs SHALL be distinct from origin IDs. Invalid or ambiguous pane roles and targets SHALL refuse before import. A partially failed attachment SHALL report each pane's outcome without substituting another session.

#### Scenario: Origin panes arrive in reverse order
- **WHEN** a valid split lists right before left
- **THEN** import assigns panes by role and preserves the origin split axis

#### Scenario: Selected local window closes during discovery
- **WHEN** the window chosen for import no longer exists when discovery completes
- **THEN** attachment fails without creating a session in the active window

### Requirement: Remote viewers never gain workload ownership
Every remote binding SHALL be borrowed. Close, detach, app exit, failed import and orphan cleanup SHALL disconnect only local transports. Remote termination and fresh-start actions SHALL refuse; origin workload ownership SHALL never be inferred from names or local history.

#### Scenario: Viewer closes a shared split
- **WHEN** the user closes one imported pane while other clients remain connected
- **THEN** only that viewer attachment ends and the origin workload and other clients continue

### Requirement: Leadership is explicit and attachment scoped
Remote panes SHALL expose leader, follower, unowned or unknown role. Only explicit leadership actions SHALL claim the workload grid. Discovery, background resizing, automated input and reconnect SHALL not take leadership. Role notices SHALL be accepted only for the current attachment generation.

#### Scenario: Follower reconnects at another size
- **WHEN** a follower reconnects with a different local grid while the origin client leads
- **THEN** the origin workload retains the leader's dimensions until an explicit leadership claim

### Requirement: Automation reaches the authoritative remote workload
Remote terminal text and cursor reads SHALL query authoritative origin state or fail explicitly. Automated input SHALL target the exact bound workload, honor readonly and control-byte rules, and distinguish accepted delivery from uncertain delivery. Acceptance SHALL not be reported as program completion; ambiguous input SHALL never be retried automatically.

#### Scenario: Input acknowledgement is lost
- **WHEN** an input send may have reached the workload but its response is lost
- **THEN** the caller receives uncertainty and the input is not sent again by reconnect or retry

### Requirement: Recovery preserves bindings without command replay
Recovery SHALL prefer an exactly matching surviving Rust transport, otherwise reconnect only a verified saved origin incarnation. Saved bindings SHALL retain resolved WSL and origin selectors despite changed defaults. Buffers, agent resume, pinned commands and captured commands SHALL not be replayed. Missing workloads SHALL remain visible without remote relaunch.

#### Scenario: UI restarts with a surviving attachment
- **WHEN** the matching Rust-hosted SSH attachment survives a UI restart
- **THEN** it is adopted without a second remote client or replayed commands

#### Scenario: Origin becomes unavailable
- **WHEN** reconnection cannot verify the saved origin
- **THEN** the pane retains its metadata and reports unavailable or uncertain without opening a replacement shell

### Requirement: Reconnect and local cleanup cannot resurrect retired panes
Reconnect SHALL be serialized and bounded per binding, use backoff, and discard stale callbacks after close, detach or replacement. Closing a borrowed pane SHALL not turn its Recent entry into a launch command. Clearing history or restore metadata SHALL not alter origin workloads.

#### Scenario: Attach completes after detach
- **WHEN** a remote attachment completes after its local pane was detached
- **THEN** the late transport is discarded without recreating the pane or terminating the origin workload

#### Scenario: Recent reopens a remote entry
- **WHEN** the user reopens a closed remote entry
- **THEN** it performs an explicit existing-only reattachment or shows a recoverable failure, without executing saved commands or granting ownership

### Requirement: Capabilities and verification are scoped honestly
Diagnostics SHALL identify the WSL SSH transport, origin endpoint, binding, ownership and recovery outcome. Outbound terminal support SHALL be distinct from inbound export and remote presentation. Compatibility claims SHALL name tested peer builds and limits; real-peer acceptance SHALL not be replaced by fixtures.

#### Scenario: Real agterm peer is unavailable
- **WHEN** fixture checks pass but no real agterm origin has completed the acceptance matrix
- **THEN** interoperability acceptance remains pending and is not reported complete
