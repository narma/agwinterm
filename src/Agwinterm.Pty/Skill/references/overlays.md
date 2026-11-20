## Scratch & quick terminals
- `agwintermctl session scratch on|off|toggle [--target <id>]` — a per-session extra shell drawn over that session's content (opens in the session's cwd; stays alive when hidden; not restored)
- `agwintermctl quick on|off|toggle` — one app-level detached shell (home dir; stays alive when hidden; not restored). API shows stay pinned without taking focus; human toggles dismiss on blur. `--window quick --target active` addresses its surface. `quick-terminal-size` is 40–90% of the pointer monitor's work area; `quick-terminal-hotkey` is opt-in, empty by default. See `docs/quick-terminal.md`.

## Overlays (run a program over a session — or over ONE pane of it — ephemerally)

Web pages share session-wide and independent pane slots with program overlays. Use an exact SESSION_ID;
--pane left|right selects a full-pane slot. Program commands and --wait are unsupported for web opens.
`agwintermctl session overlay open --html "C:\\path\\page.html" --navigation --target SESSION_ID --json`
opens a local document; use --url for HTTP(S), including localhost. JavaScript is off
unless --js is supplied. --navigation and --chromeless conflict. --follow selects the
owner; without it opening a background owner preserves selection. --width/--height
are DIPs; --size-percent uses the session content area. Existing program/web slots,
including hidden ones, refuse a new web open; validated successful web opens replace HUDs.
The result.pageID identifies the page. tree adds htmlOverlays with id, file/url,
currentUrl, state/error, hidden/visible, geometry and zoom. close/hide/show/toggle/resize
act on the web page by session target. reload preserves page ID and temporary storage.
navigate back|forward|browser provides history or explicit external handoff.
Local HTML is self-contained by default; --cwd DIRECTORY grants canonical sibling resources,
refusing home/filesystem roots and escaping links. Top-level remote navigation
stays on the initial origin by default. Pass --browse with --url for HTTP(S) redirects/links
across sites (for example --url https://google.com --js --browse). Browse is reported in tree,
shows the current site, conflicts with --chromeless, and does not imply --js. Local documents stay within their grant.
Remote pages never receive local-file access or a review bridge. Missing runtime refuses without disturbing the slot;
`agwintermctl install webview2` is the explicit optional runtime installation command.

Trusted local HTML supports data-agterm declarative buttons/forms even without --js.
With --js it exposes the asynchronous agwinterm.request API (agterm alias), inheriting
live owner context and accepting explicit target/window. It refuses zmx.present/reset,
session.overlay.job.run/page.serve and session.companion.page.attach. URL/image pages
and subframes have no host bridge. Explicit --js --bridge review selects a narrower API.
The review API accepts session.overlay.submit with
{args:{value:"text"}} (up to 1 MiB UTF-8, empty string valid), or session.overlay.close
with {}. No target, window or other commands are allowed. `session overlay result
--page PAGE_ID --json` returns result.pageOutcome with pageID and outcome pending,
submitted (plus value), or dismissed. First completion wins; closing the owner
dismisses pending reviews. The latest 32 finished results are retained instance-wide;
pending pages are not evicted. Keep a separate backup file for restart/timeout recovery.
Ordinary CLI success/refusal/argument exit codes remain 0/1/2; a review adapter must
interpret dismissed itself. Do not assume a transport timeout cancelled a page.
Local HTML --block waits for the exact page and exits 0 on submit, 2 on dismissal,
1 on failure. Bare reload uses the original source; --current/toolbar Reload uses the
current document. URL-only --persistent shares saved browser storage; browser clear
requires all persistent pages closed, including Undo-retained pages. browser links
browser|overlay|overlay95|overlay75|overlay50 selects terminal-link opening policy.
--image FILE accepts PNG/JPEG/GIF/WebP up to 64 MiB/100 million pixels without scripts.
--companion opens HTML/URL in an independent shell-less child session (no terminal
commands, hide or split). Closing its page removes it; closing its parent closes children.
session close --undoable retains a live web document for three seconds for Reopen.
Remote display/replication is deferred to a separate change.
The rule, quoted from `ISessionHost.SessionOverlay` (the CLI header quotes it too):
  A session has three overlay slots: one session-wide and one per pane. A session-wide overlay
  covers the whole session (every pane, and any pane overlay under it), as today. A pane overlay
  covers exactly one pane's box - always the full box, never floating - and the sibling pane stays
  visible and interactive. --pane left|right names the slot: left is pane 0 and right is pane 1
  whatever the axis (on a horizontal split left is the top pane), a non-split session accepts
  --pane left, and the flag omitted means the session-wide slot - today's behaviour, byte for
  byte. A pane overlay is that pane's surface while it is open and shown: keys typed into the focused pane,
  the mouse inside the pane's box and --target active reach the overlay; --target with a pane id
  reaches the shell underneath (agterm: "session text reads the surface underneath"); --target
  with the overlay's id reaches the overlay from anywhere, and on session overlay itself names
  that overlay's slot - the same as passing its --pane word - for as long as the id resolves (an
  overlay that closed is reached by --pane only); with --pane naming the other side it is refused.
  The slot moves with its pane (a swap, a split close of the other pane) and dies with it (split
  close, the shell exiting when that removes the pane - a single-pane session keeps an
  exited shell on screen, and its overlay with it - session close, the window closing).
