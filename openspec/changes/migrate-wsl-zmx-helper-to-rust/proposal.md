# Proposal

## Why

The first-stage WSL recovery helper currently requires Python 3.11 at runtime and retains an interpreter for lifetime leases. The application already ships a static Rust Linux helper; reuse it to remove this dependency without changing recovery guarantees.

## What Changes

- Add isolated ZMX lifecycle modes to the existing agwinterm-hook binary for Linux x64/arm64.
- Move exact identity, bounded IPC, durable ledger, capability probing and lifetime leases to Rust.
- Bootstrap and pin the bundled executable per endpoint without installing ZMX or touching shell/agent configuration.
- Preserve operation/checkpoint compatibility, ownership fences, terminal continuity and all reviewed crash fixes.
- Remove Python from production resources and execution paths; Python may remain test/build tooling.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `wsl-live-sessions`: bundled native lifecycle operations require no Python runtime.

## Impact

native/agwinterm-hook, WslLiveClient, Linux payload packaging, integration/regression tests, development documentation. Rust PTY hosting and WSL-only ZMX remain unchanged. PR remains open without merge; final head requires fresh Greptile and green CI.
