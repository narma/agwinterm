## Manage sessions & workspaces
Automatic names follow the focused pane's OSC program title, then its cwd basename.
OSC 0/2 names the terminal; OSC 7501 `title` names an agent row and does not rename
the session. Agent rows use their own pane's title, even when another split is focused.
`tree` reports `customName` (null for automatic labels) and `paneCwds` (pane ID to directory).
Directory paths may repeat; use pane IDs as targets. Do not rename or clear names unless
the user asks: explicit names pin the label and stop it from following the program title.
Set or clear `session context` only at the user's request. Omit secrets.
Nested agents must not change their parent's labels. To unpin a name at the user's request,
use `agwintermctl session rename --clear [--target ID]`.
In WSL the CLI uses Windows interop. If the sandbox blocks it (e.g. UtilBindVsockAnyPort),
retry through normal tool approval when available; never bypass a denial or disable the sandbox.

- Opt-in WSL live panes expose `liveSessions` in the tree. Rust stays the PTY host; ZMX is Linux-only.
  `zmx list --profile NAME --json` discovers exact daemon/incarnation tokens; `zmx attach --profile NAME --daemon NAME --incarnation TOKEN`
  connects existing work. `zmx retry|detach|lead --target PANE_ID` requires an explicit exact pane ID.
  Retry never creates work; detach preserves it; lead explicitly claims terminal size ownership.
  `zmx terminate --target PANE_ID --force` ends the exact owned workload for every client.
  `zmx restart --target PANE_ID --source shell|agent|pin|captured` requires confirmed loss and one available source.
  Read back pending operations in tree. Never resend uncertain mutations automatically.
  Managed `session text`/`surface cursor` read the daemon. Managed `session type` acknowledges queue acceptance,
  not execution completion; lost acknowledgement is uncertain. Readonly/control-byte restrictions still apply.
  No SSH or agterm presentation is delivered by this local development feature.
- `agwintermctl tree --json`                              — list workspaces+sessions (id, name, active, status,
  `statusChangedAt` = epoch SECONDS of the last status write on the pane whose status the node is
  showing — its liveness clock. Every status write to that pane restamps it, including a re-assert of
  the same status, so `now - statusChangedAt` is how long ago that agent last said anything: a large
  age next to `"status":"active"` means the hook died, not that work is still running. In a split
  session the age belongs to the pane whose status won: a write to a pane that LOSES the
  aggregate does not move it, but panes tied at the winning status all do, so two panes both
  `active` report the freshest of the two: the session-level age cannot tell a dead hook
  from a live one beside it, and no verb reports the stamp per pane. Always present, even
  for an idle session that never set one)
- `agwintermctl events [--since CURSOR] [--limit N] [--kind status,overlay]` — bounded event pages. Save run/window/cursor after consuming each page; head is separate, gap means history loss. `events --watch --json` tails UTF-8 NDJSON pages; explicit resume after zero requires --since, --run and exact --window. No automatic reconnect.
- `agwintermctl session wait completed,blocked [--target ID] [--scope session|pane] [--wait-timeout 30] --json` — wait for agent-reported status (not process exit). Session scope aggregates main/split panes: blocked > completed > active > idle. Pane scope can address scratch/overlay/quick. Target defaults to caller; selection changes do not retarget. Exit 0 matched, 3 timeout, 2 usage, 130 cancellation, 1 refusal/transport/protocol failure. Timeout 0 is unbounded; explicit --timeout controls transport independently.
- `agwintermctl session new [--name N] [--cwd DIR] [--workspace ID|--workspace-name NAME [--create-workspace]] [--command "PowerShell code"] [--command-mode powershell|direct] [--profile NAME] [--no-select] [--wait]`
  `--no-select` creates the session in the background without stealing focus or changing the current selection.
  `--command` runs PowerShell code and leaves an interactive prompt. `--wait` is accepted in this mode; the prompt stays open either way.
  — create a session (prints its id). `--command-mode direct` launches executable + arguments using Windows quoting, with no added shell; it refuses `--wait`. An exited standalone pane remains visible without an interactive prompt. Profile/login-shell tabs show an in-terminal exit hold and close when you press Enter.
  `--profile NAME` picks a shell profile (default = Windows PowerShell). `--command` and `--profile` are mutually exclusive.
  `--workspace` is a workspace id (or unique id prefix; `tree --json` lists them), `--workspace-name` its sidebar label
  (case-insensitive). **An unknown workspace is refused** (`ok:false`, no session created) — it is never swapped for the
  active workspace: an unknown id, or an unknown name without `--create-workspace`, is an error that names the value.
  `--workspace-name NAME --create-workspace` creates the workspace when it does not exist and reuses it when it does.
  `--workspace` and `--workspace-name` together are refused; pass one. **Omit both and the session lands in YOUR
  workspace** — the workspace of the pane you run in (the CLI sends its `AGWINTERM_SESSION_ID` as the caller), however
  the user has clicked around meanwhile. Sessions go next to you unless you say otherwise, and `--workspace` is how you
  say otherwise. Only a CLI whose pane the answering window cannot see falls back to the active workspace — the one
  the user last clicked, which moves under you: a script from an unrelated shell, a pane that has since been closed,
  or a pane in ANOTHER WINDOW (with no `--window`, the frontmost window answers, and its host knows only its own
  panes — pass `--window` to reach yours, see "Windows").
