## Purpose

Allow an agterm viewer to discover and use Agwinterm-owned WSL terminals without changing workload ownership or exposing Windows transport internals.

## ADDED Requirements

### Requirement: Discovery is scoped to an explicitly provisioned origin
The adapter SHALL return the agterm control response envelope and Linux executable/socket paths for one explicitly selected distribution, user, origin and socket namespace. Discovery SHALL be bounded and read-only. It SHALL NOT install SSH services, enroll host keys, create workloads or expose the Rust host pipe.

#### Scenario: Different endpoint
- **WHEN** another user, distribution, origin or socket namespace contains a similarly named daemon
- **THEN** its panes are excluded from the selected origin tree

### Requirement: Exported sessions have complete verified topology
An exported session SHALL contain stable window/workspace/session IDs and exactly one left pane or left/right panes with the supported split axis. Every pane SHALL have a valid owned local WSL binding and independently verified matching daemon incarnation. Pending, missing, borrowed, closing, ordinary and partially eligible sessions SHALL be excluded.

#### Scenario: One side of a split is unavailable
- **WHEN** the right pane is missing or belongs to another endpoint
- **THEN** the entire session is omitted rather than advertised as a single pane

#### Scenario: Pane swap
- **WHEN** the origin swaps panes
- **THEN** exported roles follow current layout while daemon identities remain with their panes

### Requirement: Attachment is existing only
The endpoint executable SHALL accept the agterm attachment invocation and connect only to the published workload incarnation. It SHALL bind identity verification and attachment to the same socket. Missing, revoked or replaced daemons SHALL fail without executing the trailing create command or substituting a shell.

#### Scenario: Name is reused
- **WHEN** the published daemon name now denotes another process or socket
- **THEN** attachment refuses the replacement and creates nothing

### Requirement: Viewer controls preserve origin ownership
Viewer disconnect and close SHALL preserve the origin workload. Leadership SHALL be explicit and scoped to a fresh attachment nonce; follower resize SHALL not resize the workload. The adapter SHALL restrict controls to supported exact exported targets and refuse lifecycle mutation and presentation requests.

#### Scenario: Viewer closes
- **WHEN** agterm closes a remote pane
- **THEN** the WSL workload and other attachments continue until explicit origin-owned termination

#### Scenario: Different viewer sizes
- **WHEN** a follower resizes and later explicitly claims leadership
- **THEN** only the explicit claim changes the authoritative workload geometry

### Requirement: UI loss is explicit and preserves terminals
When the Windows UI is unavailable, fresh discovery and metadata/control SHALL fail visibly rather than serving stale topology. Existing terminal connections SHALL survive UI loss. Previously issued attachment endpoints SHALL revalidate their immutable exact workload identity and refuse lost workloads without replay or fresh start.

#### Scenario: Origin UI restarts
- **WHEN** Agwinterm restarts while a viewer is attached
- **THEN** the workload survives, and fresh discovery resumes only from current verified origin state

### Requirement: Compatibility requires real peer acceptance
Completion SHALL require a pinned actual agterm client discovering and attaching single and split WSL workloads, leadership, disconnect, both application restarts, endpoint/user isolation and retained stage-2 regressions. Fixture tests SHALL NOT substitute for unavailable real-peer evidence.

#### Scenario: Peer cannot authenticate
- **WHEN** fixtures pass but the actual peer cannot connect
- **THEN** real-peer acceptance remains pending and stage 3 is not reported complete
