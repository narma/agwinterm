# Tasks

Each group depends on the preceding groups unless stated otherwise. C0 is the prerequisite. A group is ready only when its implementation, tests and documentation below are complete.

## 1. Bounded event journal and page contract

- [x] 1.1 Extract the per-window observation coordinator with process run identity, Int64 sequence, bounded records and retention; verify empty journal, Unicode truncation and retention boundary tests.
- [x] 1.2 Implement safe pagination, filters, tail handshake and typed errors; verify limited pages never skip matching events, filtered scans advance safely, gaps and future/run-mismatched cursors are explicit.
- [x] 1.3 Carry Int64 and validated event arguments through host/dispatcher/CLI; update the event API documentation and verify request and compatibility tests.

## 2. Immutable status publication and membership

- [x] 2.1 Add immutable status notifications at the write boundary across serving session backends, preserving existing subscribers; verify rapid completed-to-idle and concurrent updates retain exact transition values.
- [x] 2.2 Maintain observed pane membership and session aggregation using existing priority; verify snapshot/registration barriers, auxiliary exclusion, split removal and retained input-closed panes.
- [x] 2.3 Publish typed status/session-status records while retaining legacy fields; document ownership and verify background pane identity, aggregate winner and same-status flag changes.

## 3. Committed lifecycle events

- [x] 3.1 Publish selection, focus and split lifecycle records from shared UI/control seams; verify background actions, slot swap, ratios, no-ops and refused operations against the payload table.
- [x] 3.2 Publish scratch, overlay and HUD lifetimes with stable owners and guarded process callbacks; verify replacement, late exit and owner teardown records without message/command bodies.
- [x] 3.3 Document all payloads and privacy bounds; verify representative UI and control operations produce equivalent typed lifecycle records.

## 4. Asynchronous observation transport

- [x] 4.1 Add cancellable asynchronous dispatch and filtered long polls with monotonic deadlines; verify matching wakeup, idle expiry and excluded-event floods without UI blocking.
- [x] 4.2 Enforce shared 128-observer capacity and connection/window/shutdown cleanup; verify disconnect releases within two seconds, pipeline misuse, slow readers and ordinary sequential request compatibility.
- [x] 4.3 Document wire wait-ms, errors and observation connection lifetime; verify real named-pipe tests cover EOF, cancellation and completed-write disposal without abandoned reads.

## 5. CLI watch

- [x] 5.1 Implement watch parsing, pinned tail/replay/resume and protocol validation; verify actual CLI processes refuse unsupported servers, invalid resume identity and malformed pages.
- [x] 5.2 Implement flushed NDJSON envelopes, plain rows/stderr metadata and terminal exit codes; verify baseline/heartbeat output, gap suffix behavior, transport failure and Ctrl+C without automatic reconnect.
- [x] 5.3 Update CLI usage, agent skill and observation examples; run documented tail and explicit resume examples against a private application instance.

## 6. Session and pane wait

- [x] 6.1 Resolve and pin session/pane targets with caller defaults and window bounds; verify ambiguity, auxiliary scope refusal, main/split ownership and selection changes.
- [x] 6.2 Implement atomic snapshot/register, latched transitions and monotonic match/timeout/removal ordering independently of journal retention; verify deterministic race barriers, history wrap and deadline equality.
- [x] 6.3 Implement CLI wait arguments, transport deadline derivation and distinct outcomes/exit codes; verify real CLI matched, timeout, refusal, malformed reply, transport timeout and client cancellation output.
- [x] 6.4 Document scope, aggregate priority, agent-reported completion and cancellation semantics in control API/usage/agent guidance; verify all documented wait examples against the implementation.

## 7. Integrated readiness

- [x] 7.1 Run Release build and relevant full Core/Pty regressions, including C0 reliability; record commands, counts and limitations in verification.md with no unexplained failures.
- [x] 7.2 Add and run isolated Windows acceptance for watch/wait and lifecycle readback; verify foreground/background targeting, removal and UI responsiveness with real processes.
- [x] 7.3 Extend and run real WSL native-bridge acceptance for watch interruption and wait match/timeout/cancellation; record evidence and confirm bridge cleanup.
- [x] 7.4 Validate OpenSpec strictly, audit every normative scenario against evidence and update the roadmap C1 status; verify no unimplemented requirement is marked complete.

## Workflow follow-up

- Sync the completed delta specs into main specs and archive this change.
- Commit the implementation, tests, documentation and archived change together, preserving unrelated working-tree edits.
- Proceed to C2 through its own OpenSpec proposal, design, specs and tasks; follow the same workflow for every remaining roadmap stage.
