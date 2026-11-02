# Design

## Context

See [proposal.md](proposal.md) for motivation and the [roadmap](../../../docs/plans/2026-11-01-wsl-zmx-agterm-roadmap.md) for the four-stage boundary. Implementation was requested on 2026-11-02. Stage 1 is merged in origin/narma at 5552130; the implementation branch starts there. Discovery/transport evidence and pending acceptance are recorded in docs/agterm-remote-development.md.

Observed local seams:

- `ZmxContracts.cs` contains fixture-only `AgtermTree`/origin mappings. It does not decode the real control response envelope, validate every topology field, execute SSH or supply remote identity evidence.
- `WslLiveClient`, `WslLiveOperations` and the bundled Rust Linux helper implement Linux-local ownership, `/proc` identities and pidfd leases. Their ledger must not become authority over a macOS origin.
- `Program.WslLive.cs`, `Program.Sessions.cs` and `RestoreState.cs` already separate a pane, a Rust transport ticket and a live workload, serialize recovery and suppress replay. Reuse these behaviors through a provider boundary rather than interpreting a remote binding as local owned work.
- `ControlServer` provides authoritative-read interception, explicit window routing and input validation. Existing local `zmx.attach` takes profile/daemon/incarnation; preserve this contract.

### Refreshed upstream evidence

