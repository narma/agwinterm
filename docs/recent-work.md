# Recently closed work

`agwintermctl recent list` returns a structured `entries` array, newest first, and an
optional `warning`. Entries have `id`, `kind` (`session`/`workspace`), `name`,
`sourceWindowId`, `sessionCount`, sequence and partial-retry destination fields.
The entry ID is distinct from the session ID. History is shared across library windows
in the same app identity, including entries from closed/deleted windows.

`recent open ENTRY-ID [--window WINDOW]` uses an exact entry ID and an open library
destination (default active). It selects recovered work inside that window without
forcing Windows foreground activation. Session recovery uses its original workspace
if present there, otherwise the active workspace; workspace recovery creates a workspace.
Unknown/ambiguous/closed/quick destinations refuse. Quick terminals do not enter history.

Both panes preserve order, IDs when free, active pane, axis, ratios, font sizes,
profile/cwd, custom/automatic names, flag, context and explicit restore pins. ID collisions
across open windows get fresh IDs; the result reports actual IDs. Missing profiles or
Windows directories refuse that member. WSL uses the saved profile's distribution/user.
Recovery starts shells and retains pins as settings; it does not replay pins, captured
foreground programs or agent bindings. Scrollback and transient surfaces are not saved.

The open result contains `outcome` (`completed`/`partial`), destination IDs, `members`
with original/actual session and pane IDs or an error, and `remaining`. A failed single
session request returns a refusal and retains history. A workspace can partially succeed;
that accepted request exits 0 with explicit member errors. Its entry retains only failed
members and must be retried in the same destination workspace. If that destination is
deleted, clear the stale entry explicitly. Retrying never reopens successful members.

`recent clear [ENTRY-ID]` removes one exact entry or all recent history. It does not
touch live sessions, pins or the separate `restore clear` checkpoint operation. Menu
clear/reopen uses the same store. List/clear accept neither `--window` nor `--target`.
CLI usage errors exit 2; host refusal exits 1, also with `--json`.

`%LOCALAPPDATA%/<app-id>/recent.json` is independent of live restore files and persists
up to 25 session and 25 workspace entries, 1024 total member sessions and 8 MiB. Oldest
whole entries are evicted; surviving workspaces are never silently truncated. Invalid
history is preserved as `.bad` with a readback warning and empty history. Writes replace
the file atomically; a failure is visible while in-memory history survives. A crash before
a write can leave a stale recoverable entry; reopening still checks live identities.
History mutations are queued to an ordered background writer; normal close/reopen/clear
does not wait for disk I/O on the UI thread. Each write takes a cross-process file lock,
loads the current history, and applies only its changes. An unchanged stale entry cannot
resurrect an entry cleared by another process. Control commands refresh through the writer
before inspecting history; menus show the latest local snapshot. Save failures are reported
in readback and a toast, and pending mutations are retained for the next attempt. Graceful
last-window shutdown allows up to 15 seconds to drain; a forced kill can lose pending writes.
The file can contain directories and pin text; `recent clear` queues removal of stored history.
