## Flag sessions & focus a workspace
- `agwintermctl session flag on|off|toggle|clear [--target <id>]` — flag a session (a durable working-set
  mark shown as a flag on its sidebar row; survives moves; persists). `clear` unflags every session.
- `agwintermctl sidebar mode tree|flagged|toggle`        — switch the sidebar between the workspace tree
  and a flat list of just the flagged sessions (great for a curated working set across workspaces).
- `agwintermctl workspace focus on|off|toggle`           — show only the active workspace in the tree
  (hide the rest); a "show all" banner / this verb brings the others back.
- `agwintermctl tree --json` reports `"flagged":true` per flagged session.
- `agwintermctl session switch begin|advance|advance-back|commit|cancel` — drive the MRU (Ctrl+Tab)
  recency switcher programmatically (advance previews the next recent session; commit lands it). The
  interactive equivalent is holding Ctrl and tapping Tab (Shift+Tab walks back, Esc cancels).

## Windows (multi-window)
Each window is independent: its own workspace/session tree + sidebar. Sessions never move
between windows — instead you TARGET a window.
- `agwintermctl window list`                             — id (short), name, whether open + which is active.
- `agwintermctl window new [--name <name>]`              — open a new window (seeded with 1 workspace + 1 session).
- `agwintermctl window select|close|delete|rename <w> [name]` · `window resize <w> W H` · `window move <w> X Y` · `window zoom <w>`
  (`<w>` = a window id, a unique id prefix, or `active`; the last window can't be deleted).
- `--window <id|prefix|active>` targets a specific window on ANY content verb, e.g.
  `agwintermctl session new --window <prefix> --name build` or `agwintermctl tree --window active`.
  Omit it to act on the frontmost window. Your own window is `AGWINTERM_WINDOW_ID`.
- App-global verbs ignore `--window`: `config`, `theme`, `settings`, `keymap`, `install`.

## Custom commands (keymap.conf) & the launcher
Define commands in keymap.conf, then run them by chord (Ctrl+Shift+O palette) or the control API:
Map heads accept alternatives: `map f5 | f7 = next_workspace`, also `map leader a | b = next_workspace`.
An invalid alternative rejects the entire map line. Fixed gestures such as F6 keep precedence; pipes in command bodies are unchanged.
- `command <Label> = <text>`                    — default "send" mode: types the text into the active session
- `command [new] <Label> = <text>`              — run in a fresh session's shell (stays interactive after)
- `command [overlay] <Label> = <text>`          — run in an ephemeral overlay pane over the session
- `command [detached] <Label> = <text>`         — launch an independent OS process (open a URL, external tool)
- Run one: `agwintermctl command run "<Label or raw command>" [--mode new|overlay|detached|send] [--target ID]`
  (a raw command with no matching label defaults to `--mode new`).
  Explicit target supplies pane, cwd, workspace, profile and AGW context. Explicit send bypasses broadcast.
  New selects its newly created session in the target workspace; overlay uses the target session's slot;
  detached launches independently. Target resolution never selects the source. Quick supports send/detached only.
  Omitted target retains active scope, even with AGWINTERM_SESSION_ID set; explicit active uses the routed window.
  Unknown/ambiguous/empty targets and unknown modes refuse. Success accepts input/launch, not child completion.
- List them: `agwintermctl command list`         — tab-separated `label  mode  chord  text`.
- `{AGW_*}` tokens are expanded in the text AND passed as `$AGW_*` env vars to the launched process:
  `{AGW_SESSION}` (name) · `{AGW_SESSION_ID}` · `{AGW_WORKSPACE}` · `{AGW_CWD}` (active pane cwd) ·
  `{AGW_PANE_ID}` · `{AGW_APP}` (path to agwintermctl, for callbacks). Unknown `{AGW_*}` → empty.
- Leader chords (tmux-style): `leader = ctrl+k`, then `map leader b = command:Build`. Press the leader,
  then the follow-up key. Drive/observe it with `agwintermctl command leader state|begin|cancel|key:<chord>`.

## Splits, font, sidebar, theme
A session has at most TWO panes. Each pane has its own id (`tree --json` lists them as `paneIds`). THE SESSION-ID
RULE, by condition and not by verb: a session id names the pane that carries it while one does — pane 0 of a fresh or
reopened session; after a `swap`, either side (a restore keeps the order it saved) — and names the session's FOCUSED
pane while none does. None does once
the carrier was closed: `split close` on it, Ctrl+Shift+W on it, or its shell exiting. Hiding the carrier with
`split off` after a `swap` retains the carrier: its exact id still targets that hidden shell. After a close, a later `split` mints a fresh id, so the state persists
until the session is closed and reopened (a reopened session's pane 0 carries the id again). In a split session,
address panes by their own ids. The split verbs take `--target <id>` as a
session or either of its panes and act on THAT session, not the active one; `focus` and `resize` act on the active session.
- TO GET A PANE, USE `session split on`. The bare form is `toggle`: on a session that is already split it HIDES the split
  and answers the MAXIMIZED pane's id — the focused shell — so a launcher that calls it
  twice types its second command into itself (seen in the field). `on` answers the split pane's id every time, split or not.
  Hiding maximizes the focused pane; showing restores the same order, focus and divider ratio.
  Hidden panes retain their saved state across app restart (focus then resets to primary). Appearance and restore
  commands work while hidden. Existing overlays can be read/closed; opening one requires a visible pane.
  Use `split close` to end a pane, including a hidden one.
- `agwintermctl session split [on|off|toggle] [--axis vertical|horizontal] [--target <id>]` — REPLIES WITH A PANE ID, a bare
  string: `on` = the split pane's id — ALSO when the session was already split (nothing changes; a caller that does not
  know whether it split gets something addressable either way); `off` = the maximized pane's id, also when already
  single; `toggle` = whichever it produced. Default op = toggle. The axis names the ARRANGEMENT, agterm's words: vertical = left/right panes (the default of a session never split), horizontal = top/bottom panes.
  Omitted = keep the session's orientation — remembered for the life of the session, through `off`, and across a
  restart while the split exists, shown or hidden; given on an
  already-split session = re-orient it live. Anything but the two words is refused and nothing is split. `tree --json`
  reports `axis` beside `paneIds` / `focusedPane` / `splitRatios` whenever `paneCount` > 1.
- `agwintermctl session split close [--target <id>]` — close ONE pane, EITHER side (even hidden): a pane id
  closes that pane; a session name or no target closes the session's focused pane (what Ctrl+Shift+W does); from your
  own pane, your pane. Replies with the SURVIVOR's id, which becomes pane 0 with the whole area and the focus. A one-pane
  session is REFUSED — `session close` is the verb that closes a session. Closing the pane that carries the session
  id is one of the ways the session-id rule above flips to "the focused pane": keep addressing panes by the ids
  `tree --json` lists.
- `agwintermctl session swap [--target <id>]` — exchange the two panes. What moves: the pane order (left↔right or
  top↔bottom), the focus (it follows its pane), the two shells' contents and each pane's PANE OVERLAY (the slot is on
  the pane, so it crosses with it: `paneOverlays` reads `["left"]` where it read `["right"]`, and the overlay is
  regridded to its pane's new box). What does not: the axis, the divider (the left/top box keeps its size — the
  contents change places), the session-wide overlay / scratch / quick (they cover the session, not a pane), the
  status, the context, the flag — and EVERY ID, the overlay ids included. A swap moves panes, never ids: the id you hold keeps reaching the same shell, now on the other
  side, and the session id keeps naming the pane it always named. Replies `{session, paneIds, focusedPane, axis}` = the
  tree's split block after the swap. A one-pane session is refused.
- `agwintermctl session focus [primary|split|left|right|top|bottom|other] [--target ID]` — move focus between the panes (default
  `other`, the one word valid on either axis; `primary` = pane 0, `split` = pane 1). `left`/`right` exist on a vertical
  split only and `top`/`bottom` on a horizontal one — the wrong pair is refused naming the axis; so is a one-pane session.
- `agwintermctl session resize [--split-ratio R] [--grow-left N|--grow-right N|--grow-top N|--grow-bottom N] [--target ID]` — move the
  divider: `--split-ratio` is pane 0's share (0.7 = the left/top pane gets 70%); `--grow-left/--grow-right` move a
  vertical split's divider by N columns, `--grow-top/--grow-bottom` a horizontal one's by N rows. The other axis's flags
  are refused and the divider does not move; so is a one-pane session.
  Focus/resize accept session or main/split pane IDs, including after swap; auxiliary panes refuse.
  Background targets change remembered focus/ratio without selection, unseen clearing or recency changes.
  Omitted targets retain active scope (ignore caller environment); --window bounds explicit target resolution.
- `agwintermctl font inc|dec|reset [--target <id>]`      — font zoom; target a session (active pane) or a specific split/scratch/quick pane by its id (see `tree` paneIds)
- `agwintermctl dashboard [<id> ...] [--close] [--font-size N]` — grid overlay of live sessions (no ids = most-recent; `--close` dismisses; Ctrl+Shift+D toggles it in the UI)
- `agwintermctl sidebar show|hide|toggle|expand|collapse` (`on`/`off` are aliases of show/hide). An op the sidebar
  cannot do is REFUSED (`ok:false`, nothing changed) — it used to answer `ok` and do nothing. `sidebar width [N]`
  reads/sets the width (see "Manage sessions & workspaces").
- `agwintermctl session background set <image> [--opacity 0..100] [--mode fit|fill|center|tile]` — a faint per-session
  watermark drawn behind the terminal (the image is copied into app data); `session background clear` removes it.
  Per-session (honors `--target`/`--window`); persists; `tree` reports `"background":true`.
- `agwintermctl theme list` · `agwintermctl theme set "Solarized Light"`
  Bundled curated themes — dark: Dracula, Tokyo Night, Catppuccin Mocha, Gruvbox Dark, Nord, One Dark, Solarized Dark;
  light: Solarized Light, Catppuccin Latte, GitHub Light. Drop more ghostty-format files in %LOCALAPPDATA%\agwinterm\themes\.
