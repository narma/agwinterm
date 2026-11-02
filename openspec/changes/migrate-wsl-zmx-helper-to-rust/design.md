# Design

## Context

See proposal.md. agwinterm-hook already ships as static musl binaries on x64 and arm64. Existing bootstrap selects architecture and copies the bundled binary to Linux without installing dependencies. The reviewed Python implementation defines the current JSON wire protocol and version-1 ledger.

## Goals / Non-Goals

Preserve all first-stage behavior and crash fences while removing Python from production paths. Test/build scripts may use Python. Do not change ZMX patches, replace the Rust PTY host, enable remote support, or merge the PR.

## Decisions

- Add a Linux-only zmx module and --zmx/--zmx-install dispatch to agwinterm-hook before its hook watchdog. Lifecycle requests have bounded input/locks/IPC; successful lease holders wait on pidfds without a request deadline killing the lease.
- Reuse the architecture-selected bundled payload and bootstrap for installation only. Install an immutable content-addressed binary under the existing endpoint's private helpers directory; keep socket/ledger locations and v1 records unchanged. No shell/agent config changes.
- Preserve exact connected Unix socket and pidfd verification, nofollow reads, endpoint locking, atomic fsynced records and non-replacing owner index publication. Record markers remain one-shot even across failures.
- Refresh legacy script helper locations to a native endpoint before invoking them, including restoration and explicit controls. No interpreter fallback. Retain predecessor identity when restart requests are interrupted.
- Use Rust crates for JSON, Linux ABI, SHA-256 and base64; statically link them. These are build dependencies, not user-installed runtimes. Keep module boundaries for ledger, exact IPC, readiness and lifecycle within the existing executable rather than making a new distributable.
- Port failure-boundary tests to native code and keep black-box real Rust/WSL acceptance. Test old record compatibility and operation idempotency, byte-bounded IPC, terminal continuity, shared external leases and dropped replies.

## Risks / Trade-offs

- ABI or serialization differences → keep explicit little-endian ZMX layouts and Python-compatible fingerprint encoding for old records; validate fixtures and real daemon behavior.
- Existing hook deadlines accidentally kill leases → dispatch before watchdog and exercise idle host-loss survival.
- Migration loses authority → only replace helper location after checking saved distribution/user/ledger/socket endpoint; preserve all operation and daemon identities.
- Native code increases unsafe surface → isolate Linux syscalls, use owned descriptors and failure cleanup; test crash boundaries and wrong identity refusals.

## Migration Plan

Build and package the same helper artifacts, switch Windows caller to native commands, remove production Python resources, update docs, run native and routed WSL suites, then repeat Greptile and CI. Keep the PR unmerged. Do not downgrade active checkpoints to ordinary shells.
