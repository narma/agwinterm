## Read a session's output
- `agwintermctl session text [--all|--lines N] [--target <id>]` — dump the pane as plain text. Without a flag that is
  the visible screen; `--lines N` returns the last N lines ending at the bottom of the screen, reaching back into
  scrollback — which is where a launch banner or an error printed before a full-screen app started still lives;
  `--all` is the whole buffer, screen + scrollback (`--all` with `--lines` is refused, nothing read). A pane id
  reads the shell in that pane even while a pane overlay covers it; the overlay's own id, or no target while
  the focused pane holds one, reads the overlay (see Overlays)
- `agwintermctl session text --styles [--all|--lines N] [--target <id>]` — JSON `{cols,rows:[{row,runs}],cursor:{row,col}}`
  from one snapshot. Runs carry `col,width,text,faint,bold,italic,underline,inverse,strike,fg,bg`; colours are
  `default`, `idx:N`, or `#rrggbb`. Coordinates/widths are grid cells, not character counts; negative rows are
  history. Trailing whitespace (even styled) and trailing empty rows trim like plain text. On a known composer
  using faint suggestions, inspect runs in the cursor row where `col+width > cursor.col`: an all-faint suffix
  is consistent with a suggestion, a non-faint run with a draft. Verify the composer and text before the caret;
  this is an app-specific heuristic, not proof. `--json` keeps the reply envelope. `session overlay text --styles` is refused; use `session text --styles --target <overlay-id>`.
- `agwintermctl session copy [--target <id>]`            — return the session's current mouse text selection ("" if none);
  resolved like `session text`: `--target <pane id>` reads the shell under a pane overlay, no target or
  `--target active` reads the focused surface (the overlay while one is up), and `session overlay copy`
  reads a pane overlay's own selection by slot
- `agwintermctl surface cursor [--target <id>]`          — the caret COLUMN of a pane, as a bare integer.
  Use it before typing into ANOTHER agent's composer: an empty composer parks the caret at a known
  column, so a different column means a draft is sitting there and you should not send. The same
  column is necessary, not sufficient: a draft whose length is an exact multiple of the pane width
  wraps the caret back to the column it started at, so back a match with `session text` of the
  composer row before typing. After a print into the last column the answer equals the pane width
  (the wrap is deferred), so do not use it as an index into a `session text` row without clamping.
  A pane id
  reports that pane; a session NAME reports its focused pane. (A session id targets the pane that carries it
  while one does, same as `session text`/`session type`, so the pane you check is the pane you type into. While
  NO pane carries it — the carrier was closed, however it was closed — the id behaves like a NAME and reaches the
  FOCUSED pane, which can change under you: in a split session, address panes by the ids `tree --json` lists.)
  Reading rendered text and guessing at placeholder strings is what this replaces
- `agwintermctl session search "<term>"`                 — open the find bar over the active session; returns "N of M" (or "no matches")
- `agwintermctl session search --next|--prev|--close`    — step matches / close the find bar

## Selection & clipboard

A paste can also refuse with `the pane's input is closed` before clipboard access: no bytes
were sent by that request, and this does not assert that the child exited. A send-command
failure during broadcast may follow delivery to other panes; never retry it blindly.
- `agwintermctl selection all [--target <id>]`           — select the whole buffer (scrollback + live grid)
- `agwintermctl selection copy [--target <id>]`          — copy the current selection to the Windows clipboard
- `agwintermctl selection clear [--target <id>]`         — clear the selection
- `agwintermctl session paste "<text>" [--target <id>]`  — paste text into the pane (clipboard if text omitted; honors bracketed paste); replies `pasted` (handed to the pane's input, NOT proof a program read it — verify with `session text`), or `nothing to paste` when neither the text nor the clipboard gave any; refused (ok:false) on a read-only pane and on a pane whose process has exited (nothing sent, clipboard unread; the exit is observed a moment after it happens, so a paste right after a child dies can still reply `pasted`), and as `paste failed: <why>` when the write threw (the clipboard may have been read and part of the payload may have landed — do not retry blindly)
- keys: Ctrl+C (copy selection) · Ctrl+V (paste) · Ctrl+Shift+A (select all) · double/triple-click = word/line · drag past the edge auto-scrolls
- config `copy-on-select = true` auto-copies each finished selection (no Ctrl+C needed)

## Type into a session
- `agwintermctl session type "npm test" --target <id>`   — send keystrokes (newline = Enter). Control bytes are
  REFUSED, not stripped: a NUL would truncate your command while its Return still fired. Add `--allow-control`
  when you really mean one (an escape sequence for a TUI, a lone ^C). `session write` is NOT the way — it
  injects into the display, not into the program's input; but a terminal query in the payload is answered
  onto the program's input, and a mode it sets (mouse, focus, bracketed paste, key encoding) changes what
  the program receives from then on. What it paints is NOT durable: the program's next repaint, and the one
  every pane resize triggers (a window resize, `session split`, a split-ratio change, `session split close`),
  paints over it, and it does not survive into scrollback — write once the layout has settled, or read it back
- `agwintermctl session type --stdin --target <id>`      — the text is STDIN, as bytes. This is how text with
  quotes, newlines, runs of spaces or a leading `--` is sent: positionals are re-joined with one space and
  the option parser eats a leading `--`, both silently. Pipe a here-string (`@"..."@ | agwintermctl session
  type --stdin --target <id>`) or redirect a file. Exactly one trailing newline is dropped (the one the
  shell adds), so end the text with TWO newlines to press Enter. Invalid UTF-8 is refused with the byte
  offset and NOTHING is sent (exit 2). `--stdin` with positional text or `--select` is refused as ambiguous.
  `--allow-control` still applies. There is no `quick type` verb: the quick terminal is a pane whose id starts
  with `quick:`, so `session type --stdin --target quick:` types into it (once `quick on` has created it)
