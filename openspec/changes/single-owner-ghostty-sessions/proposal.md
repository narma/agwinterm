# Proposal

## Why

Hosted Ghostty sessions currently parse the same PTY output in the .NET host and a UI replica, retaining two terminal histories and image stores. A single authoritative terminal should reduce duplicated work and memory, while preserving serverless operation and proving that input-to-visible-output latency does not regress.

## What Changes

- Keep one live Ghostty terminal per session: in the .NET host for hosted sessions, or in the UI process for in-process sessions. No host is launched for in-process operation.
- Replace UI access to mutable emulators with a common session presentation/command interface. Hosted UI receives bounded viewport updates, not VT bytes or a native terminal replica; local UI uses direct calls with the same semantics.
- Move mode-dependent input encoding, terminal responses, history/search/selection and terminal state mutations to the owner. Keep Windows keyboard translation, IME and rendering in UI.
- Add versioned presentation, ordered input, query and host-effect contracts with backpressure, reconnect fencing and bounded memory.
- Establish repeatable latency, throughput and memory baselines before changes; require non-regression and measure the expected input-latency improvement separately.
- Ship only the Ghostty backend. Remove managed/Rust engine selection and old Windows hosts from the new runtime. Upgrade compatibility is saved-state import: the old installation saves state and closes its sessions before replacement; the new version imports layout/history/settings, not running process identity.
- Preserve current control API behavior, default-terminal handoff, WSL/remote recovery and no-new-runtime-dependency policy.

## Capabilities

### New Capabilities

- `terminal-session-presentation`: Single-owner terminal operation, transport-independent UI access, ordered input and bounded presentation with lifecycle and performance acceptance.

### Modified Capabilities

None. Existing pane appearance, diagnostics and recovery behavior is preserved. The existing `wsl-session-recovery` spec's Rust-specific wording predates PR #35's dual-host routing; this change does not reinterpret that as permission to recreate live transports. Reconcile that pre-existing documentation discrepancy separately from this transport refactor.

## Impact

Primary changes: `ISession`, `TerminalSession`, `ServerSession`, `PtyHostServer`, `PtyHostClient`, `proto/ptyhost.proto`, native capture adapter and Win32 rendering/input/search/selection/UIA/control consumers. Tests extend Ghostty native/server harnesses and routed Windows/WSL integration suites. No Ghostty upgrade, new external runtime, or multi-client collaboration is included. The bundled Rust Linux helper remains; it is distinct from the retired Windows Rust host. This proposal plans follow-up implementation on the current PR foundation; it does not change executable code.
