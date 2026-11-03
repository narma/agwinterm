# Spec Delta

## Purpose

Carry agent presentation and interactive outcomes between Agwinterm and agterm in both directions while preserving origin identity, terminal independence and bounded authority.

## ADDED Requirements

### Requirement: Presentation is negotiated independently
Each direction SHALL negotiate presentation version and supported kinds independently of terminal attachment. Missing or incompatible support SHALL be visible and SHALL NOT disconnect or replace a terminal. Unsupported kinds or option shapes SHALL be refused without silently changing their meaning.

#### Scenario: Older terminal-only peer
- **WHEN** a verified terminal peer advertises no presentation version
- **THEN** terminal access remains usable and presentation is reported unsupported without launching a presentation channel

#### Scenario: Partial feature intersection
- **WHEN** a peer supports passive presentation but not an interactive family or required option
- **THEN** only the negotiated subset is used and the origin receives an explicit refusal or keeps the interaction locally

### Requirement: Presentation targets retain exact origin identity
Every presentation binding SHALL retain endpoint, origin session, stable origin pane identities and their local mappings. Pane swaps SHALL preserve ownership. Missing, replaced, ambiguous or out-of-scope targets SHALL refuse without active-pane fallback. Mirrored state SHALL NOT be exported onward as locally originated state.

#### Scenario: Pane swap with a delayed update
- **WHEN** a pane swaps sides before its HUD update arrives
- **THEN** the update follows its stable origin identity or is refused if that identity no longer exists

#### Scenario: Same IDs on different endpoints
- **WHEN** two origins publish the same session or interaction ID
- **THEN** an answer or update for one endpoint cannot affect the other

### Requirement: Frames are bounded and generation ordered
The channel SHALL use bounded newline-delimited JSON with a negotiated hello, connection generation and increasing origin revisions. Invalid frames, queue overflow, timeout and stale connections SHALL fail only their presentation channel. Unknown additive kinds SHALL cause no action while preserving ordering. Earlier-generation callbacks SHALL not mutate current state.

#### Scenario: Replay after reconnect
- **WHEN** an old connection delivers a frame after a new hello and snapshot
- **THEN** it is discarded even when its numeric revision exceeds the current connection's revision

#### Scenario: Stalled reader
- **WHEN** a viewer stops reading until the queue limit is reached
- **THEN** the channel terminates within its deadline without unbounded buffering or blocking terminal input

### Requirement: Passive state and live events have distinct recovery
Snapshots SHALL replace status, context and HUD state, including explicit clears, without generating transition alerts. HUD expiry SHALL use remaining lifetime and reject stale withdrawal. Notifications SHALL be live events, never snapshot replay. Duplicate revisions SHALL have no repeated effect; reconnect SHALL not claim delivery of events lost while disconnected.

#### Scenario: Reconnect with a timed HUD
- **WHEN** a snapshot carries an existing blocked status and a HUD with remaining lifetime
- **THEN** the viewer restores that state without another blocked alert and does not restart the HUD's original full lifetime

#### Scenario: Duplicate notification
- **WHEN** a notification frame is received twice on one generation
- **THEN** it creates at most one viewer notification and no notification is echoed back to the origin

### Requirement: Presenter authority is separate from terminal leadership
At most one viewer SHALL hold an origin session's presenter grant. Only the current granted connection and owner generation SHALL resolve delegated interactions. Loss of that connection SHALL revoke the grant and retire its replicas. Acquiring presentation authority SHALL NOT claim terminal leadership or workload ownership.

#### Scenario: Two viewers request presentation
- **WHEN** another live viewer already holds the grant
- **THEN** the second viewer remains a passive mirror and cannot answer the first viewer's questions

#### Scenario: Late answer after grant loss
- **WHEN** a disconnected presenter sends an answer after authority has returned to the origin or another viewer
- **THEN** the origin ignores it and preserves the current interaction outcome

### Requirement: Questions preserve schema and origin outcomes
Delegated questions SHALL preserve button IDs, supported text/choice forms, cancellation and exact owner identity. The origin SHALL validate answers against its stored request and retain first completion. Unsupported form shapes SHALL refuse before showing an altered question. Origin dismissal SHALL remove a replica without synthesizing an answer.

#### Scenario: Answer races cancellation
- **WHEN** a valid answer and cancellation compete for the same question
- **THEN** only the first origin-accepted completion is retained and repeated results return that same outcome

#### Scenario: Local form cannot be represented remotely
- **WHEN** an Agwinterm multi-field form cannot be expressed by the negotiated agterm question schema
- **THEN** it remains available at the origin with an explicit remote-unsupported reason rather than dropping fields or splitting the answer

