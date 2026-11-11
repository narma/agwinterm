# Startup diagnostics

GUI and host write automatic JSON-lines timing journals under
`%LOCALAPPDATA%\agwinterm\logs` (or the selected app-id directory).
Files are named `startup-gui-<UTC>-<pid>.jsonl` and `startup-host-<UTC>-<pid>.jsonl`.
Each role keeps its ten newest files; each file is capped at 4096 records and
approximately 1 MiB. Logging failures do not abort startup.

Records contain UTC, process ID, elapsed milliseconds since journal creation,
stage, optional opaque window/session/pane ID, and duration for completed spans.
No command lines, working directories, environment or terminal contents are recorded.
`process.enter` identifies the assembly version. An `.end` record means a scope
finished (including exception unwinding), not necessarily that it succeeded.

Compare `config.loaded`, `themes-keymap.loaded`, `fonts.ready`,
`window-index.loaded`, `window.first-paint`, `window.sessions.begin/end`,
`window.restore.begin/end`, `session.restore.begin/end`, and `message-loop.ready`.
`host.restore-inventory.begin/end` measures the single inventory used by each
restored window; `pane.adopt.begin/end` identifies actual synchronous reconnections.
The host records `host.listening`. `pane.first-output` records the first observed
output callback, **not shell prompt readiness**; adopted panes may already have output.
The timer begins in managed startup, so it excludes process loader/runtime startup.

The GUI shows and paints its frame before connecting/restoring sessions, and
repaints restoration progress between saved sessions. Restoration still contains
synchronous host calls: earlier visibility does not imply full interactivity
until `message-loop.ready`. It does not dispatch arbitrary user input against a
partially restored session tree.

New hosted panes use the current pane/theme colors before their first owner
frame arrives, including while WSL is starting. Placeholder defaults are not
treated as resolved black RGB; real owner frames retain their exact colors.

Restore inventories the host before starting missing panes. Only IDs in that
inventory attempt synchronous adoption; an inventory failure falls back to the
ordinary per-pane check. Ticketed live bindings retain their exact adoption path.
Progress paints are throttled to at most ten per second during restoration;
forcing a synchronous paint for every pane can otherwise add a display-frame
wait per session even when session construction itself is fast.

New shells await a per-backend launch slot asynchronously before entering the
shared control connection, so large restores do not park a pool worker per pane
behind an already serialized pipe. Closing a queued pane still prevents its spawn.

## Local profile measurement, 2026-11-11

Measured on the installed .NET 11 / ReadyToRun / runtime-async build. These are
individual observed launches, not a statistical benchmark. The earlier launch
restored 45 sessions; the optimized profile contained 46 sessions / 47 panes.

| Managed-start milestone | Before | Optimized cold host | Existing host |
| --- | ---: | ---: | ---: |
| Initial window paint | 1.019 s | 0.800 s | 0.404 s |
| Restore finished | 15.123 s | 1.745 s | 0.711 s |
| Message loop ready | 15.139 s | 1.806 s | 0.771 s |

The optimized cold run observed first output/frame callbacks for all 47 panes
by 11.441 s; this is not a shell-prompt readiness measurement. Shell launches
continue after the interface is usable. The warm run attempted 47 adoptions
and retained the host and its existing children.

Evidence: `artifacts/startup-performance/profile-final-cold.jsonl` and
`profile-final-warm.jsonl`. Both warm and cold Hyper-V restore checks passed,
as did 14 focused queue/lifecycle/startup-claim tests. The UI checks validate
session identities, actual terminal input, early paint, and inventory/adoption
counts; warm setup waits for real attachments before restarting the GUI.
