# libghostty-vt 1.0.0 integration audit

Checked 2026-11-07 against tag vt-1.0.0 (0fa3d577a5614150ff8e2c152de5fb9d5c6d2dec).

The recommendations and validation below are chronological. For the current
completeness assessment, see the 2026-11-08 re-audit at the end.

Sources: https://lib.ghostty.org/vt/ and https://lib.ghostty.org/vt/stability.html; release headers in include/ghostty/vt. These are integration opportunities, not all newly introduced in 1.0.

Already integrated: native keyboard/mouse/paste encoders, focus reporting, Kitty direct graphics and animation, hyperlinks, synchronized output, semantic prompt marks, OSC 7501 program status, progress and notifications, snapshots, Unicode search and history import. Do not count these as missing features.

Priority recommendations (benefit versus integration cost):

1. Theme reporting: wire GHOSTTY_TERMINAL_OPT_COLOR_SCHEME for CSI ? 996 n, then send ghostty_color_scheme_report_encode when the theme changes and mode 2031 is enabled. Neither is wired in agwvt.c. Helps TUI applications follow the actual pane theme. Small-to-medium effort, including owner/GUI communication.
2. Native selection: use ghostty_selection_gesture_* and terminal_select_word/line/output. Program.Input.cs currently selects words by scanning only one physical row with a custom delimiter predicate. Preserve user delimiters, rectangular selection and host ownership while adopting library semantics for graphemes and wrapped lines. Medium-to-large effort.
3. Formatted copy/export: use ghostty_formatter_* for plain text, HTML and VT output. Adapter currently calls no formatter functions; current copying is managed. Add Copy with formatting and export of selected command output, preserving colors and links where supported by the formatter. Medium effort; Windows HTML clipboard packaging remains our responsibility.
4. Extended clipboard: current clipboard callback accepts only one text/plain representation and supplies no protocol completion reply; no CLIPBOARD_READ callback is installed. Add atomic MIME-aware writes and replies for OSC 5522, then optional reads for OSC 52/5522 under an explicit user policy. Medium-to-large effort; callback timing requires special care because native replies are synchronous.
5. Terminal size reports: wire GHOSTTY_TERMINAL_OPT_SIZE with actual pane/cell pixel geometry. Existing mouse encoder size does not install this terminal query callback. Improves applications that ask for text-area and cell pixel dimensions. Small-to-medium effort; verify both local and hosted paths.
6. Accurate capability identity: explicitly set TERMINFO_NAME and XTVERSION/device-attribute callbacks where needed. The adapter does not configure them, so library defaults may describe Ghostty instead of Agwinterm's effective capabilities. Audit the defaults before overriding them; report only capabilities implemented end to end. Small-to-medium effort.

Lower priority: opt-in application window resize (RESIZE_WINDOW callback); Kitty file/shared-memory media (deliberately disabled, requires transport-aware handling); Glyph Protocol (disabled by default, requires a separate renderer/API feasibility investigation). These should not precede the six items above. Sixel is not a feature obtained simply by enabling this library.

1.0 guarantees C ABI compatibility within 1.x, but explicitly excludes snapshot byte-format stability. Keep upstream identity and snapshot validation; do not remove our version checks merely because the C ABI is stable.

Validation: release library and C adapter built successfully with Zig 0.17.0; native search suite: 132 passed, 3 skipped, 0 failed; Agwinterm.GhosttyHarness: 241 checks passed. GUI installation and VM UI tests were not performed in this update.

## Implementation progress on codex/libghostty-vt-capabilities