- `agwintermctl session hud [open] "message" [--detail "text"] [--spinner] [--position top-right] [--target <session id>]`
  — passive session-wide text; shell input, mouse, history and dimensions are unchanged. Nine anchors:
  top-left/top-center/top-right, center-left/center/center-right, bottom-left/bottom-center/bottom-right;
  top/bottom aliases normalize, default center. Optional --size-percent 1..100 bounds width to 10..80%,
  --spinner-style bar|braille|circle|blocks|dot|none, --background-color and --text-color #rrggbb.
  `hud update "message"` replaces the whole spec except its original background; omitted options reset.
  `hud close` dismisses only the HUD. Open replaces a HUD but refuses over a program overlay; program
  overlay open replaces a HUD. Overlay close/the ordinary close shortcut dismiss a HUD too. Tree's hud
  object is its read-back. No persistence; --hide-after 0..86400 seconds sets elapsed expiry (0/default untimed).
  --sticky removes edge margin and allows width 100% off-center; --no-frame removes border/reduces padding.
  --font-size 6..72 DIP updates native text in place. --pane left|right|focused|EXACT_ID pins a main/split pane
  through swaps; pane removal closes the HUD. Existing --target still requires the owning session.
  --markdown supports bounded multiline headings, simple emphasis, code, flat lists, quotes, rules and
  pipe tables (8 columns/32 rows); 4096 scalars/128 lines, no HTML or clickable links. --file PATH reads
  strict UTF-8 up to 64 KiB instead of a positional message; WSL callers use wslpath -w explicitly.
  Tree adds style, anchorPaneId, visible and client-pixel geometry. Expiry cannot close a replacement.
  Plain mode keeps max 256 normalized Unicode scalars per text field,
  no control characters, nonblank message. Split-pane-only/auxiliary ids refuse: name the owning session.
  Scratch/quick/dashboard obscure it. See docs/session-hud.md for exact wire fields and clipping limits.