- `agwintermctl window new --minimized --name NAME` creates background work and returns effective geometry. `window next|previous|minimize|restore|geometry --target WINDOW` and `window fullscreen on|off|toggle --target WINDOW` control open library windows. `window monitors` lists device IDs; `window move --target WINDOW --monitor primary|DEVICE` bounds placement to its work area. Geometry is physical pixels. Minimize/restore do not request focus; next/previous do.
- `agwintermctl session reveal --target PANE --window WINDOW` selects that pane's current directory in Explorer, converting WSL using its own profile. `--path-only` validates and returns the path without opening Explorer. Missing/ambiguous targets and unresolved directories refuse.
- `agwintermctl session appearance set|get|clear --target PANE` controls persistent pane-local appearance. Set replaces overrides; options: `--color #RRGGBB`, `--text TEXT` (512 scalars/12 lines), `--opacity 0..100`, `--status-color #RRGGBB`, `--status-shape circle|square|diamond`, `--hide-image`. Clear resumes theme/session inheritance; IDs retain appearance through swaps/recovery. ANSI/OSC colors remain authoritative; status meaning is unchanged.
- `agwintermctl keymap list --json` reads effective bindings/leader/reserved keys/diagnostics, distinct from `command list`. `config reload --json` returns changed keys and diagnostics using native live application. `app version --json` compares running versus the on-disk executable at the running path, with explicit unavailable/unknown states; it never updates or restarts. All accept `--window`.
- `agwintermctl session duplicate [ID]` — clone a session (same cwd + shell profile) as a new session (default: active).
- `agwintermctl session restore "<command>" --target PANE` — pin a command to re-run on every restart for that pane; `none` clears.
  The target is mandatory (no active-pane default: a pin outlives whatever is active now; inside a session `AGWINTERM_SESSION_ID`
  is the pane). Replies `{action:"pinned"|"cleared", pane, session[, command]}` naming the pane it landed on — a session
  NAME lands on its focused pane, a session ID on the pane that carries that id while one does, and on the FOCUSED
  pane while none does (see the splits section for when that is) — exactly as `session type` does. Read it back in
  `tree --json` as `restoreCommands`: an object keyed by pane id listing only pinned panes (absent when none).
  An unknown target, or a scratch/overlay/quick pane (never restored), is refused and nothing is pinned.
- `agwintermctl profiles list` — shell profiles (cmd, Windows PowerShell, PowerShell 7, Git Bash, WSL:*, custom); `* ` marks the default.
  Profiles live in `%LOCALAPPDATA%\agwinterm\profiles.json` (auto-seeded from detected shells; edit to add your own — name/command/args/cwd/icon/env); `agwintermctl profiles reload` re-reads it.