- Theme reporting: implemented. Native CSI ? 996 n answers use the pane theme's weighted background brightness (299R+587G+114B >= 128000 is light). OSC background overrides do not change this identity. Scheme transitions notify only subscribers to mode 2031; duplicate/same-scheme updates are silent. Local GUI updates synchronize immediately; modern hosted sessions use the existing owner input queue; detached legacy hosts explicitly flush theme-originated replies without waiting for new output.
- Native selection: cell, word, logical-line and rectangular mouse drags now derive their bounds through `ghostty_selection_gesture_*`. A native tracked press origin survives direction reversal and reflow. The UI supplies inclusive cell coordinates, Windows click counts, capture and autoscroll timing; each update evaluates press/drag atomically with that origin. Hosted requests run the identical calculation in the owner. `select_command_output` and `copy_command_output` use native semantic output bounds; they target the existing selection's focus or, without a selection, the latest command with an output-start mark.
- Formatter: ordinary copy uses the native plain formatter, preserving Windows CRLF at the boundary; rectangles disable unwrap. HTML/VT representations are captured with the selection and carried in hosted selection replies, so formatted copy and file export survive source eviction and repaint. `copy_selection_html` supplies Windows CF_HTML offsets and a Unicode-text fallback. `export_selection_text/html/vt` actions open the Windows save dialog and write UTF-8 files. Each retained formatted representation has a 100,000-byte cap; newly selected frozen frames retain plain text only.
- Retained content: UI hit testing, highlights, clipboard packaging, hosted transport, immutable selected text and saved frames remain ours. A TUI frame can relocate its saved selection without rebuilding copy text in managed code. Frozen/presented-frame fallback still reads the saved cells because the live Ghostty terminal may already contain output that was never shown. This is intentionally separate from selecting live terminal content. Keyboard mark-mode navigation remains UI-controlled.
- Extended clipboard: OSC 5522 MIME writes, reads and MIME listings use native parsing and replies. Writes honor `clipboard-write`; reads and listings require the explicit `clipboard-read=true` setting (default false). The hosted effects pipe has a correlated request/reply operation independent of frames and terminal queries. Detached owners deny access. Windows operations prepare all representations before emptying the clipboard, publish while holding it open, and discard partial results on an OS error. See `docs/terminal-clipboard.md` for format mappings, limits and timeout behavior.
- Geometry: installed the native size-query callback with actual cell geometry supplied by resize. Added checks for CSI 14/16/18 t and mode 2048 resize notifications.
- Identity: XTVERSION identifies Agwinterm and the engine release. Kept conservative default DA reports. TN remains unset because no per-session terminfo identity is supplied to the adapter; inventing a name would misreport the remote environment.
- Existing-feature audit: the native contract suite exercises input, graphics, Unicode search, snapshots, synchronized rendering and program status. This is regression coverage, not yet an exhaustive feature-completeness audit.

Adapter ABI is now 8 because clipboard callback/reply exports must not be loaded from an older same-upstream DLL. The upstream C ABI remains 1.0.0.

Validation so far: 258 native contract checks passed. Hyper-V process contracts: 56 passed, including local, modern hosted and detached legacy hosted theme query/notification probes. Build-only warnings are the existing xUnit analyzer warnings. GUI/host/CLI test publications use .NET 11, ReadyToRun and UseRuntimeAsync=true. No installed application was replaced.

GUI validation: Hyper-V Ghostty suite passed 83 checks, including actual Windows HTML clipboard, styled VT copy, retained selection through four window resizes, hosted presentation, Unicode/IME input and native graphics. The first GUI attempt had an incomplete test-only host payload; the successful run used the normal native payload layout. The installed application remains untouched.
Evidence: artifacts/capabilities-process-tests-run7/results/evidence/pty-process/process.trx; artifacts/capabilities-ghostty-ui-run2/results/stdout.log.

## Copy and gesture migration validation

The subsequent migration passes 277 native contract checks and 93 GUI checks in
each mode (`SessionHost=in-process` and `SessionHost=server`). Coverage includes
word drag expansion/reversal/retraction, line drag, rectangles in both directions,
soft wraps, large copy buffers, graphemes, Unicode line separators, reflow of the
press origin, retained text after repaint, alternate-screen copying, HTML/VT
clipboard exports and copying the displayed synchronized-output frame.

The synchronized-output test exposed an existing local anchor-reset bug: hidden
highlight state was mistaken for a lost press origin. Frozen-frame drags now keep
their origin, while invalid live selections cannot be extended into unrelated text.
One final VM failed its prepared-desktop check before running the application;
another attempt ran out of staging space. The successful rerun used the normal
worker after removing completed payloads and a rebuildable native cache.

Evidence: `artifacts/selection-harness.log`,
`artifacts/selection-verified-local-run3/results/stdout.log`, and
`artifacts/selection-verified-hosted/results/stdout.log`.
GUI, host and CLI were published with .NET 11, ReadyToRun and runtime-async enabled.
The installed application was not replaced.

## OSC 5522 validation

Extended clipboard integration passes 290 native contract checks, eight
host-effect channel tests and 118 Hyper-V Ghostty GUI checks in each session mode.
The GUI cases exercise actual Windows Unicode/HTML/PNG clipboard representations,
binary MIME round-trips, denied reads and listings, denied writes, invalid UTF-8
transactions, clipboard contention (`EBUSY`), unsupported primary selection,
clear versus empty values, and an 800,000-byte write/read through the child PTY.
Channel tests cover receipt correlation and cancellation with a late response.
Oversized transactions are rejected before invoking host clipboard operations.

Large reads required a separate bounded PTY-response allowance for base64
expansion; user-input and paste limits remain unchanged. Full-suite reruns also
exposed an existing selection-fixture race: synchronized-output tests now wait
for the prepared frame and complete local painting before freezing it.