#### Scenario: Origin question has no explicit session target
- **WHEN** an existing Agwinterm caller opens a window-owned question without the optional exact-session target
- **THEN** it retains its local window behavior and is not assigned to the active session for remote delegation

#### Scenario: Origin question targets an exact exported session
- **WHEN** a caller opens a representable question with an exact session ID in the selected exact window
- **THEN** the origin retains that immutable window/session ownership, refuses wrong-window or missing targets, and may delegate it only through that session's current presenter

#### Scenario: Answer acknowledgement is lost
- **WHEN** the viewer cannot determine whether its answer was accepted
- **THEN** it reports uncertainty, does not resend into a new generation and consults an available exact origin result without inventing success

### Requirement: Program overlays execute only on their origin
An origin-created overlay job SHALL be claimed at most once by an authorized presenter through its scoped job channel. Command, environment and cwd SHALL remain origin-owned. Viewer rendering SHALL preserve input, resize, close, wait and result semantics for negotiated options. Refusal or failed claim SHALL not launch a local substitute or duplicate job.

#### Scenario: Expired or reused job claim
- **WHEN** a job identifier is claimed after expiry, presenter loss or an earlier successful claim
- **THEN** the claim fails and no program starts

#### Scenario: Viewer closes an overlay
- **WHEN** a viewer closes its delegated program surface
- **THEN** the origin applies that job's close/result policy exactly once, releases its slot and preserves the underlying terminal workload

#### Scenario: Presentation disconnects while the job channel survives
- **WHEN** presentation disconnects after a job has been claimed and its separate PTY channel remains connected
- **THEN** presenter authority is revoked but the running job remains on that channel, its orphaned surface closes when the job ends, and no later presentation connection adopts or replays it

#### Scenario: Presentation disconnects before claim or after job exit
- **WHEN** presentation disconnects with an unclaimed reservation or an already-ended held surface
- **THEN** the reservation is retired without execution or the held surface closes, while the base terminal survives

### Requirement: Web pages use scoped resource and bridge channels
A delegated page SHALL use distinct origin/local IDs and a single-use, expiring channel grant. File resources SHALL remain within the origin's authorized root. Bridge calls SHALL run only under the original page's grants and target scope. Tokens SHALL not appear in public readback or logs. The viewer SHALL NOT reinterpret an origin path as a local file.

#### Scenario: Resource escapes the grant
- **WHEN** a resource request traverses a parent path or symlink outside the grant root
- **THEN** the origin refuses it without exposing file contents

#### Scenario: Page attempts a broader bridge call
- **WHEN** a delegated page requests another session or a command outside its original grant
- **THEN** the bridge refuses and neither viewer nor origin executes that command

### Requirement: Web page controls and outcomes are revision fenced
Page controls SHALL preserve supported hide/show, reload, navigation, resize and close behavior with ordered command acknowledgement. Navigation or reload SHALL invalidate old document callbacks. Submission and cancellation SHALL retain first origin completion. Missing viewer runtime or unsupported options SHALL refuse explicitly without affecting terminals or opening a substitute page.

#### Scenario: Reload races a submission
- **WHEN** a stale document submits after a new document revision is active
- **THEN** its callback cannot complete the current page and accepted earlier outcomes remain unchanged

#### Scenario: Missing WebView2 runtime
- **WHEN** a negotiated page reaches a Windows viewer without an available runtime
- **THEN** the origin receives a page refusal, no runtime is installed, and terminal and passive presentation remain available

### Requirement: Loss and teardown preserve workload identity
Presentation reconnect SHALL be bounded and revalidate the saved origin binding. UI loss, detach and viewer close SHALL revoke presentation authority and settle or return delegated work according to its family policy. Old questions, jobs and page tokens SHALL NOT be replayed or silently recreated. Terminal recovery SHALL retain its existing ownership and no-replay guarantees.

#### Scenario: Origin UI restarts
- **WHEN** the origin UI disappears while terminal and presentation streams are active
- **THEN** terminal workloads survive, interactions become unavailable or settle explicitly, and fresh negotiation uses current origin state after restart

### Requirement: Completion requires both real directions and all families
Acceptance SHALL record pinned source/binary evidence and supported/unsupported options for passive state, notifications, questions, program overlays and web overlays in both directions. Each family SHALL have real-peer success and failure evidence. Fixture-only results, a passive-only milestone or outstanding required browser checks SHALL NOT be reported as full stage-4 completion.

#### Scenario: Passive presentation passes first
- **WHEN** status and HUD work but questions or overlay channels have not passed real-peer acceptance
- **THEN** stage 4 remains incomplete and its remaining tasks stay pending

#### Scenario: Required local browser evidence is missing
- **WHEN** remote page fixtures pass but the required IME or clean/offline browser release checks lack evidence
- **THEN** the report identifies those pending checks and makes no full browser release-readiness claim