- `agwintermctl session overlay open "<command>" [--pane left|right] [--size-percent N] [--wait] [--block] [--target <id>]`
  — run `<command>` in a throwaway terminal over the session (or, with `--pane`, over that one pane's box); it vanishes when the program exits, leaving the session untouched. Returns the overlay id.
  `--size-percent N` (1..100) makes it a centered floating panel over a dimmed session (default = full content region). The session gets a `* (overlay)` tag in `tree`.
- **The pane form** (`--pane left|right`): the id it returns is `<pane id>:overlay:<hex>`, so the pane is readable
  off it. The other pane keeps rendering and taking input; `session type --target <that pane id>` still lands in
  its shell; the overlay runs in the pane's cwd at the pane's font size. `tree --json` lists the open pane slots as
  `"paneOverlays":["left"]`, `["right"]` or `["left","right"]` inside the session node — OMITTED when none is open,
  independent of `overlay` (the session-wide slot; both can be up at once, and the session-wide one covers the
  pane overlays until it closes). Reading it: `session text --target <overlay id>` (or `overlay text --pane X`)
  is the program's screen; `session text --target <pane id>` is the shell UNDERNEATH it; `session text` with no
  target reaches the overlay while its pane is the focused one. `--target` may be the session id, either pane
  id, or either pane's overlay id (`<pane id>:overlay:<hex>`, the id `open --pane` returned), but one that names
  the OTHER side than `--pane` is refused (`'<id>' is the right pane; --pane left names the other one. Nothing
  opened.`, or `'<id>' is the right pane's overlay; --pane left names the other one. Nothing opened.`) — the
  caller named two panes. That overlay id with `--pane` OMITTED names its own slot, exactly as its word would,
  for as long as it resolves (`close`, `result`, `copy`, `text`; `open` on it is `pane overlay already open`;
  `resize` and `--size-percent` are refused naming the overlay, since a pane overlay is always full-pane) — so a
  program run INSIDE a pane overlay, whose `AGWINTERM_SESSION_ID` is that overlay's id, aims its bare
  `session overlay` verbs at its own slot and needs `--target <session id>` to reach the session-wide one. Once
  the overlay closed its id resolves nowhere: `--pane X` is then the only way to that slot (its `result`); an id
  that stops resolving WHILE a call is in flight (the overlay closed under it) is refused `'<id>' no longer names
  an open pane overlay: ... retry with --pane <its side>` — `ok:false`, retry by the word, not by the id. What
  is REFUSED with a pane, `ok:false`
  and nothing opened, each starting with agterm's phrase: `pane not visible` (`--pane right` on a one-pane
  session — we never hide a pane, so that is the only case; pass `--pane left` or omit it),
  `pane overlay already open` (that slot holds one; close it first or read it — a pane slot NEVER silently replaces, unlike the
  session-wide slot, which replaces as today), a `--pane` word other than exactly `left`/`right` (`Left`, `top`
  and a pane id are refused naming both words), `--pane` with `--size-percent` (a pane overlay is always
  full-pane; the CLI exits 2 and sends nothing, a raw client is refused with the same words) and `resize --pane`
  (nothing to resize to). In the UI: `Ctrl+Shift+W` closes the focused pane's overlay before it would close the
  pane; `Esc` (`close_cover`, if bound) closes the session-wide cover if one is up, else the focused pane's
  overlay; a click in a pane's box focuses that pane and lands in its overlay; the divider still drags.
  The range is VALIDATED, not clamped: `0`, `-5`, `150`, `sixty` and a quoted `"60"` are each refused (`ok:false`,
  the value and the range named) and NO overlay opens — read `tree` to confirm. To ask for the full region, omit
  the flag; there is no number that means it. The same rule applies to `overlay resize --size-percent N`, whose
  reply `resized N%` is always the N you asked for.
- `overlay hide|show|toggle` preserves the program and ID; hidden overlays do not capture ordinary input.
  Explicit overlay IDs still read/write the hidden terminal. Empty slots refuse; background show does not select.
  Open/resize accept independent `--width`/`--height` in 1..16384 Windows DIP for session slots only.
  Each overrides its percent axis. Partial resize keeps omitted settings; explicit percent resets DIP overrides.
  `resize --full` (or bare resize) clears all sizing. Pane overlays remain full-pane.
  Open accepts `--cwd`, `--follow` (select owner after open) and `--background-color '#RRGGBB'`.
  Tree `overlayDetails` reports IDs, hidden/effective visible state and client-pixel geometry with requested sizes.
- `--wait` keeps the panel after the program exits (shows "press any key to close") instead of auto-dismissing.
  With `--pane` the banner sits at the bottom of THAT pane's box and only a key typed into that pane (while it is
  the focused one) dismisses it — a key in the other pane goes to the other pane's shell.