Evidence: `artifacts/clipboard-harness.log`, `artifacts/clipboard-channel-tests.log`,
`artifacts/clipboard-verified-local-run2/results/stdout.log`, and
`artifacts/clipboard-verified-hosted-run2/results/stdout.log`.
GUI, host and CLI publications use .NET 11, ReadyToRun and runtime-async.
No installed application or user clipboard was changed by these VM tests.
Configuration and limitations: [terminal clipboard protocols](terminal-clipboard.md).

## TERM and capability identity audit

Completed for local/hosted Windows, native WSL PTYs and SSH PTY transport.
New panes use portable `xterm-256color` rather than inheriting a launching
terminal's identity. Explicit profile TERM survives ordinary and owned ZMX
launches; SSH attachments pin their own PTY type without rewriting existing
remote workloads. The native helper defaults empty as well as absent TERM.

The engine's XTGETTCAP map advertised renderer features missing from our shared
cell/frame representation: colored/shaped underlines, overlines and blinking
text. Their positive replies were suppressed during this audit; the decoration
implementation below restores all of them except blinking text. Conservative DA replies and
Agwinterm XTVERSION are verified; TN deliberately remains unset because a global
name would misrepresent overridden profiles and existing remote workloads.
See [the route matrix and limitations](terminal-capability-identity.md).

Validation: 305 native contracts, 59 WSL/SSH unit checks, two Linux PTY tests,
139 Windows UI checks in each owner mode, and default/overridden WSL plus
loopback SSH queries in both owner modes. GUI, host and CLI were published
with .NET 11, ReadyToRun and runtime-async. Evidence: `artifacts/identity-harness.log`,
`artifacts/identity-unit/identity-unit.trx`, `artifacts/identity-linux-tests.log`,
`artifacts/identity-local-ui/results/stdout.log`, `artifacts/identity-hosted-ui/results/stdout.log`,
`artifacts/identity-wsl-local-final/test.log`, `artifacts/identity-wsl-hosted-final/test.log`.

## Colored and shaped decorations

Implemented single, double, curly, dotted and dashed underlines with default,
indexed and RGB colors, plus independent overlines. Blank cells, wide glyphs,
graphemes and text runs use the same decoration renderer. Patterns stay aligned
to the terminal grid across text runs. OSC 8 UI underlines keep the text color.

Adapter ABI 9 and hosted frame revision 2 carry the new cell data. Full/delta
frames preserve decoration-only changes and immutable old leases. Added disk
fields preserve decoration styles and color sources through history import;
legacy buffers still default to plain underlining. Re-enabled XTGETTCAP `Su`,
`Smulx`, `Setulc`, `Smol`, `Rmol`; `blink` remains disabled.

Validation includes 312 native contracts, 448 core tests and 37 presentation
transport tests. Dedicated VM pixel checks distinguish all five underline
shapes and overlines on blank cells and verify their capability replies in
local and hosted modes. The full core run also refreshed old selection fixtures
to provide a real session lock, retain evicted selected text without a stale
highlight, and preserve Ghostty's original mouse-press anchor.

Final acceptance: 151 Hyper-V UI checks passed in each mode, plus private-desktop
WSL default/override and loopback SSH probes in both modes. GUI, host and CLI
were built with .NET 11, ReadyToRun and runtime-async. Evidence:
`artifacts/decorations-harness.log`, `artifacts/decorations-core/decorations-core.trx`,
`artifacts/decorations-unit/decorations.trx`,
`artifacts/decorations-local-final/results/stdout.log`,
`artifacts/decorations-hosted-final/results/stdout.log`,
`artifacts/decorations-wsl-local/test.log`, `artifacts/decorations-wsl-hosted/test.log`.

## Text blinking

SGR 5/6 blink now travels from native cells through hosted full/delta frames and
saved history (adapter ABI 10, frame revision 3). The GUI toggles foreground and
decorations every 500 ms independently of cursor blink; backgrounds, selected
text and immutable frame data are unchanged. The timer stops when no rendered
cells need blinking or the window is minimized. No PTY output or owner frame
traffic is needed for an idle blink phase. SGR 25/0 clears the attribute.

The `blink` XTGETTCAP response is restored and the last reply filter is removed.
Native contracts cover SGR 5/6/25/0, history restore and retained selections;
transport contracts cover blink-only deltas and old-frame immutability.
VM pixel checks sample idle on/off phases with cursor blinking disabled and a
10-second cursor interval, preserve backgrounds/nonblinking text, and verify
that SGR 25 stops the animation. The hosted CI harness uses the current
acknowledged clipboard callback; the WSL entry point matches the routing guard.

