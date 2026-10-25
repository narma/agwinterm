# The control API

agwinterm is scriptable through a local named pipe speaking newline-delimited JSON, with
`agwintermctl` as the CLI wrapper. `agwintermctl --help` lists the core verbs and the agent skill
(`agwintermctl install skill`) carries the full set; this page collects the
replies a script can rely on, and the ones that answer a question a script would otherwise have to
guess at.

Related pages: [launching a session command](session-commands.md), [session HUD](session-hud.md),
[workspace navigation](navigation.md), [quick terminal](quick-terminal.md),
[native picker](native-picker.md), [`image frameshm`](specs/image-frameshm.md). The subset agliteterm
shares is canonical in [`tests/conformance/control-api.json`](../tests/conformance/control-api.json).

## Reaching an instance

Ordinary requests exit 0 on success, 1 on server refusal, transport failure or invalid
reply, and 2 on invalid CLI arguments. `--json` preserves the server reply on stdout,
including `ok:false`, but does not turn a refusal into exit 0. This corrects older CLI
versions that returned 0 for every JSON reply. Offline `version` and picker outcome
codes retain their documented exceptions. A reply timeout does not prove rollback.

Unsupported options, missing values, invalid numeric values and duplicate selectors
are refused before connecting or installing anything. Choose one `--target`, one
`--window`, and either `--pipe` or `--socket`; repeated targets are not batch operations.
Boolean flags do not consume the positional text following them.

Inside a session you get `AGWINTERM_SESSION_ID`, `AGWINTERM_WINDOW_ID` and `AGWINTERM_PIPE`, so a
bare `agwintermctl` targets the instance and pane it runs in. `--pipe <name>` addresses another one
(a Debug build is `agwinterm-dev`). Most content verbs default to `$AGWINTERM_SESSION_ID`; focus,
resize and command run preserve active scope as described below. Run
`agwintermctl install skill` (or the palette entry) to teach Claude Code / Codex the full verb set.

The CLI waits at most 30 s for the app's reply and then exits 1 with "no reply from agwinterm within
30 s" (the request may still have been carried out). `--timeout <seconds>` changes the limit and
`--timeout 0` removes it. `session overlay open --block` answers when its overlay closes, so it waits
without a limit unless `--timeout` is given. A hook or script that runs `agwintermctl` under its own
deadline should pass a shorter `--timeout`, so the CLI gives up before the runner abandons it.

## A tour