- `--block` waits for the program to exit and returns `exit N` (its exit code). Good for `lazygit`, `htop`, an editor, or any pick-a-thing helper you want to run and read the result of.
  With `--pane` it waits on that slot's program; `closed` when that slot was closed first.
  The reply is the outcome of the overlay THIS call opened: `closed` when it was closed or replaced before its
  program exited. `exit 1` also covers a program that could not be started at all (the session's cwd gone,
  the pty-host down), and a blocking open closes its pane as it replies, so AFTER the call `exit 1` cannot be
  told from a program that ran and failed. When that distinction matters, do not block: open with `--wait`
  (the pane stays after the exit and the reply is its id), poll `overlay result` until it says `exit N` — a
  session-wide open polls the bare form; a `--pane X` open polls `overlay result --pane X` (or `--target <the
  id the open returned>` while the overlay is up), because a pane open never resets the window-wide value and
  a pane exit never writes it (a bare poll after a pane open reads whatever an earlier session-wide overlay
  left there, or waits forever) —
  `session text --target <that id>` for the output or the start failure (`exit N` is written when the process
  ends, not when its output is drained — the last lines can still be landing; read again if the text looks
  cut off), then `overlay close --target <that id>` (a bare `close` closes the ACTIVE session's overlay, which
  need not be yours; that close answers `ok:false` when the overlay is already gone — a key pressed on the
  `--wait` prompt, a later open replacing it — which means closed, not failed; `tree` settles it). The bare
  `overlay result` is ONE value per window — reset to `no overlay` by any session-wide open in the window,
  written by any session's session-wide overlay exit — so the bare poll is trustworthy only while yours is
  the only session-wide overlay in the window; `tree` shows which sessions have one. The pane form is per
  slot and has no such caveat.
  The window closing under a blocking open answers `ok:false` with the status unknown.
- `agwintermctl session overlay close [--pane left|right] [--target <id>]`   — dismiss the overlay now. With `--pane`
  that slot: `closed`, or `ok` with `no overlay` when the slot is empty (closing nothing is not a failure).
- `agwintermctl session overlay result [--pane left|right] [--target <id>]` — WITHOUT `--pane`: the last overlay's
  `exit N` (or `no overlay`): one value per window, not per session — reset by any session-wide open in the
  window, written by whichever session's session-wide overlay exits next; it ignores `--target` EXCEPT a live
  pane overlay's id, which reads that slot as its `--pane` word would (a `--pane` open with `--wait` can be
  polled by the id it returned; once the overlay closed the id resolves nowhere and the bare form is
  window-wide again — a session-wide overlay's id never selects a slot). Two session-wide overlays in one window
  make the bare form name either one's exit. A pane overlay's exit never writes it. WITH `--pane`: that slot's
  own last result, `exit N` — or refused `overlay still running` (its program is up) / `no overlay result`
  (nothing has run in that slot since the window opened). The pane form is per slot, the bare form per window:
  a deliberate divergence from agterm (recorded in `docs/agterm-parity.md`), because the bare form shipped
  that way.
- `agwintermctl session overlay copy [--pane left|right] [--target <id>]` — the text of the selection made INSIDE
  the overlay (`selection all --target <overlay id>` makes one); `result.text`, printed bare without `--json`.
  The clipboard is NOT touched; `session copy --target <pane id>` keeps reading the pane underneath (bare, or
  `--target active`, it reads the focused surface — the overlay — like `session text`). Refused: `no overlay`
  (with `--pane X`: `no overlay: --pane X names which slot, and nothing is open in it`), `no selection`.
- `agwintermctl session overlay text [--all|--lines N] [--pane left|right] [--target <id>]` — the overlay's drawn
  buffer as `result.text`, the same reader as `session text` (`--lines N` reaches into its scrollback, `--all` is
  the whole buffer; the pair is refused). Refused: `no overlay` (as above), `failed to read surface buffer` + a colon + why.
  The buffer is readable the moment `open` returns (the terminal is built before the reply); agterm's
  `overlay not realized` is kept as the phrase for a slot with no terminal to read yet, which does not happen here.
- What is REFUSED (`ok:false`, nothing happened): `open` with no command; `open` and `resize` whenever no
  session resolves (a `--target` that matches nothing, or no target while no session is active); a `close`
  whose `--target` names something that does not exist; `open`, `close` and `resize` WITHOUT `--pane` whose
  `--target` names one pane of a split session (a session-wide overlay covers the whole session — the refusal
  names the session id to pass instead, and `--pane` is how you name one pane); `resize` on a session with no
  overlay open; and the pane refusals listed under the pane form above.
  One `resize` `ok:false` is NOT "nothing happened": the reply that says the window did not run the request
  within 15 s — that resize is still queued and may land when the window's loop resumes; read `tree`.
  `close` answers `ok` ("no overlay") when the session resolves and has no overlay, or when the target is
  absent, empty or `active` while nothing is active — closing nothing leaves "no overlay open" true. These used to
  answer `ok:true` with the failure as the result text; branch on `ok`.