Validation: 317 native contracts, 448 core tests, 38 presentation tests and 132
routing checks passed. Hyper-V passed 158 UI checks in each of local and hosted
modes, including the full 87-check server harness. Private-desktop WSL probes
passed default TERM, profile overrides and loopback SSH in both modes. GUI,
host and CLI were published with .NET 11, ReadyToRun and runtime-async.
Evidence: `artifacts/blink-harness.log`, `artifacts/blink-core`,
`artifacts/blink-unit`, `artifacts/blink-routing.log`,
`artifacts/blink-local-ui/results`, `artifacts/blink-hosted-ui/results`,
`artifacts/blink-wsl-local/test.log`, `artifacts/blink-wsl-hosted/test.log`.

## Completeness re-audit (2026-11-08)

Rechecked the pinned 1.0 C headers, adapter callbacks, managed model, renderer,
owner input/effect channels and existing native/VM regression contracts. This
is an integration-boundary audit, not a claim that every upstream escape
sequence or every external TUI has been independently tested.

Two end-to-end gaps were found and fixed:

- SGR 8 (conceal) was parsed by Ghostty but lost in `style_attributes`. All eight
  upstream boolean style flags now survive native cells, history and hosted
  frames. Concealed cells suppress foreground glyphs and all decorations,
  including blank-cell and hyperlink underlines, matching Ghostty/xterm.
  Backgrounds and underlying text remain intact; SGR 28/0 clears conceal.
  Conceal is display styling, not redaction: copy and text queries retain text.
- Mode 1004 reports previously followed only Windows activation. Switching the
  active session, split pane or cover now sends loss to the previous surface
  and gain to the next. Re-selecting the same surface is silent. Switching
  surfaces while the window is inactive sends no premature gain; activation
  reports to the current surface. Hosted owners check their current mode when
  consuming input, rather than trusting potentially stale frame metadata.

Adapter ABI 11 and hosted frame revision 4 prevent older peers from silently
dropping conceal. Existing history attribute storage needs no new disk fields.

| Integration | Assessment and coverage |
| --- | --- |
| Keyboard and paste | Native encoders remain authoritative for legacy/Kitty modes, Windows input, Unicode/dead keys, modifiers, bracketed paste and terminator sanitization. Owner encoding and broadcast tests cover recipient-specific modes. |
| Mouse and focus | Native mouse encoding covers cell/pixel coordinates and tracking modes. Focus payloads are fixed CSI I/O, gated by native mode 1004; the UI transition gap above is closed. |
| Cells and colors | All native boolean styles and five underline shapes are carried. Palette/default/RGB sources, OSC resets, cursor defaults and reverse-screen resolved colors are preserved by native captures. Conceal now joins the shared local/hosted renderer. |
| Hyperlinks | OSC 8 URIs survive capture, synchronized frames, history and formatted copy. Windows opening/UI decoration remains application-owned. |
| Graphics | Direct Kitty RGB/RGBA/PNG, placements, layers, animation and immutable image generations have native and UI coverage. Hosted image payloads remain separate from cell frames. |
| Synchronized output | Previous complete cells/images/links remain visible; bounded timeout and resize release are covered. Selection targets the displayed frame. |
| Search and persistence | Unicode folding/graphemes, cancellation, reflow, eviction, snapshots, parser continuation and inert history import have native contracts; hosted queries validate generation. Snapshot identity/checksum/size checks remain necessary. |
| Titles, cwd, bell, notifications, progress, OSC 7501 | Callbacks are connected to host actions and hosted effects. Durable progress/status is restored on attach; ephemeral bell/clipboard effects are not replayed. Native/hosted suites exercise these paths; existing user notification policy remains in the UI. |
| Theme, geometry and identity | Native query callbacks, mode 2031/2048 updates and conservative DA/XTGETTCAP claims remain connected. TN stays unset for unknown per-session identities. |
| Selection, formatting and clipboard | Native gestures/formatters remain authoritative for live content. Frozen-frame fallback, UI, transport and retained exports are ours. OSC 52/5522 policy, MIME limits, acknowledgments and errors retain their dedicated coverage. |

Deliberate boundaries remain: application-requested window resize is not enabled;
Kitty file/temp-file/shared-memory media need transport-aware handling and stay
disabled; Glyph Protocol needs renderer work. ENQ has no configured answerback,
title reports remain disabled by upstream default, and checksum extensions are
not opted into. These are not missing callbacks to enable blindly. Sixel is not
provided by this library. Existing formatter size caps and frozen-frame plain
text fallback remain documented product limits.