```powershell
agwintermctl tree --json                     # workspace/session tree (+ splits, badges, overlays, paneOverlays)
agwintermctl window state                    # sidebar/fullscreen/active read-back
agwintermctl sidebar width 300               # move the divider; the reply is the width in effect
agwintermctl session split on --axis horizontal   # stack the panes; the reply is the split pane's id
agwintermctl session split close --target <pane>  # close EITHER pane; the reply is the survivor's id
agwintermctl session swap                    # the two panes change places; every id stays where it was
agwintermctl session status blocked --sound  # report agent status (a dot + bell in the UI)
agwintermctl notify "build finished" --category ok  # green unread badge when the session is not in view
agwintermctl session new --name build        # from a pane: lands in THAT pane's workspace, not the last-clicked one
agwintermctl session new --name build --workspace-name CI --create-workspace
agwintermctl session new --workspace no-such   # refused, no session: an unknown workspace is never swapped for the active one
agwintermctl session type "npm test`n"       # type into the active session
@"
say "hi"
"@ | agwintermctl session type --stdin   # text with quotes/newlines: stdin as bytes (see below)
agwintermctl session overlay open "git diff" --size-percent 60
agwintermctl session hud "Reviewing" --spinner --position top-right  # passive status, shell stays usable
agwintermctl session hud update "Ready"                             # replace message in place
agwintermctl session hud close
agwintermctl session overlay open "lazygit" --pane right    # over the right pane only; the left pane stays live
agwintermctl session overlay text --pane right --all        # the overlay's own screen + scrollback (session text reads the shell under it)
agwintermctl session restore "npm run dev" --target <pane>   # re-run on every restart; reply names the pane
agwintermctl restore capture                 # capture every pane's running command into its restore slot NOW, not only at quit
agwintermctl session rename api              # the custom name in the sidebar and title bar
agwintermctl session context "reviewing PR 226, left pane is the diff"   # one line of what the session is FOR; survives a restart
agwintermctl dashboard build test deploy     # grid overview of chosen sessions
agwintermctl theme set "Tokyo Night"         # retint the whole window
agwintermctl window new --name scratchpad    # open a second window
agwintermctl surface cursor --target <pane>  # the caret column of a pane, as a bare integer
agwintermctl version                         # which CLI ran, and which app answered the pipe
```

## Notification categories

`agwintermctl notify "message" [--title "Title"] [--category ok|normal|attention]` sends a
notification to a session. Raw control requests use `{"cmd":"notify","args":{"body":"message",
"category":"ok"}}`. The category is optional: omitted notifications and OSC 9/777 notifications
remain `attention` (red) for compatibility. Unknown values are refused without delivery. An older
server ignores the optional field and still displays a red badge, so scripts that depend on the
new colors should check the serving app version first.

The unread count remains the total number of messages. Its color reflects the highest-priority
unread category: `attention` red, then `normal` yellow, then `ok` green. Visiting the session or
running `session seen` clears the count and category. A notification in the currently focused pane
does not add an unread badge while the window is active. The agent-status circle is independent.
The three badge colors can be changed in Settings → Notifications or with `agwintermctl config set
notification-color-ok "#RRGGBB"` (likewise for `notification-color-normal` and
`notification-color-attention`). This changes only presentation, not category priority or the count.

## `surface cursor`

Returns the caret **column** and nothing else, so "is that agent's composer empty before I type into
it" is a comparison, not a hunt for its placeholder text. A different column is proof of a draft; the
same column is not proof of none (a draft exactly one wrap width long parks the caret where it
started), so back a match with `session text` of that row. It resolves its target exactly as
`session text` and `session type` do, so the pane you check is the pane you type into.

## `session text --styles`

`agwintermctl session text --styles [--all|--lines N] [--target ID]` reads text and its
rendering attributes. The wire request is `session.text` with `args.styles: true`.
The CLI prints the result object below; adding `--json` keeps the usual `{ok,result}` envelope.
Omitting `styles` or setting it to false keeps the existing plain string result unchanged.

```json
{"cols":80,"rows":[{"row":0,"runs":[{"col":0,"width":2,"text":"> ","faint":false,"bold":false,"italic":false,"underline":false,"inverse":false,"strike":false,"fg":"default","bg":"default"},{"col":2,"width":10,"text":"suggestion","faint":true,"bold":false,"italic":false,"underline":false,"inverse":false,"strike":false,"fg":"default","bg":"default"}]}],"cursor":{"row":0,"col":2}}
```

Rows and columns are zero-based grid coordinates. Row 0 is the top of the visible screen;
negative rows are scrollback (`-1` is the newest history row). Each run's `col` and `width`
describe its grid extent, including the second cell of a wide glyph; spacer cells add no text.
UTF-16 length or Unicode character count is **not** its width. All six flags are always present;
`faint` is SGR 2 (dim), and `inverse` is reported as stored, without swapping foreground/background.
Colours are the program's specifications, independent of the current theme: `default`, `idx:N`
(palette index 0–255), or lowercase `#rrggbb` for truecolor. Adjacent cells with identical
attributes and colour specifications merge into one run.

The cursor and runs come from one locked snapshot of the pane's emulator (the local replica for
a server session). The cursor column can equal `cols` while wrapping is deferred. This is a
text-oriented attribute view: all trailing Unicode whitespace, including styled spaces and
U+3000, is trimmed just like plain `session text`. Trailing empty rows are omitted; interior
empty rows have `runs: []`. Joining run texts per row, then rows with newlines, reproduces the
plain dump. A cursor on a trimmed-empty row is still returned, even when that row is omitted.

Default selection is the visible screen. `--lines N` selects the last N rows ending at the
screen bottom, reaching into history when needed (`0` means the screen); `--all` includes all
history. `--all` with `--lines` is refused. Restored history seeded from plain text is marked
dim by the emulator, so these restored rows also report `faint: true`.

