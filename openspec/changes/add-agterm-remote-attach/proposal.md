# Proposal

## Why

Stage 1 preserves local WSL workloads, but Agwinterm cannot yet view a session owned by agterm on another machine. Stage 2 of the [required roadmap](../../../docs/plans/2026-11-01-wsl-zmx-agterm-roadmap.md) adds that outbound direction while retaining Rust hosting and the existing workload's identity.

## What Changes

- Add explicit remote discovery and attachment through an existing WSL SSH configuration, using the origin's `agtermctl zmx tree --json` response.
- Import eligible terminal sessions and their split panes into a chosen local window, keeping origin identities separate from local IDs.
- Attach only existing verified daemons; missing sessions, incompatible peers and uncertain requests never start replacement remote shells.
- Treat every remote pane as borrowed: close, detach, application exit and cleanup preserve the origin workload. Expose explicit leadership, authoritative reads, acknowledged input and bounded reconnect.
- Preserve remote binding metadata through UI/transport recovery without replaying buffers, agent-resume, pinned or captured commands.
- Add pinned contract fixtures and real agterm acceptance. A fixture-only pass or unavailable real peer does not complete this stage.
- Keep inbound discovery/export (stage 3), remote presentation and companion/page channels (stage 4), SSH administration and remote process termination outside this change.

## Capabilities

### New Capabilities

- `agterm-remote-sessions`: Outbound WSL/SSH discovery, terminal attachment, borrowed ownership, leadership, authoritative control and recovery of agterm-origin sessions.

### Modified Capabilities

- `wsl-live-sessions`: Advertise capabilities by endpoint/provider so adding an outbound adapter does not imply local WSL export or remote presentation support.

## Impact

Extend the model seam in `ZmxContracts.cs`, CLI/control routing, Rust-backed pane creation and restore metadata. Introduce a separate remote binding/transport implementation; do not reuse the Linux-only local ownership ledger for macOS origins or change the Rust wire protocol. Existing local `zmx` controls keep their semantics.

Requires an already configured WSL SSH client and a compatible real agterm origin. Dependency installation, SSH keys, host trust and remote helper deployment are explicit setup, never implicit application behavior. The refreshed upstream baseline is `9133258994a6cafb330ee354df949b76b1b2834b` (2026-11-02); its stock create-on-miss attach guard does not meet our existing-only requirement, so capability proof is an implementation gate. This proposal records planned behavior, not delivered interoperability.
