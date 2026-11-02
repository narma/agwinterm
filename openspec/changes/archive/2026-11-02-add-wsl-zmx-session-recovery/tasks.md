# Tasks

Implementation requested on 2026-11-02. Groups are dependency-ordered; the first gate must pass before enabling live mode. Future SSH and presentation work is not part of this checklist. Dependency build/probe evidence is recorded in `docs/wsl-zmx-development.md`.

## 1. Prove the Linux dependency and transport boundary

2026-11-02: all implementation and local acceptance tasks passed. Isolated-distro negative control confirmed WSL idle distribution shutdown; the pidfd foreground lifetime lease resolves host-loss survival and exits when the workload exits. Design updated with user authorization. See `docs/wsl-zmx-development.md` for final evidence and environment limits. Live mode remains explicitly opt-in and disabled by default; review completed and specifications synchronized; archived on 2026-11-02.

- [x] 1.1 Add a reproducible isolated WSL build/probe recipe for the pinned ZMX revision and agterm patches from design.md; record revision, patch digest, licenses and Linux build evidence, and verify managed leadership, screen, acknowledged input and snapshot/publication capabilities without changing the user's executable.
- [x] 1.2 Add a routed WSL survival fixture using an isolated Rust host, socket directory and long-running workload; verify identical Linux process incarnation after UI quit, forced test-UI exit and loss of only the test Windows host/attachment. Record failure instead of enabling live mode if WSL kills the daemon.
- [x] 1.3 Prototype exact Linux daemon identity and owned termination in the test fixture, including name/PID reuse and lost replies; verify a stale operation never terminates a replacement and document the supported kernel/identity mechanism before lifecycle code proceeds.
- [x] 1.4 Document the supported baseline, explicit dependency provisioning, bash launch coverage, WSL shutdown/reboot limits and unsupported host/surface combinations; verify the documented readiness probe distinguishes missing executable from missing capability.

## 2. Model profile configuration and independent identities

- [x] 2.1 Add validated optional WSL profile ZMX configuration and readiness results; extend `WslIntegrationTests` with distro/user defaults, quoted paths, custom shell argv, environment isolation and unsupported native/transient requests, and verify disabled profiles retain prior launch behavior.
- [x] 2.2 Add endpoint, workload-incarnation, ownership and binding models distinct from pane IDs and host tickets; verify host-free tests for identical names across endpoints, malformed values, stale identities and independent pane/workload UUIDs.
- [x] 2.3 Add versioned optional binding persistence with visible save errors; extend `RestoreStateTests` for old checkpoints, invalid/unknown bindings, changed WSL defaults and write failure, and document the additive format and downgrade limits.
- [x] 2.4 Pin agterm tree/endpoint, managed-role and screen fixtures with provenance; verify model mappings preserve both inbound and outbound origin identities and report presentation/SSH capabilities as unsupported without implementing their transports.

## 3. Implement local Linux lifecycle coordination

- [x] 3.1 Implement the bounded WSL helper and private operation ledger with serialized creation and reconciliation; verify restart, partial ledger writes, lost creation replies and repeated operation IDs cannot create duplicate workloads, and document ledger ownership/recovery rules.
- [x] 3.2 Implement owned creation with a fresh daemon UUID and one-time profile/creation command; verify readiness is published only with recoverable identity evidence and command counters show one launch under retries.
- [x] 3.3 Implement existing-only attach and inventory with incarnation checks and inherited-ZMX-variable isolation; verify disappeared/replaced daemons fail without a replacement shell, malicious/malformed arguments refuse, and read-only inventory starts no workloads.
- [x] 3.4 Implement attachment-only detach and exact owned termination; verify a second client survives detach, external termination refuses, stale operations cannot kill replacements and uncertain termination remains visible. Document close versus detach behavior alongside these tests.

## 4. Integrate Rust transport and recovery decisions