For a known single-line composer that renders suggestions with SGR 2, find the cursor's row
and select runs where `run.col + run.width > cursor.col`. If all those runs are faint (or the
row has no text), the suffix is consistent with an empty composer showing a suggestion.
A non-faint overlapping run distinguishes a real draft, including one whose caret moved back
inside that run. This is an application-specific heuristic: verify the composer and any text
before the caret, and handle its cursor decoration if it uses inverse video. Grey text encoded
with a palette/RGB colour instead of SGR 2 needs that application's colour convention; colour
alone is not proof of an empty composer. The snapshot does not reserve the pane against later input.

Targeting is unchanged, including quick-terminal and overlay **ids**. `session overlay text
--styles` is refused before reading; use `session text --styles --target <overlay-id>`. agliteterm implements
the same reply over its own row selection (see [parity tracker](lite-parity.md)).

## `statusChangedAt` in `tree --json`

Each session reports `statusChangedAt` — epoch **seconds** of the last status write on the pane whose
status the node shows, restamped even when the same status is re-asserted. `now - statusChangedAt` is
how long ago that agent last spoke, which is what separates a working agent from one whose hook died.

In a split session it is the clock of the pane whose status won: a write to a pane that loses the
aggregate does not move it, but panes tied at the winning status all do. Two `active` panes report
the freshest of the two, so the session-level age cannot tell a dead hook from a live one beside it
(nothing reports the stamp per pane).

## `version`

Prints two greppable lines: the `cli` that ran (version and its resolved path — several
`agwintermctl.exe` can coexist off `PATH`) and the `app` that answered (version and pipe). It exits 0
and still prints the `cli` line when nothing is listening, which is the case it exists for. `--json`
gives the machine-readable form.

## `session type --stdin`

Takes the text from standard input as bytes, so quotes, newlines, runs of spaces and a leading `--`
arrive intact: the argv form re-joins positionals with one space and the option parser eats a leading
`--`, both silently.

- Exactly one trailing newline is dropped (the one the shell adds), so end with two to press Enter.
- Invalid UTF-8 is refused with its byte offset and nothing is sent.
- `--stdin` beside positional text or `--select` is refused as ambiguous.
- There is no `quick type` verb: the quick terminal is a pane whose id starts with `quick:`, so it is
  `session type --stdin --target quick:`.

`session write` is display-only: the program's next repaint, or any pane resize, paints over what it
wrote.

## `session overlay`

C2 adds `hide`, `show` and `toggle`, with the same `--target` and `--pane left|right`
slot selection as open/close. Hiding keeps the process, ID, buffer and exit result alive.
Ordinary input reaches the underlying surface; explicit overlay-ID reads/writes still
reach the hidden program. Empty slots refuse. A background show does not select its
session. A hidden `--wait` banner is retained until shown and dismissed or explicitly closed.

Session-wide open/resize accepts `--width N` and `--height N` (integer Windows DIP,
1..16384). Each overrides its axis of `--size-percent 1..100`, or full size when no
percentage was set. Partial resize retains omitted settings. An explicit percentage
resets DIP overrides before applying dimensions in that request. `resize --full`, or
legacy bare `resize`, resets all sizing; `--full` conflicts with other sizing flags.
Pane overlays remain full-pane and reject resizing, including via their overlay IDs.
Effective dimensions are centered and clamped to the content bounds; requested values
survive window resizing and DPI changes.

Open also accepts `--cwd PATH`, `--follow` and `--background-color '#RRGGBB'`.
Cwd uses the owner's Windows/WSL profile and directory rules. Follow selects the owner
session and pane after opening, without promising OS foreground activation. Background
color is local to that overlay; explicit terminal colors retain precedence. Invalid
options refuse before replacing a program. Wire keys match CLI names, with JSON integers
for dimensions and booleans for follow/full.

`tree --json` adds `overlayDetails` for occupied slots: `id`, `slot` (session/pane),
`ownerPaneId`, `hidden`, `visible`, `exited`, and `geometry` (`x`, `y`, `width`, `height`
in client pixels; `percent`, `widthDip`, `heightDip` requested sizing). Background or
occluded slots remain present with `visible:false`. Existing presence/result fields
retain their meanings. C1 events add overlay actions `hidden`, `shown`, `resized` with
stable owners; idempotent visibility requests do not fabricate changes.

