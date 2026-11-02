# Tasks

## 1. Native lifecycle core

- [x] 1.1 Add isolated Linux ZMX dispatch, bounded protocol, exact IPC and readiness probing to agwinterm-hook; verify native unit tests and x64/arm64 builds.
- [x] 1.2 Port durable ledger, one-shot creation, reconcile/restart, ownership controls and lifetime leases; verify crash boundaries, legacy fingerprints and real daemon lifecycle tests.

## 2. Application integration

- [x] 2.1 Switch installation, requests, attachments and legacy checkpoint migration to the bundled native helper; verify focused .NET tests and remove all production Python resources.
- [x] 2.2 Update routed integration tests and development documentation for native commands and no-Python runtime; verify real Rust/WSL identity, terminal replay and lifecycle survival on a private desktop.

## 3. Acceptance

- [x] 3.1 Verify existing hook behavior, static x64/arm64 payload packaging, strict OpenSpec and release build; record checks and performance limits.
- [x] 3.2 Complete Greptile review and green CI on the final commit; keep PR open and unmerged.

Implementation commit `41a46443fc7c3724ac60a486067cb4ae8d0083e5` passed all jobs
in CI run `41454213130`. Greptile review `37560633` completed at 5/5, with all
five PR review threads resolved. Completion-metadata commits are also checked
before delivery; PR #23 remains open and unmerged.
