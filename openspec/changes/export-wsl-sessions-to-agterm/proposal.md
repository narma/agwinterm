# Proposal

## Why

Stage 2 lets Agwinterm view agterm workloads, but agterm cannot discover Agwinterm-owned WSL terminals. Stage 3 supplies the reverse terminal direction while retaining exact workload identity and the Rust Windows PTY host.

## What Changes

- Publish an explicitly selected WSL endpoint through an agterm-compatible discovery/control adapter, including complete single/split topology and Linux paths.
- Filter exports to verified owned workloads in the selected distribution/user/origin/socket namespace; never export ordinary or borrowed remote panes.
- Provide existing-only attachment behind the endpoint executable expected by agterm, with exact incarnation checks and managed leadership.
- Define Windows-UI-unavailable behavior and explicit endpoint provisioning, without installing SSH services or exposing the Rust host pipe.
- Exercise real agterm discovery, attachment, leadership, disconnect, application restarts and endpoint isolation; retain stage-2 regressions.

## Capabilities

### New Capabilities
- `wsl-origin-export`: Scoped agterm discovery, existing-only attachment and restricted origin control for owned WSL terminals.

### Modified Capabilities
- `wsl-live-sessions`: Distinguish explicitly provisioned inbound export from local readiness and unsupported presentation.

## Impact

The Pty layer gains a tested projection/eligibility boundary. The Win32 control layer supplies origin topology; the Linux native helper bridges restricted metadata/control and exact daemon attachment. Provisioning and routed integration tests are additive. Stage 4 presentation, general SSH administration and full agterm CLI parity remain separate.