Examples: `session overlay open "cmd /d" --width 500 --height 300`,
`session overlay hide --target OVERLAY_ID`, `session overlay show --target OVERLAY_ID`,
`session overlay resize --full --target SESSION_ID`.

`open --size-percent N` and `resize --size-percent N` take 1..100 and **refuse** anything else,
naming the value and the range. `0`, `150` and `sixty` used to open a full-screen overlay and report
success; now nothing opens and `ok` is false. Omit the flag for the full content region.
`resized N%` is always the N that was asked for.

The verb's other failures are refusals too:

- `open` with no command;
- `open` and `resize` whenever no session resolves (a `--target` that matches nothing, or no target
  and no active session);
- a `close` whose `--target` names nothing;
- `open`, `close` and `resize` without `--pane` whose `--target` names one pane of a split session (a
  session-wide overlay covers the whole session — the refusal names the session id to pass instead,
  and `--pane left|right` is how one pane is named);
- `resize` with no overlay open.

The pane form has its own, each starting with agterm's phrase:

- `pane not visible` (`--pane right` on a one-pane session);
- `pane overlay already open` (a pane slot never silently replaces — the session-wide slot still
  does);
- `no overlay` (`copy` / `text` / `result` on an empty slot), `no selection`;
- `overlay still running` / `no overlay result` (`result --pane`);
- a `--pane` word other than exactly `left`/`right`, a `--target` pane id naming the other side than
  `--pane`, `--pane` with `--size-percent`, and `resize --pane` are refused with nothing sent (a pane
  overlay is always full-pane).

Not refusals:

- `close` stays `ok` when the session resolves and has no overlay, or when the target is absent,
  empty or `active` while nothing is active — there is nothing to close, and nothing is what you
  asked for.
- A `resize` whose reply says the window did not run it within 15 s is still queued and may land
  later.
- `open --block` answers the outcome of the overlay it opened: `closed` when that overlay was closed
  or replaced first, `exit 1` also when its program could not be started at all, and `ok:false` with
  the status unknown when the window closed under it. A blocking open closes its pane as it replies,
  so to tell a program that could not start from one that ran and failed, open with `--wait` instead,
  poll `overlay result` for its `exit N`, read the pane by the id the open returned, and close it by
  that id — a close whose overlay is already gone is refused, which is the same end state.

`overlay result` stays one value per window, written by whichever session's session-wide overlay
exits next. `result --pane left|right` (or `result --target <pane overlay id>` while that overlay is
up) is that slot's own `exit N`, and a pane overlay's exit never writes the window-wide value.

## `session restore`

Replies `{action, pane, session}` instead of the word "pinned".

- `pane` is the pane the target resolved to: a session name lands on its focused pane; a session id
  on the pane that carries that id while one does (pane 0 of a fresh session, either side after a
  `session swap`), and on the focused pane while none does, i.e. once the carrier was closed by any
  path — exactly as `session type` does.
- `action` is `pinned` or `cleared` (`none` clears).
- The target is mandatory, because a pin outlives whatever pane is active now.
- `tree --json` reads the pins back as `restoreCommands`, an object keyed by pane id that lists only
  pinned panes.

## `session.bind` from a SessionStart hook

For a WSL profile, `pid` is a Linux PID in that profile's distribution and user context. The host reads `/proc` there while the hook is alive, verifies `AGWINTERM_SESSION_ID`, and never looks up that PID in the Windows process table. The CLI accepts `session bind <agent> --resume <id> --pid <pid> --cwd <Linux path>`.

