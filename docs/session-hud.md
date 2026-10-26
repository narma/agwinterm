# Passive session HUD (P13)

A HUD displays short status text over a session without writing to its terminal, changing
its dimensions, taking keyboard focus, capturing the mouse or changing notification badges.
It is native presentation: no shell, helper process, temporary message file or restore state.

```powershell
agwintermctl session hud "Preparing review" --detail "Reading the diff" --spinner --position top-right
agwintermctl session hud update "Review ready" --text-color '#80ff80'
agwintermctl session hud close
```

`open` is optional before the message. Use explicit `open` for a message that is literally
`open`, `update` or `close`. `--target ID` and `--window ID` use normal control routing.
Use the owning **session** id, not the secondary split pane or an auxiliary cover's id.
Use `--pane left|right|focused|EXACT_PANE_ID` to anchor within that session (C4).
A session name is accepted; an ambiguous/unknown target refuses. With `--target active`,
an active scratch/quick/session-wide program cover refuses; name the session explicitly instead.
Pane overlays may coexist, including on the focused pane. Empty target/window selectors refuse.

## Options and state

- `--detail TEXT`: optional second text block.
- `--position`: `top-left`, `top-center`, `top-right`, `center-left`, `center`,
  `center-right`, `bottom-left`, `bottom-center`, `bottom-right`. Default `center`.
  `top` and `bottom` normalize to `top-center` and `bottom-center`.
- `--spinner`: animate the default ASCII bar. `--spinner-style` accepts `bar`, `braille`,
  `circle`, `blocks`, `dot` or `none`; an explicit style wins over the flag.
- `--size-percent N`: requested width 1–100, bounded to 10–80% of the content area;
  tree read-back reports the bounded value. Omitted width fits the text within those
  limits. Height fits wrapped text up to 80%; it is not an independently settable size.
- `--background-color HEX` / `--text-color HEX`: six hex digits, optional `#`.
  Otherwise the current theme's background/foreground is used. On **update**, the
  original background is retained; a newly supplied valid background is ignored.
  Open again to change it. Text color can change on each update.
- `--hide-after SECONDS`: finite 0..86400, fractional seconds allowed; omitted/zero is untimed.
  Time elapses even while unselected, obscured or minimized. Each successful open/update
  restarts the lifetime; old expiry cannot remove a replacement HUD or program overlay.
- `--sticky`: remove edge margins, allowing width through 100% at noncenter positions.
  Center keeps the normal 80% cap. Height remains bounded to 80% of the anchor region.
- `--no-frame`: omit border and reduce vertical padding; the background remains opaque.
- `--font-size DIP`: finite 6..72, default native 13 DIP; updates take effect in place.
  This affects HUD layout only, never terminal fonts or dimensions.
- `--pane left|right|focused|ID`: resolve a main/split pane once and pin its exact ID.
  Swapping follows that ID; removing it closes the HUD. Unknown/mismatched/auxiliary IDs
  refuse. Omission anchors to the session. There is still only one shared HUD slot.
- `--markdown`: enable the bounded native subset described below.
- `--file PATH`: read the message from a strict UTF-8 file (optional BOM), at most 64 KiB,
  instead of a positional message. Same text limits apply; invalid input refuses before
  connecting. From WSL, convert paths explicitly with `wslpath -w`, including paths with
  spaces. The CLI reads the file; the server never opens paths or creates body files.

Message and detail each allow 256 Unicode scalars after NFC normalization. The message
must be nonblank; control characters, including newline/tab/escape, are refused. Text is
plain unless --markdown is supplied. DirectWrite wraps Unicode; small windows may clip
content. The nine positions apply to the session or explicitly pinned pane rectangle.
An off-center anchor holds a 10% edge margin unless sticky is enabled.

Open replaces an existing HUD. Update requires an existing HUD and replaces its whole
specification except background: omitted detail/spinner/position/width/text color reset
to their defaults. Close is idempotent on a valid session, but unknown targets refuse.
Omitted hide-after keeps the historical untimed lifetime. Transient means it is not
restored: expiry, close, replacement, anchor/session teardown or app exit ends it.
Without a timer, a crashed agent may leave its HUD until closed.

## Bounded Markdown

Markdown message limit is 4096 NFC Unicode scalars and 128 lines; detail stays plain
256-scalar text. CR/LF/tab are allowed only in the Markdown message; other controls
still refuse. Native formatting supports ATX headings (`#` through `######`), simple
bold/italic, inline backtick code, triple-backtick fenced code, flat ordered/bullet
lists, `> ` quotes, horizontal rules and pipe tables. Tables need a separator row and
matching columns (at most 8), at most 32 rows including the header, and render as
monospaced rows with separators and a bold header. There is no nested style parser,
HTML processing, images, clickable links or scripting; unsupported syntax stays literal.
Unclosed inline markers stay literal; an unclosed code fence keeps the remainder code.
Oversized/mismatched recognized tables refuse. Text is clipped at the bounded panel
height; HUD never intercepts wheel input to scroll itself.

## Program overlays and input

A session-wide program overlay owns the same logical slot: HUD open/update refuse while
that program is present. HUD close never kills it. Opening a session-wide program overlay
replaces a HUD, and `session overlay close` also dismisses a HUD. The ordinary close
shortcut dismisses the HUD before closing a pane. Pane overlays may remain underneath;
scratch/quick covers and the dashboard obscure the HUD while shown.

The HUD itself is not a terminal surface. `session text/copy/type/paste` continue to
address their normal terminal targets. A HUD cannot be read with `overlay text`, resized
with `overlay resize`, or awaited with `overlay --block`; inspect `tree` instead.

## Wire API

`session.hud.open`, `session.hud.update`, `session.hud.close`. Open/update args:
`message`, optional `detail`, `spinner` (style string), `position`, `size-percent` (JSON integer),
`color` (background), `text-color`, numeric `hide-after`/`font-size`, boolean
`sticky`/`frame`/`markdown`, string `pane`. Close accepts no display arguments. Invalid types,
unknown arguments and unsupported options refuse before mutation. Replies are
`{session, hud}` inside the usual `ok/result` envelope; close reports `hud:null`.
The session's `tree` node includes the same `hud` object while set:
`message`, `detail`, `spinner`, `backgroundColor`, `textColor`, `sizePercent`, `position`.
Absent optional values are null; no HUD means the tree omits `hud`.
Additive fields are `hideAfter`, `sticky`, `frame`, `markdown`, `fontSize`,
`effectiveFontSize`, `anchorPaneId`, `visible` and `geometry:{x,y,width,height}` in client
pixels. Geometry uses the drawing layout; background queries do not select a session.
Visibility accounts for selected session, scratch/program covers and dashboard. Expiry
publishes a HUD closed event with reason `expired`; anchored events include `anchorPaneId`.
Timers dispatch on the owner UI thread, so busy UI work can delay expiry but cannot make
it early or redirect it to a replacement.

This is the native Win32 implementation. Hosts without HUD support explicitly refuse.
P17 expands the shared conformance floor alongside the Wave-3 lite companion; see
[lite parity](lite-parity.md#p17-wave-3-mirror). No release/tag is included.

Reference: [agterm v0.26.0 HUD implementation](https://github.com/umputun/agterm/blob/v0.26.0/agtermCore/Sources/agtermCore/Hud.swift).
The nine positions, passive behavior and replacement semantics follow that interface;
rendering is native rather than an auxiliary terminal, and pane/cover targeting is refused
instead of silently widening to the whole session.
