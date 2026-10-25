# Spec Delta

## Purpose

Wait for a pane or aggregate session to reach a requested agent status without losing brief transitions or confusing timeout, removal and cancellation with success.

## ADDED Requirements

### Requirement: Wait inputs are explicit and validated
CLI `session wait STATUS[,STATUS...] [--target ID] [--scope session|pane] [--wait-timeout SECONDS]` SHALL map to wire `session.wait` with args.statuses (nonempty unique array), scope and timeout-ms. Statuses SHALL be idle, active, blocked or completed, case-insensitive in CLI and lowercase on wire. Scope SHALL default to session. Unknown options, repeated selectors, empty targets and invalid values SHALL refuse without registering a waiter.

#### Scenario: Multiple acceptable statuses
- **WHEN** a caller waits for blocked,completed
- **THEN** either status satisfies the request; it does not mean that both must occur

### Requirement: Target scope resolves once and never follows selection
Omitted CLI target SHALL use AGWINTERM_SESSION_ID when present, otherwise active context. Session scope SHALL resolve a session or its main/split pane to the owning session; auxiliary targets SHALL refuse. Pane scope SHALL use existing exact-pane-first rules and allow scratch/overlay/quick. Explicit window SHALL bound resolution. The resolved identity SHALL be pinned until completion; ambiguity, deletion or later selection SHALL never retarget the waiter.

#### Scenario: Two panes with different status
- **WHEN** a session has one blocked and one completed pane
- **THEN** default session scope does not match completed, while pane scope on the completed pane matches immediately

#### Scenario: Caller differs from visible session
- **WHEN** CLI caller A waits without target while B is visible
- **THEN** it waits for A's owner under session scope; C0's active-only exception for focus/resize/command run remains unchanged

### Requirement: Session status uses the existing aggregation rule
Session scope SHALL aggregate only main/split panes with blocked greater than completed greater than active greater than idle. Pane scope SHALL read only its addressed surface. Structural changes SHALL recompute aggregate status. Process exit SHALL NOT be inferred as completed; status remains agent-reported, and an input-closed retained pane can still be observed.

#### Scenario: Removal changes aggregate
- **WHEN** a blocked split pane is removed and its session survives with a completed pane
- **THEN** session wait for completed matches the recomputed aggregate; a waiter pinned to the removed pane fails target-gone

### Requirement: Snapshot and registration cannot lose a transition
Wait SHALL atomically establish its observation boundary and inspect the current status. A matching current value SHALL return immediately. Every subsequent committed transition through a requested value SHALL satisfy a registered waiter even if auto-reset follows before its continuation runs. Pre-registration historical matches SHALL NOT satisfy a new wait. Matched results SHALL identify whether snapshot or transition satisfied the request.

#### Scenario: Completed immediately auto-resets
- **WHEN** a registered target becomes completed then idle before a waiting client resumes
- **THEN** the result is matched with status completed and the matching transition sequence, not a timeout or idle

#### Scenario: Completion predates registration
- **WHEN** completed then idle occurred before the observation boundary
- **THEN** a new wait for completed remains pending until a new matching transition or terminal outcome

### Requirement: Wait outcomes and exit codes are distinct
A matched wire reply SHALL be ok:true with result.outcome matched, scope, run, window, sessionId/paneId where applicable, status, seq, source (snapshot|transition), and elapsedMs. Timeout SHALL be ok:true with outcome timeout and the same identity/elapsed fields but no matched status/seq/source. CLI SHALL exit 0 only for matched, 3 for timeout, 2 for local usage, 130 for Ctrl+C, and 1 for refusal, target/window loss or transport/protocol failure.

#### Scenario: JSON timeout
- **WHEN** the requested deadline expires without a match
- **THEN** --json prints the final timeout envelope and exits 3; it does not report an ordinary success just because ok is true

### Requirement: Deadline and cancellation have defined bounds
The observation deadline SHALL default to 30000 ms; wire timeout-ms SHALL accept integers 0..86400000, with zero unbounded. CLI wait-timeout SHALL accept integer seconds 0..86400. Deadlines SHALL use a monotonic clock from accepted registration. The CLI's default transport reply deadline SHALL be wait-timeout plus five seconds, or unbounded for zero; explicit --timeout SHALL retain C0 transport semantics. Match/timeout/removal SHALL complete once in serialized observation order.

#### Scenario: Shorter transport timeout
- **WHEN** --timeout expires before --wait-timeout
- **THEN** the CLI exits 1 as a transport failure, never 3 as a confirmed observation timeout, and closes the observation connection

#### Scenario: Match races the deadline
- **WHEN** a matching transition commits at or after the monotonic deadline
- **THEN** it cannot turn an already due timeout into a match; a match committed before the deadline remains matched even if its response is delivered later

### Requirement: Removal and disconnect release the waiter
Target removal SHALL fail with code target-gone; window closure SHALL fail window-gone when a reply can still be delivered. Cancellation, peer disconnect and server shutdown SHALL unregister pending observers without blocking UI. Local Ctrl+C SHALL close the connection, print diagnostics on stderr and exit 130; JSON mode SHALL produce a client-sourced cancelled error envelope if no final result was already consumed. Wait SHALL NOT automatically reconnect or replay after transport loss.

#### Scenario: User changes windows then removes the target
- **WHEN** a pending waiter remains pinned to A while B becomes active and A is removed
- **THEN** it terminates target-gone without waiting for B, and its observer capacity is released

### Requirement: Waiting is independent of journal retention
Pending waiters SHALL latch matching transitions without requiring the event to remain in the public 1000-record journal. They SHALL share the 128-pending-observer window limit and refuse excess requests with observer-limit. Slow readers SHALL not block status publication or UI. Plain mode SHALL print only the matched status to stdout; nonmatch outcomes SHALL be diagnosed on stderr. JSON mode SHALL output exactly one final envelope.

#### Scenario: Journal wraps before client reads
- **WHEN** a waiter matches and unrelated events overwrite that public history before response delivery
- **THEN** its latched match remains valid, and the system does not extend journal retention for that reader
