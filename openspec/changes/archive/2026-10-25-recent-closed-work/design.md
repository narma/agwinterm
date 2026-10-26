# Design

## Context

See proposal.md. Existing `ClosedSession`/`ClosedWorkspace` lists live per Program, with a shared close sequence only within that window. Capture stores the active cwd but not split/pins; reopen removes records before creation. Cold restore already has typed `SessionState`/`PaneState` and safe ID folding. Native windows share one UI thread and app directory.

## Goals / Non-Goals

Goals: one app-wide recent store, explicit exact-entry operations, durable bounded snapshots and loss-aware recovery. Non-goals: replaying foreground commands, restoring transient surfaces, scrollback archival, remote/quick history, opening closed source windows or altering cold restore-clear semantics.

## Decisions

- Keep separate `recent.json` in the app directory, atomically replace via a same-directory temporary file. This survives source-window deletion and avoids changing the byte-compatible live restore schema. Use a typed pure model in Pty, validate on load and bound whole entries by age/count/serialized bytes. Keep diagnostics in list results and native toast. A malformed file is renamed `.bad`; no auto-launch ever occurs from history.
- Make native menu history app-wide using the same lists/model as CLI. Preserve existing menu labels and most-recent behavior; stable GUID entry ID is separate from stable session identity. Source window is informational; `--window` is the destination. List/clear are global and reject destination/session selectors.
- Capture the same supported pane metadata used by cold restore, without buffers, captured foreground command or automatic agent replay. Use existing CreateSession/AppendPane, validated font/axis/context and profile-specific cwd handling. Keep pins as settings; opening history starts shells, not arbitrary stored commands. Free IDs survive; collisions across all open windows fold onto new IDs and actual IDs are returned.
- Restore members independently on the UI thread. Roll back partially created failed sessions without recapturing them. Remove successful members only; bind a partially restored workspace entry to its actual destination window/workspace. Retrying into a different or missing destination refuses with actionable error; the entry can be cleared explicitly. An empty closed workspace is recoverable too.
- Add a narrow recent-host interface to the app-level control path, validating raw payload before any mutation. CLI uses the existing reply envelope and local validation; partial workspace restoration is an accepted operation with `outcome: partial`, detailed member errors and remaining count, not a false all-success assertion.

## Risks / Trade-offs

- Disk errors after a successful in-memory restore → expose persistence warning; do not claim rollback. A crash before a successful write can leave a recoverable stale entry; live-ID collision folding prevents duplicate identities.
- History can contain directories and explicit pin commands → local app data only, bounded file, no automatic execution; clear is independently available.
- Large workspace snapshots → evict oldest whole entries to enforce all bounds; an oversized newest entry can itself be evicted with a visible diagnostic rather than silently dropping members.
- Missing profiles/invalid paths → return a member error rather than substituting another shell/profile. Existing cold restore behavior remains unchanged.

## Migration Plan

No existing persisted history exists to migrate. Load the optional new file once per process; absent means empty. Older builds ignore the separate file. No release/live installation is part of this change. Validate and archive C5 before its separate commit, then proceed through C6's own OpenSpec.
