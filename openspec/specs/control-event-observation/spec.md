# control-event-observation Specification

## Purpose

Provide bounded, owner-aware event history and resumable observation without silently losing events across pagination, reconnection or application restart.

## Requirements

### Requirement: Event identity is scoped to a process run and window
Each event page SHALL carry `schema:1`, opaque `run`, exact `window`, `oldest`, `head`, `cursor`, `gap`, `more` and `events`. A process start SHALL mint a new run ID. Sequence numbers SHALL be positive monotonic Int64 values per window. Cursor zero SHALL precede its first event; an empty journal SHALL have head/cursor zero and oldest one. The resume identity SHALL be the tuple (run, window, cursor).

#### Scenario: Restart with reused window ID
- **WHEN** an observer sends the previous run ID after the application restarts
- **THEN** the response is `ok:false` with `code:run-mismatch` and the current run/window identity, never a successful continuation

### Requirement: Existing polling remains usable with safe pagination
The existing `events` command SHALL retain its ordinary response envelope and `events --since N --limit N` form. Omitted since SHALL mean zero; omitted limit or zero SHALL mean 1000, and positive limit SHALL be 1..1000. Cursor SHALL identify the last safely scanned sequence, not skip undispatched matching events; head SHALL identify the sampled journal head. Negative, noninteger, overflowing or future cursors SHALL refuse.

#### Scenario: Limit does not skip a page
- **WHEN** head is 12 and a request since 5 with limit 2 returns matching sequences 6 and 7
- **THEN** cursor is 7, head is 12, more is true, and the next request since 7 can return 8

#### Scenario: Int64 cursor
- **WHEN** a valid journal cursor exceeds Int32.MaxValue
- **THEN** CLI and server preserve its exact Int64 value without truncation

### Requirement: Retention loss is explicit
The journal SHALL retain at most 1000 events in memory per window with no cross-run persistence. A page SHALL report `gap:true` when since is less than oldest minus one, including an explicit since zero. It SHALL still return retained matching records and their bounds. A request without run SHALL remain supported for legacy polling but SHALL NOT promise restart detection.

#### Scenario: Slow poller falls behind
- **WHEN** oldest is 101 and a client requests since 50
- **THEN** the page reports gap true, oldest 101 and the available suffix; it never labels the suffix complete

### Requirement: Filters advance over nonmatching records without skipping matches
CLI `--kind TYPE[,TYPE...]` SHALL map to wire `args.kinds` (array of unique type names). Omission SHALL include all types. Empty/unknown kinds or duplicate options SHALL refuse before observation. Filtering SHALL NOT renumber events. Once the page limit is reached, cursor SHALL stop at that returned event; otherwise it SHALL advance to sampled head. More SHALL mean that cursor is below sampled head.

#### Scenario: No records match the filter
- **WHEN** a page scans through head 40 without finding a requested type
- **THEN** events is empty, cursor is 40 and more is false; the next poll does not rescan excluded records

### Requirement: Watch is opt-in and pins observation identity
`events --watch` SHALL continuously consume pages for one pinned window/run. Without since it SHALL establish a tail baseline at the first handshake head and emit no earlier records. Wire args.tail=true SHALL return that empty baseline and SHALL conflict with since. Explicit since greater than zero SHALL require run and an exact window ID for watch. Explicit since zero SHALL request retained history. Changing the frontmost window SHALL NOT redirect an established watch.

#### Scenario: Start watching the active window
- **WHEN** watch starts without since while A is active, then B is selected
- **THEN** it continues observing A after its initial baseline, and every page identifies A

#### Scenario: Explicit resume
- **WHEN** watch starts with `--window W --run R --since 25`
- **THEN** it resumes strictly after 25 in W/R, or refuses a mismatched run/window or invalid cursor

### Requirement: Continuous output has explicit control records and outcomes
Watch with `--json` SHALL emit flushed UTF-8 NDJSON ordinary envelopes, one per page including its initial handshake and empty heartbeat pages. Plain watch SHALL emit `seq type window sessionId-or-- paneId-or-- info-as-JSON` tab-separated event lines; resume metadata and diagnostics go to stderr. Ctrl+C SHALL close observation and exit 130. Refusal, transport/protocol failure, gap or run change SHALL exit 1. Watch SHALL NOT reconnect automatically.

