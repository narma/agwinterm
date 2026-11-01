# Tasks

## 1. Runtime and hosting acceptance gate

Acceptance decision (2026-10-31): the user accepts synthetic DPI checks and will
test IME manually elsewhere. Proceed beyond 1.4 without waiting for that result;
retain its checkbox as pending until manual IME evidence is available.

The user also explicitly chose to leave clean/offline Windows validation unfinished
and continue all other checks. Tasks 1.5 and 6.3 retain pending checkboxes for that
validation; simulated missing-runtime checks do not replace it. Evidence for the
original scope is in docs/webview2-release-validation.md. Section 7 extends the
scope following the upstream audit and explicit user approval of full parity.

- [x] 1.1 Capture the pre-change Release startup/package baseline using the design's 20-run protocol; deliver machine/version metadata, median/p95 and installer/portable/installed byte counts.
- [x] 1.2 Add the pinned WebView2 Core dependency, lazy runtime detection and STA host boundary in Win32; verify terminal-only launch creates no browser processes and missing-runtime web requests preserve existing surfaces.
- [x] 1.3 Build a session-bound local-page vertical slice with asynchronous creation/cancellation and temporary page profiles; verify close-during-create disposal, same-page localStorage after reload and isolation between two pages.
- [ ] 1.4 Exercise child-HWND hosting against palette/settings/switcher, native ask, clipping, resize, mixed DPI, IME and UIA; deliver interaction evidence and implement/test the design's composition fallback if windowed presentation cannot pass before proceeding.
- [ ] 1.5 Add installer/portable loader packaging and explicit runtime-install entry point; verify clean machines with present/missing runtime and offline terminal operation, then document dependency and extraction behavior in installer/user guidance.

## 2. Page model and control contracts

- [x] 2.1 Add page identity/load/visibility/geometry and slot-arbitration state; unit-test program/web refusals, HUD replacement after validation, concurrent opens and owner teardown without changing existing program transitions.
- [x] 2.2 Add typed host/control/CLI HTML and URL operations, JS/bridge/navigation/chromeless validation and strict routing; extend CLI/conformance tests for duplicate/missing/conflicting options, unsupported panes, raw invalid types and window-target mismatches.
- [x] 2.3 Add `result.pageID`, additive `htmlOverlays` tree snapshots and kind-aware close/hide/show/toggle/resize; verify background readback and effective visibility without selecting an owner, preserving program result and overlayDetails shapes.
- [x] 2.4 Document the new CLI/wire fields and refusal matrix in control-api documentation and installed agent guidance; execute documented open/tree/close examples through the actual CLI.

## 3. Document loading and native interaction

- [x] 3.1 Implement local HTTPS-origin resource serving and target-context Windows/WSL paths; test sibling assets, Unicode/spaces, unavailable distribution, traversal, junction and symlink escapes with IO off the UI thread.
- [x] 3.2 Match agterm navigation: default origin pinning, explicit URL-only --browse with readback/current-site identity, chromeless conflicts, confirmed external handoff and popup/redirect/permission/download policy; test default refusals, browse redirects/history, prompt cancellation and unchanged local/review grants.
- [ ] 3.3 Integrate native navigation strip, chromeless close, DIP sizing and zoom readback; verify clipping, focused editor typing, shortcuts, Tab traversal, IME, screen reader visibility and focus return with no duplicate terminal input.
- [x] 3.4 Add theme variables and agterm aliases through live updates, user-gesture clipboard copy and DOM-preserving hide/show; verify saved notes and unsaved drafts across theme/DPI/resize/session switching, and snapshot copy output.
- [x] 3.5 Implement renderer failure/reload and complete controller/profile cleanup on close/owner removal; verify stale callbacks cannot update replacement pages and pages are absent after restart, then document lifecycle and navigation limits.

## 4. Review bridge and outcome recovery

- [x] 4.1 Implement explicit `--bridge review`, asynchronous JS aliases and the isolated two-command handler; test URL/subframe/target-override/unknown-command denial, malformed or oversized payloads and stale-document messages after reload/navigation.
- [x] 4.2 Implement page outcomes and `result --page` with first-completion-wins and the 32-finished-page retention bound; test empty submission, cancel, submit/close races, hidden pending pages, owner deletion, evicted IDs and legacy program result regression.
- [x] 4.3 Verify per-page temporary storage remains through reload and is released on close; test two reviews cannot read each other's notes and that theme updates do not reload either page.
- [x] 4.4 Document bridge grant/schema, result retention and timeout/backup recovery, with executable submit/cancel examples; verify ordinary CLI exit codes remain distinct from the review adapter's cancellation exit code.

