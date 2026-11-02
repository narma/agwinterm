# wsl-live-sessions Specification

## Purpose

Provide opt-in persistent Linux terminal sessions inside WSL while retaining the Rust PTY host, with explicit ownership and controls that can support future agterm interoperability.

## Requirements

### Requirement: Live mode is WSL scoped and opt in
ZMX live mode SHALL be explicitly enabled per WSL profile for ordinary session panes. It SHALL preserve the chosen distribution, Linux user, shell, cwd and creation command. Native Windows profiles and transient terminals SHALL retain their existing behavior. Existing ordinary WSL panes SHALL not be silently migrated into ZMX.

#### Scenario: Enable a WSL profile
- **WHEN** a user enables live mode and creates a new ordinary pane from that profile
- **THEN** its workload runs in that distribution under that Linux user through ZMX, and existing panes continue unchanged

#### Scenario: Unsupported profile or surface
- **WHEN** ZMX is explicitly requested for a Windows profile, quick terminal, scratch terminal or program overlay
- **THEN** the request is refused before spawning; automatic profile inheritance does not enable ZMX for transient surfaces

### Requirement: Rust hosting remains effective
ZMX panes SHALL retain the configured Rust PTY host for their Windows transport. Diagnostics SHALL distinguish the Windows host backend from the Linux session provider. Unavailable Rust hosting SHALL leave a requested ZMX pane unavailable rather than silently starting it in-process or replacing the host.

#### Scenario: Host cannot start
- **WHEN** a live pane configured with `server-rust` cannot reach or start its Rust host
- **THEN** it reports the host failure without launching a fallback shell or consuming recovery commands

### Requirement: Dependency readiness is explicit
The system SHALL validate the configured Linux ZMX executable and required capabilities in the selected distribution/user before live creation. It SHALL report executable/build evidence, readiness and missing capabilities. It SHALL NOT install, replace or upgrade ZMX implicitly, and SHALL NOT treat a version string alone as proof of managed leadership or authoritative screen/input support.

#### Scenario: Stock binary lacks required commands
- **WHEN** a configured executable lacks a required managed-session capability
- **THEN** live mode reports unsupported capability without creating a daemon or changing the executable

### Requirement: Endpoint identity is independent of pane identity
Every binding SHALL identify its endpoint, distribution/user, Linux socket directory, daemon name and observed incarnation independently of local pane/window IDs and Windows process IDs. Readback SHALL distinguish locally owned and externally attached sessions. A missing or ambiguous identity SHALL not authorize attachment replacement or termination.

#### Scenario: Same name in different distributions
- **WHEN** two distributions contain daemons with the same name
- **THEN** inventory and controls distinguish them and neither can satisfy the other's binding

#### Scenario: Name reused
- **WHEN** a daemon disappears and its name is reused for a new incarnation
- **THEN** an old binding cannot silently adopt or terminate the replacement

### Requirement: Existing-session attachment never creates a workload
Attaching a discovered daemon SHALL verify the exact endpoint and incarnation and SHALL never create a replacement shell if the daemon disappears. Inherited ZMX session-selection variables SHALL not redirect the attachment. New workload creation SHALL be a distinct operation from attach and retry.

#### Scenario: Daemon disappears after discovery
- **WHEN** the selected daemon vanishes between inventory and attachment
- **THEN** attachment fails visibly and no fresh workload is substituted

### Requirement: Disconnect and termination have distinct ownership rules
Explicit detach SHALL disconnect only the selected local attachment and keep the daemon alive. Ordinary close of a locally owned pane SHALL terminate its exact owned daemon; close of an externally attached pane SHALL only disconnect. Termination SHALL require an exact owned binding and revalidation; foreign, uncertain or replaced daemons SHALL be refused.

#### Scenario: Shared external daemon
- **WHEN** a pane attached to an externally owned daemon is closed
- **THEN** the other clients and workload remain running

#### Scenario: Owner closes a shared workload
- **WHEN** the locally owned pane is explicitly closed while another client is attached
- **THEN** close terminates the owned workload, including its other attachments, and the UI identifies this consequence; detach remains available to preserve it

#### Scenario: Explicit detach followed by restart
- **WHEN** a pane is explicitly detached and the application restarts
- **THEN** its daemon remains discoverable but that removed attachment is not automatically recreated

### Requirement: Background cleanup cannot claim Linux workloads
Application quit or UI failure SHALL preserve live daemons. Startup orphan cleanup, history clearing, restore clearing and failed attachment cleanup SHALL NOT terminate a daemon merely because no local pane currently claims it. Cleanup of an aborted creation SHALL target only the provable incarnation created by that operation.

#### Scenario: Detached daemon after startup reconciliation
- **WHEN** startup reconciliation finds an unused Windows transport associated with a detached daemon
- **THEN** transport cleanup does not issue daemon termination and the daemon remains available

### Requirement: Leadership governs terminal size and input
Managed attachment SHALL expose leader, follower, unowned or unknown role. Only the leader's grid SHALL control the workload size. Background resize and automated input SHALL not implicitly take leadership. A leadership claim SHALL be explicit, scoped to an attachment, and resilient to stale role notices after reconnect.

#### Scenario: Two clients have different sizes
- **WHEN** a follower resizes its window while a different client leads
- **THEN** the workload retains the leader's dimensions until an explicit leadership transition

### Requirement: Control reads and writes describe the authoritative workload
For managed ZMX panes, terminal text and cursor queries SHALL use authoritative daemon state rather than a potentially stale local rendering. Automated input SHALL preserve existing target and input-validation rules, report delivery acceptance or uncertainty, and SHALL NOT claim completion of the program or blindly retry ambiguous writes.

#### Scenario: Automation targets a follower
- **WHEN** an agent reads text and sends input through a follower pane
- **THEN** it reads the daemon's screen and sends input without claiming leadership or delivering to another active pane

### Requirement: Local controls are bounded and inspectable
Inventory, attach, detach, terminate, retry and leadership controls SHALL validate selectors and expose actual endpoint, pane, ownership, readiness and outcome. Read-only inventory SHALL neither focus panes nor start workloads. Usage errors SHALL exit 2 and refused or failed requests SHALL exit 1, including JSON mode. Timeout SHALL be reported as uncertainty when mutation completion is unknown.

#### Scenario: Unknown or cross-window target
- **WHEN** a lifecycle command names an unknown target or a pane outside its explicit window
- **THEN** it refuses without acting on the active pane

### Requirement: Compatibility is advertised by supported capability
The endpoint model SHALL preserve origin and per-pane daemon identity needed for future agterm adapters. This release SHALL report local WSL capabilities without advertising SSH discovery, agterm remote attach or presentation support. Unsupported remote requests SHALL refuse explicitly rather than claiming compatibility because basic ZMX attachment works.

#### Scenario: Local endpoint queried for remote support
- **WHEN** diagnostics are read for a ready local WSL endpoint
- **THEN** local attach capabilities are distinct from unimplemented agterm tree and presentation capabilities
