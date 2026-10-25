# Proposal

## Why

After C0 made targeting and CLI outcomes reliable, automation still has to poll incomplete event history and can miss short-lived agent states. C1 supplies bounded, resumable observation and race-free status waiting without blocking the Windows UI.

## What Changes

- Preserve one-shot `events --since`, add kind filters and opt-in `events --watch` backed by bounded asynchronous long polling.
- Add process-run/window identity, retention bounds, safe pagination and explicit gap/restart outcomes. **BREAKING correction:** a limited page's `cursor` becomes the last safely scanned sequence, not the journal head; `head` exposes the latter. This repairs event loss in the current documented cursor loop.
- Add owner-aware selection, focus, split, scratch, overlay and HUD lifecycle events while retaining existing event types/fields. Emit only committed changes, with no terminal-output recording.
- Add `session wait` for one or several statuses, with explicit pane/session scope, deadline and cancellation. Snapshot plus observer registration must not lose a completed/auto-reset transition.
- Document specialized wait exit codes, bounded resources, reconnect semantics and serving-version requirements; add host-free, CLI-process and isolated Windows/WSL acceptance.

## Capabilities

### New Capabilities

- `control-event-observation`: window-scoped event history, resumable filtered watch, cursor continuity and lifecycle payloads.
- `session-status-wait`: race-free observation of pane or aggregate session status, terminal outcomes and cancellation.

### Modified Capabilities

None. C0's ordinary CLI outcomes and routing rules are now archived and synced into main specs. This proposal preserves them, adding explicitly documented specialized observation commands.

## Impact

- Depends on implemented/verified C0 (`control-cli-reliability`); archive/sync of C0 is not an implementation prerequisite.
- Touches the existing Win32 `_evtLog`/`EmitEvent`/`Events`, status publication and aggregate seams, `ISessionHost`, asynchronous `ControlServer` connection handling, CLI parsing/output/cancellation and feature docs/tests.
- Existing journal is per window, retains 1000 records, exposes Int64 sequence in the host but parses `since` as Int32 in CLI/server. Existing status events store pane IDs in legacy `session`; preserve that field and add unambiguous owner IDs.
- No new third-party dependency or runtime Python requirement. No durable journal, cross-run replay, batch wait targets, Lite contract expansion, release or installation.
- C2 hide/show events are reserved for C2 implementation; C1 does not implement overlay hiding, picker/ask, HUD presentation changes or C3–C8.
