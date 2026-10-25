# Design

## Context

See proposal.md for motivation and the two delta specs for normative behavior. This is C1 only and depends on verified C0.

Observed seams:
- `Program.ControlHost.cs` has a per-window 1000-item `_evtLog`, a lock and Int64 `_evtSeq`. `Events` currently returns journal head even when Take(limit) omitted intervening records. CLI and dispatcher parse since as Int32.
- `ControlServer.HandleClientAsync` accepts multiple request/reply lines, but calls synchronous Dispatch. A blocking wait there would consume a thread and cannot promptly notice peer closure without explicit disconnect handling.
- `Program.Sessions.cs` subscribes to parameterless `ISession.StatusChanged`; it rereads current status and emits legacy status.session as a pane ID. Re-reading after a fast second change is insufficient evidence of the first transition.
- `StatusAggregate` defines blocked > completed > active > idle across normal panes. Main/split pane membership changes can change aggregate status without a new pane status write.
- C0 establishes strict selectors, active/caller exceptions, FIFO queued target resolution and uncertainty wording. Its contracts are now synced to main specs and archived with the implementation commit.

## Goals / Non-Goals

**Goals:** one coherent observation boundary, explicit continuity, bounded asynchronous lifetime, owner-stable event payloads and race tests that observe actual data rather than just request echo.

**Non-Goals:** persistent event storage, reliable-delivery acknowledgements, automatic reconnect, output scraping, full process completion tracking, batch targets, C2 visibility controls, Lite mirroring, release/install. Keep the named-pipe JSON transport; WSL observes through its existing native bridge.

## Decisions

### 1. A window observation coordinator owns journal and wait registration

Extract a host-free coordinator into the existing managed shared layer. It owns per-window sequence allocation, retention, current observed pane status/membership, pending observers and terminal outcomes under one short lock. App run ID is minted once per process and shared by its window coordinators; quick has its own observation owner, returned as window `quick`.

Publish immutable transition values at the status-write boundary and propagate them through all applicable session adapters. Never implement lossless waiting by queueing a UI callback that later rereads mutable Status. Use existing seams where possible, adding a typed status-snapshot notification when needed; preserve the existing StatusChanged notification for current subscribers. Read/subscribe establishment must reconcile initial pane snapshots with transitions under the same observation ordering. Membership updates are published on committed UI transitions. Complete asynchronous continuations outside locks.

Alternatives rejected: polling tree loses short states; public-journal-only waiting loses a match after ring wrap; holding UI while awaiting a condition deadlocks user interaction. A registered waiter latches its first match independently of public retention. Add deterministic barriers around registration, status writes and teardown for tests.

### 2. Pages expose safe progress and explicit continuity

Wire example (one-shot or long poll):

```json
{"cmd":"events","window":"exact-window-id","args":{"since":12,"run":"opaque-run","limit":100,"kinds":["status","session-status"],"wait-ms":25000}}
```

```json
{"ok":true,"result":{"schema":1,"run":"opaque-run","window":"exact-window-id","oldest":1,"head":18,"cursor":18,"gap":false,"more":false,"events":[]}}
```

Use Int64 end-to-end. Scan records after since in sequence order. On filling limit, stop at the final returned record; otherwise advance to sampled head. More reflects remaining unscanned history, not a promise that a filter will match. A filtered scan with no matches can advance the cursor while waiting; wake only for matching events, gap, owner closure or deadline. Sample pages atomically with their metadata.

The initial tail handshake uses args.tail=true and wait-ms=0; since and tail are mutually exclusive. A watch continuation always sends exact returned window plus run. `run-mismatch`, `window-gone`, `cursor-ahead`, `observer-limit` and invalid-argument errors carry ordinary ok:false/error plus machine-readable code; identity metadata is supplied when known. A retention gap is a successful page with gap=true so callers can inspect its retained suffix. Watch treats it as a terminal continuity failure.

Do not silently upgrade legacy run-less polling to restart-safe resume: docs must state that limitation. Unknown run/schema metadata from an old server is a watch protocol refusal, not permission to present unverified output. The cursor correction is an intentional behavior change; code that wants the old head can use the new head field.

### 3. Keep watch as a CLI page loop; wait is a server-side operation

`events --watch [--since N --run R] [--window W] [--kind status,overlay] [--limit N] [--json]` uses 25-second async long polls. No extra persistent server subscription ID is exposed. JSON emits a full envelope per response, including baseline/heartbeat, so consumers checkpoint cursor/run/window together; consumers must save the tuple only after processing the page. Plain output prints event rows and puts metadata on stderr. Explicit reconnect is preferable to an automatic loop that could hide gaps or changed instances.

`session wait completed,blocked --scope session --target ID --wait-timeout 30 --json` sends:

```json
{"cmd":"session.wait","target":"ID","window":"W","args":{"scope":"session","statuses":["completed","blocked"],"timeout-ms":30000}}
```

Successful match example:

```json
{"ok":true,"result":{"outcome":"matched","run":"R","window":"W","scope":"session","sessionId":"S","status":"completed","seq":42,"source":"transition","elapsedMs":120}}
```

Session matches report sessionId and winning paneId; pane matches report paneId and sessionId where owned. Timeout reports only outcome/identity/scope/elapsedMs, not a fabricated matching status. Snapshot seq is the registration boundary head; transition seq is the matching status/session-status record. Session-status emits when aggregate status changes; same-status flag changes still produce pane status events but no false aggregate transition.

Session scope deliberately resolves main/split IDs to their owner. Pane scope is explicit, preventing a session's primary-pane-ID alias from accidentally redefining aggregate wait. Omitted targets use the ordinary caller default; the three C0 active-only verbs are unchanged. Freeze identity before awaiting; never re-resolve active or a prefix on wake-up. Main/split deletion recomputes aggregate for surviving sessions; a removed pane wait fails rather than following its replacement.

### 4. Cancellation and limits are part of transport lifecycle

Add an async dispatch path for observation verbs while retaining synchronous Dispatch for ordinary callers/tests. Each pending observation owns its connection until its single reply. While awaiting, the server monitors peer EOF/cancellation; a second pipelined request on that connection before the observation completes is refused as protocol misuse and terminates that observer. Ordinary sequential requests remain supported. Never race two readers for one pipe or abandon outstanding overlapped I/O during disposal; reuse the repository's completed-write/dispose discipline.

Connection close releases pending observation capacity promptly (test bound: two seconds on an otherwise responsive host); shutdown/window teardown cancel registrations. Slow reply writes run outside coordinator/UI locks and are cancelled/disposed through a tested connection lifetime, with no unbounded per-reader queue. Pending cap is 128 combined long polls/waits per window. Retained journal cap is 1000 records, each <=8 KiB UTF-8; required IDs are preserved, free text truncation is marked. Payloads use bounded identifiers and arrays already limited to two panes.

CLI Ctrl+C closes its observation connection and exits 130. If no final wait result was consumed, JSON cancellation is `{"ok":false,"code":"cancelled","source":"client","error":"cancelled"}`. Watch writes no synthetic event. Wait transport deadline derives from observation deadline unless explicitly overridden; expiry is exit 1, distinct from server-confirmed timeout exit 3. No implicit retry can claim to recover a missed short-lived state.

Real WSL acceptance established that terminal Ctrl+C/SIGINT can terminate the Windows interop child without raising .NET Console.CancelKeyPress. The installed wrapper therefore delegates observation commands to the existing native Linux helper. It traps SIGINT, terminates and reaps its child, forwards watch pages immediately, and buffers the single bounded wait reply so cancellation cannot produce two final envelopes. Completed replies win over a late interrupt. Ordinary commands exec the Windows CLI unchanged. This path has no hook watchdog deadline, Python requirement or additional dependency; native hook commands retain their existing watchdogs.

### 5. Emit at committed lifecycle seams and retain compatibility fields

Use the spec's lifecycle table as the payload source of truth. Instrument SetActive, pane focus, split/axis/divider/swap/close, scratch visibility/lifetime, overlay slot open/exit/replace/close and HUD open/update/clear. Capture old IDs before disposal. Ordinary UI actions and control commands must reach the same publisher. Duplicate callbacks and no-ops must not invent changes. A teardown close event precedes terminal owner removal; late process callbacks are guarded by exact slot identity.

Preserve legacy events and their session/info payloads where they already exist, adding owner IDs; do not repurpose status.session. Overlay launch acceptance and eventual process exit are separate records. No hide/show controls are added before C2, and no command text or terminal output is recorded. HUD update events omit message bodies. Quick events omit sessionId when no owning library session exists.

## Risks / Trade-offs

- Status adapter races → typed immutable transition publication plus deterministic auto-reset/concurrency tests for every serving session backend, not only a fake host.
- Aggregate observer diverges from tree → reuse StatusAggregate precedence, publish membership changes and compare live read-back; define same-status writes separately from status changes.
- Cursors remain numbers for compatibility → preserve Int64 serialization and warn external JavaScript clients about exact integer handling; no string conversion of legacy fields.
- Long-poll filter floods or slow clients → bounded scans/pages, shared capacity, no UI locks while waiting, explicit EOF/teardown tests.
- Pagination correction surprises clients using cursor as head → document migration to head and pin limited-page continuity regression tests.
- Native bridge closes differently from .NET client → test watch interruption, wait timeout and cancellation from Windows and real WSL using native helpers.

## Migration Plan

Deliver in dependency order: journal contract → lifecycle/status publication → async transport and watch → wait → integrated acceptance/docs. Each group has a readiness gate in tasks.md. Keep C0 tests green and shared Lite conformance unchanged. No stored data migration is needed; rollback removes C1 capability and loses only in-memory observation state. Do not label C1 ready until all spec scenarios have evidence. Archive/sync and commit the completed change under the user's authorized workflow; release/install remain separate.
