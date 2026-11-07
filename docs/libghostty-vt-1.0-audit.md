# libghostty-vt 1.0.0 integration audit

Checked 2026-11-07 against tag vt-1.0.0 (0fa3d577a5614150ff8e2c152de5fb9d5c6d2dec).

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
- Native selection: cell, word, logical-line and rectangular mouse drags now derive their bounds through `ghostty_selection_gesture_*`. A native tracked press origin survives direction reversal and reflow. The UI supplies inclusive cell coordinates, Windows click counts, capture and autoscroll timing; each update evaluates press/drag atomically with that origin. Hosted requests run the identical calculation in the owner. A user-facing command-output selection action remains to be evaluated.
- Formatter: ordinary copy now uses the native plain formatter, preserving Windows CRLF at the boundary; rectangles disable unwrap. Selected-text formatting also powers `copy_selection_html` and `copy_selection_vt`. HTML copies include Windows CF_HTML byte offsets and a Unicode-text fallback. Hosted formatting validates the retained selection before reading current styles. File-export UI and immutable formatted snapshots after content eviction are not implemented.
- Retained content: UI hit testing, highlights, clipboard packaging, hosted transport, immutable selected text and saved frames remain ours. A TUI frame can relocate its saved selection without rebuilding copy text in managed code. Frozen/presented-frame fallback still reads the saved cells because the live Ghostty terminal may already contain output that was never shown. This is intentionally separate from selecting live terminal content. Keyboard mark-mode navigation remains UI-controlled.
- Extended clipboard (OSC 5522, MIME writes, optional reads): NOT implemented. The existing callback queues text writes without protocol acknowledgement. Upstream requires the reply during the callback; the existing asynchronous GUI effects transport cannot report actual clipboard success synchronously. A bounded request/reply path and explicit clipboard-read policy are still needed before claiming support.
- Geometry: installed the native size-query callback with actual cell geometry supplied by resize. Added checks for CSI 14/16/18 t and mode 2048 resize notifications.
- Identity: XTVERSION identifies Agwinterm and the engine release. Kept conservative default DA reports. TN remains unset because no per-session terminfo identity is supplied to the adapter; inventing a name would misreport the remote environment.
- Existing-feature audit: the native contract suite exercises input, graphics, Unicode search, snapshots, synchronized rendering and program status. This is regression coverage, not yet an exhaustive feature-completeness audit.

Adapter ABI is now 7 because the gesture export must not be loaded from an older same-upstream DLL. The upstream C ABI remains 1.0.0.

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
