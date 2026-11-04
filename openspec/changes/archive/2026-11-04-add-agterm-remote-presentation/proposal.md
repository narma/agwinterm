# Proposal

## Why

The two terminal directions are implemented and accepted, but both Agwinterm adapters still report presentation unsupported. Stage 4 of the [roadmap](../../../../docs/plans/2026-11-01-wsl-zmx-agterm-roadmap.md) must carry agent presentation and interactive outcomes between those same origins and viewers without confusing terminal ownership with presentation authority.

## What Changes

- Negotiate a separate bounded `agtermctl zmx present`-compatible stream in both directions, with version/kind intersection, snapshots, revisions, reconnect and explicit unsupported states.
- Mirror status, context, HUD and notifications first; preserve exact origin session/pane routing and suppress replayed alerts and relay loops.
- Add a separately granted sole presenter role, native questions with validated answers/cancellation, origin-run program overlays through scoped job channels, and web overlays through scoped resource/bridge channels. All these families remain required for stage completion.
- Reuse native questions, Rust-hosted terminals and the existing local WebView2 surface. Retain origin authority for jobs, page grants and final outcomes; never execute an origin program on the viewer.
- Extend the explicitly provisioned WSL adapter with narrowly authorized presentation/job/page operations. No automatic SSH setup, runtime installation or general remote command proxy.
- Verify older-peer degradation and actual pinned peers in both directions, preserving terminal recovery and local presentation regressions. Keep outstanding local WebView2 release checks visible.

## Capabilities

### New Capabilities

- `remote-presentation`: Negotiated bidirectional presentation, identity mapping, snapshots, live events, presenter authority, interaction channels and real-peer acceptance.

### Modified Capabilities

- `wsl-origin-export`: Permit separately negotiated scoped presentation and its authorized child channels while continuing to refuse terminal lifecycle mutation.

The existing `wsl-live-sessions` requirement already makes presentation conditional on separate negotiation; `agterm-remote-sessions` already distinguishes terminal and presentation support. Their terminal requirements do not change. Existing local question/HUD/overlay contracts remain authoritative for local callers; unsupported cross-product shapes must be reported explicitly.

## Impact

Expected changes touch `AgtermRemoteDiscovery`/binding/operations, `Program.Remote.cs`, the native WSL export adapter, CLI/control routing and the local question/HUD/program/web surfaces. A new presentation state machine and origin broker will bridge those seams. Rust remains the Windows PTY host, ZMX remains WSL-only on the Agwinterm side, and no separately installed runtime is introduced.

Research is pinned to agterm `c63e5158e637662ded7ba3a12eac4881b24caef1` (v0.40.0); this is source evidence, not runtime acceptance. The prior accepted terminal pin `9133258994a6cafb330ee354df949b76b1b2834b` remains a regression baseline. Remote clipboard, SSH administration, arbitrary companion/group import and full CLI parity remain out of scope. Implementation is authorized and in progress; see tasks and development evidence for the accepted subset and pending matrix.