## 5. Cookbook compatibility fixtures

- [x] 5.1 Add pinned-source provenance/notices and adapted fixtures for all four html-doc-overlay templates plus show/reload logic; verify theme, native navigation, background opening, loading errors and refusal over a hidden program.
- [x] 5.2 Adapt annotate-document script/template with explicit bridge grant and Agwinterm environment/transport/settings mapping; run a full block/file annotation round trip, empty Submit, Cancel, fit/zoom, timeout recovery and output-file backup checks.
- [x] 5.3 Add a pinned localhost dashboard fixture representative of scheduled-jobs' htmx frontend; verify run/toggle/filter and five-second refresh with JS enabled and no host bridge. Document that scheduler installation is outside this change.
- [x] 5.4 Add the standalone session-snapshot viewer fixture with search/details/copy and an embedded launch adapter; verify copied restore text without executing it and document upstream's external-browser behavior.
- [x] 5.5 Update cookbook parity documentation with the four-row matrix, exact tested baseline and deliberate differences; verify it distinguishes web coverage from full recipe ports, publishing, scheduling and the remaining 42 recipes.

## 6. Integrated release checks

- [x] 6.1 Run focused existing program-overlay, HUD, native-dialog, CLI routing and WSL integration suites alongside new web tests; retain results showing no legacy reply/lifetime/input regressions.
- [x] 6.2 Repeat startup baseline and 0/1/5/10-page measurements across visible/hidden/closed states, first/repeat loads and 30 open/close cycles; deliver process-tree CPU/RAM/GPU/package results and meet the design's startup and cleanup gates.
- [ ] 6.3 Run cookbook fixtures in published installer and portable builds, including missing-runtime/offline cases and busy-terminal plus dashboard responsiveness; attach final evidence and record tested SDK/runtime/OS versions before marking implementation complete.

## 7. Upstream web-overlay parity (user approved 2026-10-31)

Baseline remains agterm 714ec616c81add35627a1f8c3d5cb50ab9e030a1. Earlier
completed tasks describe the first implementation, not acceptance of this extension.

- [x] 7.1 Match navigation lifecycle: original-source CLI reload versus --current/toolbar reload, deferred window.close, about navigation, clicked redirects, external destination selection, and cancellation suppression reset by real user input; verify native integration and CLI contracts.
- [x] 7.2 Match local file grants: self-contained HTML by default, explicit --cwd canonical directory grant with root/home rejection, Windows/WSL boundaries; deny file chooser, file paste and external file drops and test actual browser behavior.
- [x] 7.3 Support independent pane web slots and session-wide pages with splits: open, target, hide/show/close, history/reload, geometry, swaps and hidden splits, mixed program/web and two-web cases; verify owner teardown and stale callbacks.
- [x] 7.4 Add explicit URL-only --persistent shared browser storage and scoped storage clearing; verify cookies/localStorage/IndexedDB across page close and application restart while preserving temporary review isolation.
- [x] 7.5 Match native page chrome and readback: identity/title/close unless chromeless, history availability, current reload, browser, copy link/reveal file and hide; shared persisted web zoom with host font commands, theme variables and default local styling; test focus, shortcuts and DOM retention.
- [x] 7.6 Add upstream-compatible trusted local declarative controls and asynchronous general command bridge with upstream command restrictions, live session/pane/window context, explicit targeting and document/frame validation; retain the bounded review mode and test URL/forbidden/stale requests.
- [x] 7.7 Add local HTML --block exact-page outcomes and exits 0/2/1, preserving existing program waiters; exercise submit, empty submit, cancel, timeout and owner teardown.
- [x] 7.8 Add bounded image overlays and shell-less companion sessions with independent ownership and command restrictions; test image formats/limits, parent coexistence and lifecycle.
Remote display/replication was explicitly removed from this change by the user on
2026-10-31: choose the transport later (likely agterm/zmx compatibility) and implement
it in a separate OpenSpec change. This is deferred scope, not completed parity.
- [x] 7.10 Match soft-close/undo page lifetime and browser-link opening policy; verify controller/DOM retention on undo, final cleanup and ephemeral pages across restart.
- [x] 7.11 Update API/installed guidance/parity matrix and run integrated published-build acceptance and resource/startup regression checks for the expanded scope; retain explicit pending manual IME and clean/offline machine checks.