`install hooks` makes the `SessionStart` hook of Claude Code, Codex and Devin send
`{"cmd":"session.bind","target":"<pane>","args":{"agent":"claude|codex|devin","resume":"<session id>","cwd":"<dir>","pid":<hook pid>}}`.
Devin's event has no `cwd`, so its hook sends `DEVIN_PROJECT_DIR`, the project root Devin runs in.
The reply `binding` means the report was well formed and the pane exists. The rest happens after it:
agwinterm walks the process tree up from `pid` to the agent, refuses a run nested under another agent
(a `claude -p`, `codex exec` or `devin -p` started from an agent's tool shell inherits the pane's id),
refuses a `devin -p` run in the pane itself (and a Devin whose command line cannot be read), keeps the
agent's permission and sandbox flags, and stores the resume line for the pane's shell. The hook waits
for the reply because the walk starts at its own process.

- The outcome is a `bind` event in `events`: `bound: <the stored line>` or `ignored: <why>`, with the
  processes the walk visited.
- An unknown agent, a session id that is not 1-128 letters, digits, `-` or `_`, or a missing `pid` is
  refused and binds nothing.
- `session.bind` with `agent` alone keeps its old meaning: that string is the relaunch command, and
  `none` clears it.

## `restore capture`

WSL panes use the Linux terminal's foreground process group, scoped by pane identity; Windows panes continue to use the Windows process snapshot.

`restore capture [--target ID]` fills the captured-command slot of every real pane (or of one)
**now** and saves, and replies per pane with what it found. Before this verb the capture ran exactly
once, on a clean quit, which is the one exit a crash, a `Stop-Process`, a power cut or a missed update
never reaches, so the restart that most needed the command was the restart guaranteed to have none.

- A pane's `captured` is the command line its shell is running, or `null` when the shell has no child
  worth restoring (the shells on `restore-denylist.conf` never count). Null is written too: a fresh
  capture replaces an older checkpoint.
- The reply's `replayOnRestore` is the `restore-commands` setting, off by default. The capture always
  happens; the typing-back at restart only happens when that is on, so a script that wants the replay
  checks the flag rather than assuming.
- It takes one process query for all panes, so allow seconds, not milliseconds.
- An unknown target, an empty one (`--target ''` — only an OMITTED target means every pane), a
  scratch/overlay/quick pane, or a query that fails is refused and nothing is written.
- One refusal leaves something behind and says so — "captured into memory but the state file could
  not be written": the slots are filled (`tree` shows them), but this save did not put the new
  checkpoint on disk. An earlier checkpoint may remain; fix the state directory and capture again.
- `tree --json` reads the slots back as `capturedCommands`, keyed by pane id like `restoreCommands`.

## `session context` and `session rename`

Session labels use an explicit name first, then the focused pane's OSC program title, then its
current/launch directory basename. The title bar uses the same explicit-name/program-title rule
and retains its full-path directory fallback. Bare executable/path titles and control characters
are ignored. `/` and Windows drive roots remain recognizable; an unknown directory shows `~`.
Shell integration enables cwd updates after `cd`. Explicit names stay pinned.

`tree` includes `customName` (null for automatic labels) and `paneCwds`, an object mapping each
normal pane ID to its live/launch directory (null if unknown), including single-pane sessions.
Split panes retain independent entries. Directories are data, not unique identifiers; target by ID.
Legacy host adapters may omit these fields; missing `customName` does not mean an automatic label.

The Claude/Codex SessionStart hook asks the agent to set only a one-line purpose with `session context`.
It must not rename or unpin the session without a user request: pinning would stop automatic OSC
title updates. Context changes only when the purpose changes; user-provided context is preserved.

`session context "<text>"` sets one line of **what a session is for**, shown dimmed after the name in
the title bar and the sidebar row and on the session palette's second line, where a name has to stay
short. It survives a restart and an undo-close, and `tree --json` carries it as `context` on the
session node, so an agent that sets it when it starts a task leaves a note every other agent can read.