#### Scenario: Resume after disconnection
- **WHEN** transport fails after a consumer has saved a page's tuple
- **THEN** watch exits 1; the consumer can explicitly resume that tuple, deduplicating by run/window/seq if its own output checkpoint was uncertain

#### Scenario: History gap during watch
- **WHEN** a watch page reports gap true
- **THEN** JSON output includes that page and its bounds, plain output diagnoses the gap without presenting the suffix as continuous, and the CLI exits 1

### Requirement: Long polls are bounded and cancellable
Wire `events` SHALL accept `args.wait-ms` as an integer 0..25000 (default zero). It SHALL return immediately for available matches, gap or refusal, otherwise return when matching data arrives or the wait expires. A no-match expiry SHALL return a successful empty page at the latest safe cursor. Waiting SHALL NOT hold the UI thread. Disconnect, window teardown and app shutdown SHALL release pending observation resources.

#### Scenario: Idle filtered watch
- **WHEN** only excluded events arrive during a 25000 ms poll
- **THEN** it returns no later than its deadline with the advanced cursor and an empty events array, without a busy CLI loop

### Requirement: Event payloads identify owners without changing legacy identity fields
Every event SHALL include seq, type and window. New typed lifecycle events SHALL include action plus sessionId and paneId when those owners exist. Existing `session` and `info` fields SHALL keep their established meaning; notably legacy status.session remains a pane ID. Typed payloads SHALL use stable IDs, including after swap, and removal events SHALL retain owner IDs after teardown.

#### Scenario: Status from a split pane
- **WHEN** a background split pane changes status
- **THEN** its status event identifies that pane and its owning session/window without substituting the focused pane

### Requirement: Lifecycle events describe committed changes
C1 SHALL add types selection, focus, split, scratch, overlay, hud, and session-status alongside existing status/notification/session/tree/bind types, using the lifecycle payload contract below. UI and API changes SHALL produce equivalent events after state commit. Refusals and unchanged operations SHALL not produce false change events. Close/removal SHALL terminate observers of the removed identity.

#### Scenario: Background focus and refused resize
- **WHEN** background focus changes and an axis-incompatible resize is refused
- **THEN** a focus event identifies the changed session/pane, no selection event is generated, and no split resize event is emitted for the refusal

#### Scenario: Overlay replacement and process exit
- **WHEN** an overlay is replaced and its old process exits afterwards
- **THEN** the close/open events identify distinct overlay IDs and a late old-process callback cannot report an exit for the new overlay

#### Scenario: Lifecycle payload schema

All rows include `window`, `action` and applicable exact `sessionId`/`paneId`. Optional absent owners are omitted, never filled with an unrelated active owner.

| type | actions | additional fields |
| --- | --- | --- |
| selection | selected, cleared | previousSessionId when present; new sessionId when selected |
| focus | changed | previousPaneId, paneId (new main/split focus) |
| split | opened, closed, axis-changed, resized, swapped | paneIds in current slot order, focusedPane, axis; ratios for split layouts; removedPaneId on close |
| scratch | opened, shown, hidden, closed | paneId of scratch |
| overlay | opened, exited, closed | overlayId, slot (session|pane), ownerPaneId for pane slot; exitCode on exit; reason on close (closed|replaced|owner-removed) |
| hud | opened, updated, closed | slot=session, anchorPaneId when anchored, reason on close (closed|replaced|owner-removed|expired); no message body |
| session-status | changed | status, previousStatus, winningPaneId, statusChangedAt |
| status (existing) | changed | paneId, status, statusChangedAt, blink, autoReset; retain legacy session/info |

Existing session lifecycle records gain `action:created|closed` and `sessionId` while keeping legacy `session`/`info`. Other legacy event types retain their existing fields and gain applicable owner IDs. Overlay hidden/shown actions are reserved for C2 and SHALL NOT imply that C1 implements those operations.

### Requirement: Observation has bounded payloads and capacity
Event records SHALL be at most 8 KiB serialized UTF-8. Oversized free text SHALL be shortened at a Unicode boundary with `truncated:true`; identity and required typed fields SHALL never be silently truncated. At most 128 pending long polls and status waits combined per window SHALL be admitted; excess requests SHALL receive `code:observer-limit`. No terminal buffers, command bodies or process environment SHALL be added to event payloads.

#### Scenario: Slow or excessive observers
- **WHEN** clients stop consuming or exceed observation capacity
- **THEN** memory remains bounded, additional observers receive an explicit refusal, and normal control/UI operations remain responsive