Inspected on 2026-11-02 at agterm commit [`9133258994a6cafb330ee354df949b76b1b2834b`](https://github.com/umputun/agterm/tree/9133258994a6cafb330ee354df949b76b1b2834b), six commits beyond the stage-1 research pin. These are source observations, not runtime acceptance:

- [RemoteTree.swift](https://github.com/umputun/agterm/blob/9133258994a6cafb330ee354df949b76b1b2834b/agtermCore/Sources/agtermCore/RemoteTree.swift): decode `ControlResponse.ok` and `result.remote`; require process exit zero first. Terminal candidates have every pane claimed/running and actually ZMX-backed. Page companions may have no panes.
- [RemoteSession.swift](https://github.com/umputun/agterm/blob/9133258994a6cafb330ee354df949b76b1b2834b/agtermCore/Sources/agtermCore/RemoteSession.swift): discovery/control use SSH without a PTY, attachment forces a PTY, BatchMode avoids prompts, and remote shell quoting handles account shells. Ordinary attach uses a create-only error command when a daemon disappears; this does not satisfy our stronger existing-only contract.
- [ControlServer+Zmx.swift](https://github.com/umputun/agterm/blob/9133258994a6cafb330ee354df949b76b1b2834b/agterm/Control/ControlServer+Zmx.swift): origin tree spans open windows; import resolves session IDs and pane roles, and carries origin endpoint paths. The tree alone has no durable daemon incarnation proof.
- [ZmxClient.swift](https://github.com/umputun/agterm/blob/9133258994a6cafb330ee354df949b76b1b2834b/agterm/Ghostty/ZmxClient.swift): authoritative screen uses the daemon; input acknowledgement means queued, not consumed. Retrying uncertain input is unsafe.
- [Patch contract](https://github.com/umputun/agterm/blob/9133258994a6cafb330ee354df949b76b1b2834b/scripts/zmx-patches/README.md): managed leadership, screen/type and atomic publication remain relevant. New `0004-managed-exit-keeps-screen.patch` changes managed exit behavior and requires a refreshed build/test manifest. ZMX remains pinned to `8bab1f0173b07e79835ea372d749af3dbf0d0842`; do not silently update stage-1 dependency pins.

## Goals / Non-Goals

**Goals:** provide one remote terminal provider with explicit origin identity, separate finite control requests and long-lived terminal streams, exact existing-only attachment, borrowed ownership and testable failure semantics. Preserve the Rust host protocol and keep Agwinterm-side Linux processes inside WSL.

**Non-Goals:** exporting WSL sessions to agterm, presentation streams, companion-group/page import, remote process creation/kill, SSH server administration or general SSH-terminal profiles. These exclusions do not remove the required later roadmap stages.

## Decisions

### 1. Separate remote binding and operation provider

Introduce a separate validated remote-binding model in the existing raw `LiveBinding` envelope, with a provider discriminator (`agterm-ssh`), resolved WSL distribution/user, SSH destination, origin endpoint, origin session/window/workspace, pane role, observed incarnation, local transport ticket and attachment nonce. Preserve raw malformed metadata as an error surface, not an ordinary shell checkpoint. Remote bindings have no owned state or relaunch source.

Share replay suppression, generation fencing and local transport adoption with stage 1. Keep endpoint-specific probe/attach/read/type operations separate. Extending the local Linux binding with fake macOS `/proc` identities was rejected because it would weaken identity validation and mix termination authority.

### Shared native protocol member

Following the requested stage-1 reuse, `agwinterm-zmx` is a workspace library shared by `agwinterm-hook` and `agwinterm-remote`. It owns bounded wire framing, existing socket connection and platform process identity observation. Linux identity JSON remains unchanged for existing ledgers. Hook retains pidfd fencing, ownership records, creation and termination. Remote exposes only probe/attach/screen/type. The library introduces no separately installed runtime.

### 2. Explicit WSL SSH execution

Use a selected WSL profile only for resolved distribution/user and SSH configuration; do not execute its shell startup command or wrap SSH in a newly created local ZMX workload. Terminal transport is Rust PTY host -> `wsl.exe` -> SSH PTY -> origin client. Finite discovery/read/input requests use non-PTY SSH with bounded stdout/stderr, handshake and overall deadlines. Cancelling a finite request stops only its request transport; it must not signal origin workloads.

Honor existing host trust and keys; use batch mode and strict host-key checking, no automatic enrollment or password UI. Refuse destination options/control characters. Keep local process argv structured and quote the remote shell boundary for sh/zsh/fish/tcsh, including apostrophes and backslashes. Validate origin executable/socket paths independently. Do not expose secrets or full SSH environment in diagnostics. Existing configured SSH aliases remain the user's trust boundary; changing configuration must not silently retarget a persisted origin identity.

### 3. Existing-only origin capability is a gate

Do not use ordinary `zmx attach NAME` with a check-then-attach sequence or an error-shell guard. Implement a small explicitly provisioned origin bridge plus the pinned connected-FD ZMX client seam. The bridge connects once, reads identity on that channel, verifies the requested incarnation, then passes that same socket to the client. It exposes only probe, existing attach, screen and acknowledged type; no create, kill, reset or prune operations.

Port identity observation to the actual origin platform: macOS process start/boot identity and socket identity, with Linux support only where explicitly tested. The stage-1 `/proc`/pidfd helper is not portable proof. Scope origin identity to the authenticated account and endpoint; record a build/capability manifest for the bridge and client. Missing bridge or client support reports unsupported. Never upload, install, replace a running daemon's binary or configure services as a side effect of discovery.

The first implementation gate must prove the connection-preserving seam on a real agterm host, including disappearance/name reuse. Build/provision the adapter as an explicit test setup step only after a host is supplied and setup authorized. If this cannot be proved, retain pending acceptance and do not weaken the contract or replace it with fixture evidence. This adapter is an outbound compatibility prerequisite, not the inbound WSL export service of stage 3.

### 4. Discovery and import are distinct transactions

Add an additive `zmx remote` CLI group (list, attach, retry, detach, lead), with raw verbs under `zmx.remote.*`; leave local `zmx.*` untouched. Discovery selects a WSL profile and SSH destination. Attach selects an exact origin session and exact local window, re-reads the tree and validates it before creating local panes. Use the origin endpoint paths, not the local executable path.

Decode the real response envelope; bound sizes/counts, reject duplicate identity fields, duplicate roles, missing left panes, invalid names, unsupported axes and conflicting IDs. Ignore unknown additive metadata safely. Show page/other unsupported content as unavailable for terminal import. Resolve panes by role, never array order. Upstream [SplitAxis.swift](https://github.com/umputun/agterm/blob/9133258994a6cafb330ee354df949b76b1b2834b/agtermCore/Sources/agtermCore/SplitAxis.swift) serializes its `leftRight`/`topBottom` cases as `vertical`/`horizontal`, matching Agwinterm's wire values; validate these raw values rather than Swift case names.

Validate the entire intended topology before local publication. After attachment begins, retain a visible pane-specific failure if only one side connects; dispose only local transports on rollback. Never claim two-pane success from one connected pane. Source labels are display data, not command arguments or ownership evidence.

### 5. Borrowed lifetime and recovery

Local close/detach and app shutdown cannot invoke origin lifecycle mutations. UI restart adopts only the saved exact Rust ticket; transport loss probes the saved incarnation before attaching again. Serialize by binding, use bounded backoff and cancellation, and discard late responses after close or replacement. Automatic reconnect remains follower; explicit `lead` creates a fresh nonce/generation and claims size.

Persist the prepared Rust ticket and nonce before allowing transport creation, not after the managed-role acknowledgement. If restore cannot adopt that exact ticket, cancel only its reservation/transport before trying again. Checkpoint failure must create no child. The bounded retry budget resets after successful attachment.

Require the running daemon's source/feature build response (IPC tag 26) on the verified socket as well as the signed client/daemon file hashes. A file can be replaced while an old process image remains alive; an absent or mismatched runtime capability must refuse rather than infer primary-history support from the new file.

Persist layout and remote metadata, but never seed saved buffers into a reattached stream or execute saved resume/pin/captured commands. Recent retains a remote descriptor and reopens only by explicit existing-only attachment; it never enters stage-1 owned creation. Missing origin work is not restartable from the viewer. Readonly affects automated input; leadership does not override target validation.

### 6. Authoritative control remains separate from rendering

Route text/cursor and input to the bridge's verified connected daemon. Read responses include geometry/revision; unsupported styled queries fail explicitly. Input bytes travel on stdin with a bounded framed request, are sent once and require an acknowledgement. Lost acknowledgement returns uncertainty, not success or an automatic retry. Terminal replay tests cover history, hidden primary screen, alternate screen, wide glyphs, keyboard modes and single query replies with multiple clients.

### 7. Capability reporting and acceptance

Keep local WSL export and presentation false. Report outbound terminal capability only for a successfully probed endpoint; model presence is not compatibility. Store provenance separately for the stage-1 local build and new origin builds. Fixture tests cover parser/quoting/races but real acceptance requires a running agterm origin, SSH connection and both products' observed workload identities.

## Risks / Trade-offs

- Origin lacks an atomic existing-only attach facility -> explicit bridge/client setup and a first implementation gate; ordinary upstream attach is refused.
- macOS identity differs from Linux -> native platform observation, replacement-race tests and no reuse of Linux kill/lease authority.
- SSH alias or defaults change -> persist resolved WSL selectors and origin identity; fail on mismatch instead of retargeting.
- Partial split/network failure -> per-pane outcome, retained metadata, no whole-session success claim and no destructive rollback.
- Upstream behavior changes -> pin source/build evidence, refresh fixtures and negotiate required capabilities rather than infer from version text.
- A macOS peer is supplied and read-only discovery passes, but its live mode is disabled. Native bridge provisioning and actual terminal acceptance remain pending.

## Migration Plan

1. Keep the merged stage-1 implementation unchanged and opt-in; retain its native Rust helper and dependency restrictions.
2. Implement behind explicit remote commands/configuration. Establish real-peer setup and capability proof before enabling attachment.
3. Run host-free fixtures, routed private-desktop WSL tests, real-peer matrix and ordinary/local-ZMX regressions. Record build hashes and sanitized outcomes.
4. Disabling remote commands preserves saved metadata as unavailable. Detach viewers before downgrade; back up checkpoints because older versions may ignore new fields. Do not stop origin workloads or modify SSH configuration during rollback.
5. Update the roadmap and archive only after all acceptance passes. Stage 3 is next after stage 2; stage 4 remains required.
