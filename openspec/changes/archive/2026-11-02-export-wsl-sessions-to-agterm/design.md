# Design

## Context

See proposal.md and docs/plans/2026-11-01-wsl-zmx-agterm-roadmap.md. Work starts at origin/narma `41cc0d9`. WslLiveBinding already separates owned/external lifetime, endpoint and Linux process/socket identity. WslLiveOperations and native agwinterm-hook provide exact daemon operations; agwinterm-zmx owns connected-socket identity. Stage 2's AgtermRemoteDiscovery decodes the real control envelope and rejects inconsistent topology.

Upstream RemoteTree.swift and RemoteSession.swift were refreshed through GitHub on 2026-11-02 (master); the compatibility/acceptance baseline remains agterm `9133258994a6cafb330ee354df949b76b1b2834b`. Source links: https://github.com/umputun/agterm/blob/master/agtermCore/Sources/agtermCore/RemoteTree.swift and https://github.com/umputun/agterm/blob/master/agtermCore/Sources/agtermCore/RemoteSession.swift. The viewer runs `agtermctl zmx tree --json`, then the returned executable with `attach DAEMON /bin/sh -c GUARD`, ZMX_DIR and optional managed-lead variables. Stock attach can create a daemon; publishing the raw ZMX executable would violate exact existing-only semantics.

## Goals / Non-Goals

Goals: isolate a reusable, host-free eligibility/projection seam; retain Linux ownership authority; make public attachment endpoints carry immutable incarnation authority; support origin UI loss without killing the workload.

Non-goals: presentation, page/companion import, full agterm CLI parity, automatic SSH provisioning, or changing the Windows PTY protocol.

## Decisions

1. Build discovery from an immutable UI snapshot plus independent live observations. A selected scope contains distribution, user, origin, executable and socket directory. Endpoint Id is currently allocated per binding, so equality of Id is NOT endpoint membership. Compare the actual scope fields. Require each pane's owned, valid, non-pending binding, matching OriginPaneId, active transport and non-closing state. Require observed exact identity from the same scope. Omit an entire ineligible split. Reject ambiguous duplicate session/daemon identity or inconsistent window/workspace metadata instead of silently picking a row. Order panes by current role, not arrival order.

2. Share the existing AgtermTree wire records. Serialize into `ok/result/remote`; omit presentation entirely. Validate the final bounded envelope through the stage-2 decoder to keep incoming and outgoing shape consistent. A projection is NOT readiness evidence for SSH, provisioned adapters or live daemon support. Pure model code must not enable the legacy SupportsSsh flag.

3. Add a narrowly scoped local export control path and Linux adapter in subsequent tasks. Capture UI data on the UI thread; probe off-thread; recheck the snapshot generation before publishing, refusing concurrent replacement/close. Do not use profile inventory for discovery: PrepareAsync can provision helpers and lacks current window topology. Aggregate explicit windows without active-window fallback. Every request is bounded and a failure is explicit.

4. Provision an account-private adapter directory explicitly. Bind its configuration to one origin control endpoint and Linux namespace. A private immutable manifest names exact bindings/incarnations; the endpoint executable path includes its manifest identity. Agterm persists that path, so an old viewer cannot use a fresh tree's name-reused daemon. The native adapter rejects arbitrary trailing commands, restricts inherited ZMX variables and passes the same verified socket to the attested existing-FD client. Reuse existing native identity/readiness checks; do not duplicate a Linux process probe in C#.

5. The adapter's agtermctl-compatible entry point serves fresh tree and the exact supported terminal controls only. It bridges to the normal Windows application control endpoint through a bounded local relay, never the Rust host pipe. Failed origin UI access refuses discovery/control. Existing stream attachment uses the immutable manifest and live daemon verification, so Windows UI loss does not kill or replace workloads. Revocation/owner termination is authoritative on the Linux side. No automatic input retry after an uncertain acknowledgement.

6. Provisioning records Linux executable paths, user, file modes, capability/build evidence and the configured origin route. Reject foreign-user, symlinked or writable-by-others adapter state. Never overwrite an existing agtermctl or start sshd implicitly. Missing setup produces actionable unavailable status. The actual Mac and WSL SSH endpoint must be verified before enabling/exporting the adapter.

## Risks / Trade-offs

- Discovery is inherently a snapshot -> exact connected-socket verification on every attach; immutable manifest paths prevent name reuse across rediscovery.
- Split topology changes during probes -> generation recheck and whole-session omission, never a fabricated single pane.
- Windows interop can be absent in an SSH session -> explicit endpoint preflight; fail unavailable, preserve existing streams.
- Latest upstream can diverge -> pinned actual-client matrix, record source/build evidence separately from model tests.
- Remote API reachability is not user isolation -> private account-owned state plus explicit namespace matching and native UID checks.

## Migration Plan

Land the host-free projection and eligibility tests first, with no advertised inbound readiness. Wire live control, native adapter and explicit provisioning next. Run routed Windows/WSL tests and actual pinned agterm acceptance before claiming stage completion. Disabling export prevents new discovery/attachment without terminating daemons. Keep immutable manifest evidence until associated viewers are detached; rollback never kills workloads or changes SSH services.

## Open Questions

The user supplied Mac 192.168.50.16 and Ubuntu SSH_AUTH_SOCK=/home/alzo/.ssh/ssh-agent.sock. Read-only SSH confirms Darwin. WSL already has sshd listening on 2222 and reports 192.168.50.22. Reverse authentication succeeds through the Mac's `wsl` alias with SSH_AUTH_SOCK=/var/run/com.apple.launchd.zjh1c8dJXU/Listeners. Isolated actual-client acceptance passed; fixture paths and binary hashes are recorded in docs/wsl-origin-export-development.md. These are deployment/test selectors, not protocol decisions.