- It is one line by rule: a newline, tab or other control character is refused rather than drawn (the
  control-byte class from #213), blank is refused, and more than 200 characters is refused because
  the ceiling is the width of the row, not a storage limit.
- `--clear` removes it, and `--stdin` takes it as bytes the way `session type --stdin` does.
- The reply `{session, context}` is the value **in effect** after the write, read off the session
  rather than echoed from the request.

`session rename <name>` is its neighbour: the short custom name, same target resolution, and the two
survive each other (renaming does not clear the context).
`session rename --clear` removes the pinned name and resumes following the program title/directory.
Its reply is `{session, name}` with the automatic name now in effect; `tree.customName` becomes null.
The API spelling is `session.rename` with `args: {"clear": true}`. A name together with clear is refused.

## `session split` and `session swap`

`session split` answers the **pane id** it produced instead of the word "split", so the shell you just
asked for is addressable from the reply: `on` on an already-split session answers the existing split
pane's id and changes nothing, `off` answers the survivor's.

**To get a pane, use `on`.** The bare form is a toggle: on a session that is already split it closes
the split and answers the survivor's id — the session's own shell — so a script that asks twice ends
up addressing the pane it runs in.

- `--axis vertical|horizontal` picks the arrangement in agterm's words — vertical is left/right
  panes, horizontal is top/bottom. It is remembered for the life of the session, through `off`, and
  across a restart only while the session is still split (a collapsed session writes no axis key);
  `tree --json` carries it as `axis` on a split session.
- `session split close --target <pane>` closes **either** pane and answers the survivor's id (`off`
  can only keep pane 0); a one-pane session is refused, because `session close` is that verb.
- `session swap` exchanges the two panes and keeps the axis, the divider position, the focus's pane
  and **every id**: a swap moves panes, never ids, so a handle you hold keeps reaching the same shell
  on the other side. Its reply `{session, paneIds, focusedPane, axis}` is the tree's split block after
  the swap.

## `sidebar width`

`sidebar width [N]` reads or sets the sidebar width in device-independent pixels and replies
`{width, visible, applied}` with the width **actually in effect**, so a script compares what it asked
for with what it got.

- Outside 120..600 is refused with the range named and nothing moves (`sidebar hide` is how to ask for
  none).
- A set while the sidebar is hidden is remembered and persisted but reported `applied:false` rather
  than as a width nobody can see.
- `sidebar state` reads `visible tree 220`: visibility, mode and width.
- A `sidebar` op the app cannot do is refused instead of acknowledged (`on`/`off` are real aliases of
  `show`/`hide`).

## `session new` lands beside its caller

`session new` with no `--workspace` creates the session **in the caller's own workspace**: the CLI
sends the pane it runs in (its `AGWINTERM_SESSION_ID`), and the session goes next to it. Before, it
went to the *active* workspace, a global the UI moves on every click, so an agent creating several
sessions scattered them wherever the user had last clicked. An agent gets sessions beside itself
unless it says otherwise, and `--workspace` / `--workspace-name` are how it says otherwise.

Only a CLI with no pane identity, or one whose pane has since been closed, still lands in the active
workspace; a stale caller is not refused, because that would break a working script.

## Explicit targets for focus, resize and custom commands

`session focus`, `session resize` and `command run` accept `--target ID` and `--window ID`.
For these three operations, an omitted target preserves the legacy active context in the routed
window, even when `AGWINTERM_SESSION_ID` names another pane. Explicit `active` selects that same
context. Other commands retain their documented caller defaults.

Explicit window selection bounds resolution. Empty, unknown, ambiguous or incompatible targets
refuse without falling back to the visible session. Resolution occurs on the UI queue, so a target
removed before execution is refused. Exact pane IDs win over session IDs; prefixes must be unique.

Focus and resize resolve a main/split pane to its owning session, including after swap. Auxiliary
panes and single-pane sessions refuse. Background focus changes remembered focus; resize changes
that session's divider. Neither selects the source, raises its window, clears unseen notifications
or changes recency. Axis restrictions, ratio clamps and integer row/column growth are unchanged.

```powershell
agwintermctl session focus other --target <background-pane-id> --window <window-id>
agwintermctl session resize --split-ratio 0.3 --target <background-session-id>
agwintermctl command run 'echo {AGW_PANE_ID}' --mode send --target <pane-id>
```