- [x] 4.1 Integrate live creation and matching transport adoption into `CreatePane` while retaining `server-rust` and the existing host protocol; extend Rust host integration coverage to prove no second WSL client on adoption and no in-process fallback for a failed live request.
- [x] 4.2 Add the explicit per-binding recovery state machine and serialized retry/cancellation; verify host-adopted, daemon-reattached, missing, unsupported, unavailable and uncertain outcomes with deterministic helper failures and stale callback tests.
- [x] 4.3 Route buffer seeding, agent resume, pins and captured-command replay through the recovery result; verify sentinel workloads receive zero replay commands on both adoption paths while ordinary non-ZMX restore tests remain green.
- [x] 4.4 Add explicit fresh-start selection for shell, agent, pin and allowed captured sources after confirmed workload loss; verify missing source and uncertain endpoint refuse, exactly one selected source runs, and readback reports a new incarnation rather than survival.
- [x] 4.5 Integrate quit, close, detach, split removal and startup cleanup with ownership rules; extend hosted cleanup/startup-claim tests and routed WSL tests to prove late reconnect cannot resurrect closed panes and Windows orphan cleanup does not terminate daemons.
- [x] 4.6 Preserve binding identity through swap, survivor promotion and multi-window checkpoint restore; extend restore/Recent tests for partial failures, fresh daemon identities on Recent reopen, retained but unexecuted pins, and clear operations that leave live workloads intact. Document the recovery outcome table.

## 5. Add local controls and coherent terminal behavior

- [x] 5.1 Add the planned `zmx list/attach/detach/terminate/retry/restart/lead` CLI and raw-control contracts; verify successful/refused JSON exits, local usage exit 2, duplicate/unknown options, exact pane/window routing and uncertain mutation outcomes with focused contract tests.
- [x] 5.2 Add optional tree/readback state and UI recovery actions with explicit ownership and lost-workload labels; verify a missing daemon retains its layout and metadata, owner-close consequences are visible, and retry never acts as fresh start.
- [x] 5.3 Integrate managed-role notices and explicit leadership claims using attachment nonce/generation; verify two differently sized clients, follower resize, stale notices and automatic reattach do not steal a valid leader's size or leak role notices into titles.
- [x] 5.4 Route managed screen/cursor queries and automated input to authoritative daemon operations; verify follower queries, acknowledged/uncertain delivery, readonly and control-byte validation, and no implicit leadership changes. Document acceptance versus execution completion.
- [x] 5.5 Verify replay, scrollback, alternate screen, Unicode/wide cells, colors, cursor, keyboard modes, bracketed paste and terminal query replies across both recovery paths; add regressions for duplicate history and conflicting terminal responses using the routed two-client WSL fixture.
- [x] 5.6 Update control docs, profile/recovery guidance and installed agent instructions for the delivered local surface; validate examples against isolated fixtures and explicitly distinguish planned agterm compatibility from supported local ZMX behavior.

## 6. Integrated acceptance and release readiness

- [x] 6.1 Run the full routed WSL matrix with Rust hosting: two windows, split/swap, two profiles/users where available, host/attachment loss, daemon loss, dependency failure, renamed defaults and in-flight close/retry races; retain process identity and command-counter evidence and mark unavailable configurations unverified.
- [x] 6.2 Validate workload-loss behavior after shutdown/reboot in a dedicated disposable environment only, never the user's distribution; retain evidence of missing-state readback and explicit single-source relaunch, or leave this gate pending if that environment is unavailable.
- [x] 6.3 Run focused ordinary host, Windows profile, native restore, Recent, routing and cleanup regressions through their mandated routes; retain unchanged non-ZMX behavior and the Rust backend/protocol identity as acceptance evidence.
- [x] 6.4 Run repeated create/adopt/reattach/detach/terminate cycles under isolated namespaces; verify no duplicate workloads, leaked Windows transports or unintended daemon cleanup, and record restore latency/resource observations with test build/WSL/kernel versions.
- [x] 6.5 Verify disabled-by-default rollout, checkpoint backup, dependency-unavailable recovery and disable/rollback instructions in an isolated install; update parity documentation to identify WSL-only local support and retain separate deferred inbound/outbound agterm changes.

## Workflow follow-up

- This is stage 1 of the required [four-stage WSL ZMX/agterm roadmap](../../../../docs/plans/2026-11-01-wsl-zmx-agterm-roadmap.md). All four stages are requested; stages 2–4 remain outside this change, not optional.
- Review these planning artifacts before explicitly starting implementation.
- After implementation and acceptance, synchronize/archive using the repository's OpenSpec workflow; do not mark unchecked acceptance as complete.
- Before archive, update the roadmap with verification evidence and the resulting archive link; adjust this relative roadmap link for the extra archive directory. Report stage 2 as the next unfinished stage.
- Propose stage 2 (outbound agterm attachment) next, followed by stage 3 (inbound WSL export through an agterm-compatible adapter) and stage 4 (bidirectional presentation). They are not prerequisites for completing this local change, but are required to complete the user's overall outcome.
