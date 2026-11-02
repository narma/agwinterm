# Proposal

## Why

The Rust PTY host already preserves Windows terminal processes across UI restarts, but it cannot independently recover a surviving Linux workload after its WSL transport is lost or expose that workload to agterm. Add opt-in WSL ZMX persistence while keeping the Rust host, with one recovery decision that prevents duplicate shells, command replay into live agents, and accidental termination of shared sessions.

## What Changes

- Add opt-in ZMX support for ordinary WSL session panes, preserving the selected distribution/user and the `server-rust` backend. Windows-native profiles and transient terminals retain their existing behavior.
- Persist Linux endpoint and daemon identity independently of UI pane identity and Windows host creation tickets; distinguish owned sessions from attached sessions.
- Recover in order: adopt the existing Rust-hosted transport; otherwise attach the verified surviving ZMX daemon; otherwise report missing/unavailable state. Relaunch after confirmed loss is explicit and never masquerades as live recovery.
- Separate attachment-only detach from termination. Keep ordinary close semantics for locally owned sessions; closing an attachment to an externally owned daemon only disconnects it. Scope all cleanup to proven ownership and incarnation.
- Add bounded inventory, attach, detach, terminate and retry/readback contracts, capability checks, coherent screen/input behavior and explicit leadership handling.
- Establish compatibility seams and pinned fixtures for future agwinterm-to-agterm and agterm-to-WSL connections, without shipping SSH discovery, an `agtermctl` compatibility service, presentation streaming or remote UI in this change.
- Require routed Windows/WSL lifecycle verification with Rust host enabled, including UI restart, transport loss, missing daemon, ambiguous operations, split identity, screen replay and leadership.

This change currently authorizes planning artifacts only. Implementation tasks remain unchecked. Existing native-session restore, Recent reopening and non-ZMX control contracts are preserved.

## Capabilities

### New Capabilities

- `wsl-live-sessions`: WSL-only ZMX configuration, endpoint identity, lifecycle ownership, local controls, leadership and terminal/control coherence above the existing Rust PTY host.
- `wsl-session-recovery`: Durable binding and deterministic live recovery, replay suppression, explicit recovery after workload loss and compatibility with existing restore/history behavior.

### Modified Capabilities

None. `recent-closed-work`, `control-cli-contract` and `control-target-routing` remain constraints; this change does not redefine their existing requirements.

## Impact

- Expected implementation areas: `ShellProfiles`, `WslIntegration`, `RestoreState`, `ServerSession`, Win32 pane creation/restoration/close paths, control server/CLI and installed agent guidance.
- Rust PTY host and protobuf transport remain in place. ZMX runs in WSL, behind `wsl.exe`; no Windows ZMX port or Rust-host replacement is proposed.
- Introduces an explicitly configured, capability-checked Linux ZMX dependency. The agterm patched baseline must be evaluated and pinned before enabling the feature; no automatic installation or replacement of a user's ZMX.
- Future interoperability depends on agterm-specific endpoint/tree schemas, managed leadership and presentation protocols, not just stock ZMX. Those dependencies are documented and fixture-tested at the local model boundary, not advertised as delivered remote support.
- Dedicated tests extend existing Rust host, startup cleanup, WSL, restore and Recent coverage. Implementation was explicitly requested on 2026-11-02, including the isolated dependency download and design correction for WSL lifetime. Production session manipulation and deployment remain outside this change.