Named and raw custom commands use the addressed pane's cwd and identity, owning workspace and
profile for `{AGW_*}` tokens and launched-process `AGW_*` environment. Named commands keep their
configured mode; raw commands default to `new`. Supported modes are `send`, `new`, `overlay` and
`detached`; unknown modes refuse. Explicit `send` writes only to the addressed pane even when
broadcast is enabled, and refuses readonly/input-closed destinations. Untargeted send keeps the
existing human broadcast behavior. New mode creates and selects its new session in the source
workspace/profile context; overlay uses the source session's overlay slot and addressed cwd;
detached launches independently. None temporarily selects the source. Quick retains send/detached
only. Windows launch conventions remain PowerShell for new and cmd for overlay/detached; WSL
retains its selected distribution/user and Linux cwd. Success means accepted input or launch,
not program completion; write/timeout failures do not imply rollback or safe retry.

These explicit-target guarantees require a serving Win32 build with C0 support. An updated CLI
cannot make an older server honor fields it previously ignored. The shared Lite contract is unchanged.

## Event pages (C1)

`agwintermctl events --since 0 --limit 100 --kind status,session-status --json`
reads one bounded page in the selected window. `--run ID` checks process identity.
The wire command is `events`, with `args.since` (nonnegative Int64), `limit`
(0 or omitted = 1000; otherwise 1..1000), `kinds` (optional unique array of
known event types), and `run` (optional nonempty string). Invalid arguments refuse.

The result contains `schema:1`, `run`, `window`, `oldest`, `head`, `cursor`,
`gap`, `more`, and `events`. Each event retains `seq`, `type` and existing
`session`/`info` fields and includes its owning window. Save the tuple
`(run,window,cursor)` only after processing a page. A limited page's cursor
now stops at its last scanned event, fixing the previous behavior that could
skip events; use `head` when the journal head itself is needed. Filters do
not renumber events and advance over excluded records. `more` means unscanned
history remains, which might contain only excluded types.

History retains at most 1000 records, each at most 8 KiB serialized UTF-8.
Free text may be shortened with `truncated:true`; identity is preserved.
`gap:true` means `since < oldest-1`, including explicit zero after retention
has wrapped. The retained suffix is returned but is not a complete history.
An empty journal has oldest 1 and head/cursor 0. No history survives restart.
`run-mismatch` and `cursor-ahead` are explicit error codes. Legacy polling
without a run remains supported but cannot detect restarts. External clients
must preserve Int64 integers exactly (including clients using JavaScript).

Wire `tail:true` requests an empty baseline at the sampled head and conflicts
with any explicit `since`. These metadata guarantees require a C1 server.

### Continuous observation and connection lifetime

`events --watch --kind status,session-status --json` starts at a tail baseline.
`events --watch --since 25 --run RUN --window EXACT_WINDOW --json` resumes an
explicit identity. The initial active window is resolved once; later changes
to the frontmost window do not redirect the watch. Explicit `--since 0`
replays retained history and fails if that history has a gap.

JSON watch emits one flushed ordinary envelope per line, including the initial
baseline and empty heartbeat pages. Plain output emits tab-separated
`seq type window sessionId-or-- paneId-or-- info-as-JSON`; resume metadata is
on stderr. A gap emits its suffix envelope in JSON, diagnoses the gap and exits
1; plain output does not print a misleading continuous suffix. Transport,
protocol, identity change and refusal also exit 1. Ctrl+C exits 130. No
automatic reconnect occurs; checkpoint only consumed pages and deduplicate
by run/window/seq if a consumer's own output checkpoint is uncertain.

Wire `args.wait-ms` accepts integer 0..25000, default zero. It returns available
matching events immediately, otherwise waits for a match or deadline; excluded
events advance the safe cursor without waking the client. Each pending request
owns its connection until its one response; do not pipeline another request.
Ordinary sequential exchanges remain supported. EOF, cancellation, window
closure and server shutdown release pending observers. Long polls and status
waits share a limit of 128 pending observers per window (`observer-limit` on
excess). Reply writes and waiting never hold the UI or journal lock.

### Lifecycle payloads

Every record carries `seq`, `type`, `window`, plus applicable exact `sessionId`
and `paneId`. Legacy `session`/`info` retain their meanings: `status.session`
is still a pane ID. New records describe committed changes; refusals and
unchanged operations do not generate false changes.