- `agwintermctl session select <id>` / `session close <id>`
- `agwintermctl session rename <new-name> [--target ID]` — set a session's custom name (sidebar + title bar).
  It names a SESSION, and a PANE id names the session that pane belongs to — worth knowing, because
  `AGWINTERM_SESSION_ID` is your PANE's id and is what the CLI sends when you pass no `--target`: from
  inside a split pane, a bare `session rename` names the session BOTH panes are in, not your half (a pane
  has no label of its own — the sidebar draws one row per session). Replies `{session, name}` naming the
  session it landed on and the name IN EFFECT, so you can see which one took it; read it back in
  `tree --json` as `name`. A blank name is refused, and so is a target that belongs to no session
  (the quick terminal).
- `agwintermctl session context <text> [--target ID]` — ONE LINE of "what is this pane for", shown dimmed after the
  name in the title bar and the sidebar row and on the palette's second line, where the name has to stay short.
  It survives a restart (and `session reopen`), and `tree --json` reads it back as `context` on the session node
  (absent when none). The value
  is one line: a newline, tab or other control character is REFUSED (a control byte in the title bar is a
  rendering accident, not a context), blank is refused, and more than 200 characters is refused — the ceiling is
  the display budget of the row, not a storage limit. `--clear` removes it (text beside `--clear` is refused);
  `--stdin` takes the text from stdin the way `session type --stdin` does (one trailing newline dropped, an
  embedded one then refused). Replies `{session, context}` with the value IN EFFECT after the write, read off the
  session; the target resolves as `session rename` does (a scratch/overlay cover id lands on the session under it).
- `agwintermctl session seen [--target ID]` — clear a session's unseen-notification badge headlessly
- `agwintermctl session output [--target ID]` — the LAST COMPLETED command's output (FTCS marks;
  pwsh sessions emit them automatically) — cleaner than parsing `session text` yourself
- `agwintermctl sidebar state` — read-back `<visible|hidden> <tree|flagged> <width>`, e.g. `visible tree 220`:
  visibility, view mode and width (DIP) in one call (`ping` reports the app version)
- `agwintermctl sidebar width [N]` — read (no N) or set the sidebar width in device-independent pixels. Replies
  `{width, visible[, applied[, note]]}` with the width ACTUALLY in effect, so compare `width` with what you asked
  for. N outside 120..600 is REFUSED with the range named (nothing moves), never clamped — `sidebar hide` is how
  you ask for no sidebar. A set while the sidebar is hidden is remembered and persisted but not applied
  (`applied:false` + a note); it takes effect on the next `sidebar show`. Persists across restarts.
- `agwintermctl version [--json]` — which CLI binary you just ran (version + its resolved path) and which app
  answered (version + pipe), on two greppable lines, `cli` and `app`. Several agwintermctl.exe can coexist and
  none need be on PATH; this says which one this was. It exits 0 and still prints the `cli` half when no app
  answers, marking the app `unavailable` — that is the case it exists for
- `agwintermctl session go next|prev|first|last|next-attention|prev-attention` — move the active session
- `agwintermctl session move --to up|down|top|bottom`     — reorder within its workspace
- `agwintermctl session move <workspace-id>`             — relocate to another workspace
- `agwintermctl workspace new [name] [--default-dir DIR]` / `workspace default-dir set <path>|clear [--target WS]` / `workspace rename <name> [--target WS]` / `workspace select [--target WS]` / `workspace move --to <dir> [--target WS]` / `workspace delete [--target WS]`
- `agwintermctl workspace go next|prev [--window ID]` (also `--to`, alias `previous`) wraps visible workspaces, including collapsed ones; refuses flagged mode, no other destination, quick windows and explicit `--target`.
  An empty destination becomes current without closing the selected terminal. New-session explicit workspace/caller placement still wins over this fallback.
  Tree `foregroundShells` (pane-order, null unknown) and primary/split aliases are conservative Windows shell hints, never idle-prompt or input-safety proof.
- `agwintermctl workspace collapse [WS] [--target WS]` / `workspace expand [WS]` — collapse/expand a single workspace's session list (default: active)