| Type | Actions | Additional fields |
| --- | --- | --- |
| selection | selected, cleared | previousSessionId; new sessionId when selected |
| focus | changed | previousPaneId, paneId |
| split | opened, closed, axis-changed, resized, swapped | paneIds in slot order, focusedPane, axis, ratios when split, removedPaneId on close |
| scratch | opened, shown, hidden, closed | paneId |
| overlay | opened, exited, closed | overlayId, slot=session or pane, ownerPaneId for pane slot, exitCode on exit, reason on close |
| hud | opened, updated, closed | slot=session, reason on close; no message body |
| status | changed | status, statusChangedAt, blink, autoReset; legacy session/info preserved |
| session-status | changed | status, previousStatus, winningPaneId, statusChangedAt |
| session | created, closed | sessionId and legacy session/info |

Close reasons are `closed`, `replaced`, or `owner-removed`. Overlay IDs remain
stable through slot swaps; a late callback from a replaced process cannot
report an exit for its replacement. Pane status writes carry immutable
snapshots in serialized write order, and session aggregation uses the same
priority as the tree. Same-status flag changes produce pane status events but
not false aggregate status transitions. No terminal buffers, process environments
or new command/message bodies are recorded. Legacy bind info remains compatible.

### Status waiting

`session wait completed,blocked --target ID --scope session --wait-timeout 30 --json`
maps to `session.wait` with `args.statuses` (unique lowercase status names),
`scope` and integer `timeout-ms`. CLI status names are case-insensitive.
Omitted target uses the caller's AGWINTERM_SESSION_ID, otherwise active context.
Session scope accepts a session or its main/split pane, but refuses auxiliary
surfaces; pane scope can observe scratch, program overlay or quick. Explicit
window bounds resolution. Resolved identity stays pinned until completion.

Session priority is blocked > completed > active > idle, considering normal
main/split panes only. Removing a pane recomputes this aggregate. Process exit
does not mean completed; status is agent-reported, and an input-closed retained
pane can still be observed. Snapshot and observer registration are atomic:
a brief matching transition is latched even if auto-reset or journal wrap
occurs before delivery. Historical matches before registration do not count.

Matched result: `outcome:matched`, scope, run, window, owner IDs, status, seq,
`source:snapshot|transition`, elapsedMs. Snapshot seq is the registration head;
transition seq identifies the matching status/session-status record. Timeout
has `outcome:timeout` and identity/elapsed fields, without status/seq/source.
Both are wire `ok:true`, but CLI exits **0 only for matched, 3 for timeout**.
Usage is 2; refusal, target/window loss and transport/protocol errors are 1.
Plain matched output contains only the status; nonmatch diagnostics use stderr.
JSON emits exactly one final envelope. Removal errors use `target-gone` or
`window-gone`. Ctrl+C closes the connection and exits 130; if no final result
was consumed, JSON emits a client-sourced `cancelled` error envelope.

Default deadline is 30 seconds; `--wait-timeout` accepts 0..86400 integer
seconds (wire timeout-ms 0..86400000). Zero is unbounded. Deadlines use a
monotonic clock from registration: a transition at or after the deadline
cannot turn timeout into success. CLI transport defaults to this deadline
plus five seconds, or unbounded for zero. Explicit `--timeout` retains its
independent C0 transport meaning; a shorter transport timeout exits 1, not 3.

## Native questions and picker presentation (C3)

See [native questions](native-questions.md) for the `ask open/result/cancel` schema,
exact-ID outcomes, limits, cancellation and transport uncertainty. See
[native picker](native-picker.md) for `--selected-id` and optional item status markers.
These use owned native controls and share the window's modal input slot.

## Passive HUD presentation (C4)

HUD open/update additionally accept elapsed `hide-after`, sticky/frameless placement,
native DIP font size, stable pane anchoring and bounded Markdown. CLI `--file` reads
strict UTF-8 before transport. Tree/replies expose effective style and client-pixel
geometry; lifecycle events report expiry and anchor identity. See
[passive session HUD](session-hud.md) for precise limits, defaults and compatibility.

## Recently closed work (C5)

See [recent work](recent-work.md) for `recent.list`, `recent.open` and `recent.clear`, exact entry IDs, destination rules, partial outcomes and bounded persistence. These app-wide operations are independent of live restore checkpoints.
