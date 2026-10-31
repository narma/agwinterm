# Design

## Context

See proposal.md for motivation. The production shell is `src/Agwinterm.Win32`, not the separate WinUI project. `Program.cs` creates an STA Win32 message loop and an `ID2D1HwndRenderTarget`; `Program.Render.cs` draws floating covers, palette and other native surfaces. `Program.OverlayControls.cs` owns program-overlay routing and uses `InvokeOnUiQueued`; `Agwinterm.Pty/ControlServer.cs` serializes tree and dispatches commands. `OverlayOptions.cs`, `ISessionHost.cs`, the Ctl parser and `tests/integration/program-overlay-controls.ps1` provide adjacent contracts and test patterns.

Existing program results are intentionally window-wide for session overlays and slot-scoped for pane overlays. Existing HUDs are passive native content with no HTML. Neither contract becomes a web contract. Introduce a web kind in session slot arbitration; do not represent a page as a terminal or launch a PTY for it.

Cookbook evidence is pinned to agterm `714ec616c81add35627a1f8c3d5cb50ab9e030a1`, reviewed on 2026-10-31. Documentation, authored scripts, tests and HTML templates across 46 recipe directories were scanned; bundled minified vendor JS was identified rather than audited. This is source analysis, not a claim of executed Windows compatibility.

## Goals / Non-Goals

**Goals:** Isolate browser costs from terminal-only operation; use one coherent page identity from CLI acceptance through UI teardown and result recovery; validate the actual cookbook interactions instead of an empty browser control.

**Non-Goals:** Changing the terminal renderer, moving the UI to a web framework, porting every recipe dependency or restoring pages across restart. The user-approved parity extension supersedes the initial session-only, temporary-storage-only and review-only bridge scope; see section 8.

## Decisions

### 1. Evergreen runtime with lazy Core API hosting

Use Microsoft.Web.WebView2 Core APIs directly with the existing HWND and STA message pump; no WPF/WinUI hosting layer. Defer assembly/native loader activation and environment creation until the first valid web request. Asynchronous initialization must never block the message pump; continuations return through the existing UI dispatch mechanism. Pin a tested SDK version during implementation while accepting compatible Evergreen runtime updates.

Use a shared environment for compatible controls in one application identity, with a dedicated user-data directory under that identity's local app-data root. Use unique temporary/InPrivate page profiles to isolate storage while allowing same-page reload; release profile data on close and recover stale application-owned temporary state on next web use. Never reuse a user's Edge profile. Verify actual localStorage behavior in the initial technical gate.

Evergreen avoids shipping a private Chromium copy; it does not eliminate disk or RAM costs on the machine. Detect a missing runtime before committing a slot. Installer and portable builds provide an explicit install-runtime action and useful offline error; no automatic download on terminal startup. Test loader resolution in both published formats, including extraction if the single-file packaging requires it.

Alternatives: Fixed Version adds a private runtime and update responsibility; CEF adds Chromium packaging without a demonstrated resource advantage; Ultralight narrows web API compatibility; Servo adds maturity/integration risk. Reconsider only on measured blockers.

References: [distribution](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution), [threading](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/threading-model), [performance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/performance).

### 2. Windowed host with an explicit composition acceptance gate

Start with a child HWND controller clipped to the session content rectangle. Build the first vertical slice against palette, settings, switcher, HUD, resize, multiple windows, mixed DPI, IME, clipboard and UIA. An HWND can paint above content drawn into the parent Direct2D target; ordinary draw order cannot solve that.

Use separate native popup surfaces where appropriate. Where parent-drawn modal UI must cover the web, temporarily hide only the browser presentation, preserve the controller/DOM, show a static preview if needed and restore focus afterward. No continuous screenshot rendering. If this cannot meet the visibility/input/accessibility spec, implement DirectComposition visual hosting behind the same host boundary before shipping; a nonfunctional palette is not an acceptable fallback. The task sequence includes this bounded branch, not a deferred redesign after feature completion.

References: [hosting modes](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/windowed-vs-visual-hosting).

### 3. Separate page model and transactional slot ownership

Core owns page ID, load state, requested/effective visibility, source, geometry, zoom and outcome records. Win32 owns controller and focus; Pty exposes snapshots and typed requests; Ctl validates operation-specific options. Browser objects never cross into the PTY host process or persistence snapshots.

Resolve and validate target/source/options before reserving the slot; initialize asynchronously against an owner/generation token. Commit only if the owner and slot generation still match. Close during initialization cancels ownership; late callbacks dispose rather than reattach. Program/web occupied slots refuse web open; valid web open can replace HUD only at commit. Program/HUD open into an existing web slot refuses until explicit close. Keep existing non-web transitions unchanged.

Open returns pageID once ownership commits; loading continues independently. Keep original source separately from current navigation URL, and render bounded errors on load/renderer failure. Explicit reload retries the same page identity with a new document generation. Close and owner deletion dismiss pending results and release the controller. Hide/show never replaces a controller. No automatic suspension in v1: dashboards continue background refresh and reviews retain state. Automatic sleeping is a later policy, not an assumed free optimization.

### 4. Additive control surface

| Operation | Contract |
| --- | --- |
| `session overlay open --html PATH` / `--url URL` | Exactly one source; returns `result.pageID`; no positional program |
| `--js` | Enables page scripts, off by default for both source types |
| `--bridge review` | Requires local HTML and JS; explicit recipe adaptation |
| `--navigation` / `--chromeless` | Mutually exclusive native navigation strip / no strip |
| `--size-percent`, `--width`, `--height`, `--follow` | Reuse applicable session-overlay geometry/follow semantics |
| `session overlay reload` | Reload original source; --current reloads shown document; retain page ID/storage |
| `session overlay navigate back\|forward\|browser` | Native history or explicit external handoff |
| `session overlay hide\|show\|toggle\|resize\|close` | Dispatch by surface kind, preserve program contracts |
| `session overlay result --page ID` | Return `result.pageOutcome`; never use the legacy exit-result channel |
| `tree` | Add `htmlOverlays`; preserve existing program `overlayDetails` semantics |

Reject unsupported web pane selectors, program-only cwd/wait/block/background-color/text/copy options and unknown fields rather than ignoring them. Existing CLI exit 0/1/2 semantics and strict window/target routing remain. Ordinary web reads use tree/result polling in v1; no new event stream contract is required. Page IDs are outcome keys, not aliases for terminal input targets.

### 5. Source policy and minimal bridge

Serve local HTML/resources through an application-controlled HTTPS virtual origin unique to page identity, preserving a stable origin across reload. Resolve requested resources against canonical document-directory grants, including Windows junctions and WSL symlinks; do not rely on lexical prefix checks or expose a whole drive. Perform potentially slow WSL IO off the UI thread. Resolve relative files using the target's cwd/profile; document Windows paths and WSL paths with spaces. Local document directory is the grant; broader roots require a later explicit feature.

HTTP(S) pages use normal networking and their own origin; localhost is supported. Match agterm: remote pages pin their original origin (scheme, host, effective port) by default. Explicit URL-only `--browse` permits HTTP(S) redirects and links across origins, including bare-domain to www redirects and sign-in hosts, without manual URL normalization. It does not imply JS or persistence, conflicts with chromeless, is reported in tree, and displays the committed site in a native strip. Chromeless URLs require localhost/127.0.0.1/::1 exactly and restrict their frames to that origin. Remote pages retain no local-resource grant or bridge after navigation. Block non-HTTP(S) main-frame destinations and embedded URL credentials. Clicked off-origin links and user-initiated popups request native confirmation before external handoff; Cancel is the default, denial suppresses further prompts for that page, and hiding/closing cancels the prompt. Automatic popups stay suppressed. Toolbar/CLI browser actions are explicit user handoffs. Local pages stay in their root. Block unexpected schemes, automatic downloads and unsolicited permission grants; offer external handoff for unsupported browser workflows. Remote subresources such as Markdown images can load normally, but never receive local-resource grants or a host bridge.

Only the granted top-level local document receives `agwinterm.request` plus `agterm.request` compatibility alias. Translate submit/close to a dedicated page handler, not the general ControlServer dispatcher. Validate sender origin, main frame, current page/document generation, method, schema and a 1 MiB UTF-8 value limit on every message. Deny target/window overrides. Remove authority on navigation; a new document generation needs a fresh binding. A bridge page blocks navigation to other documents (external links use handoff); reload rebinds the original granted document. This prevents granting authority merely because another file shares a directory.

Record submission before closing. Reject repeat completion; retain the newest 32 finished outcomes in the UI application instance, pending entries are not subject to this limit. The recipe's backup output file handles recovery beyond retention or app restart. Temporary profile storage preserves notes across explicit reload; live theme-variable updates avoid destroying unsaved drafts.

### 6. Cookbook traceability and deliberate differences

All recipe links below resolve within [the pinned cookbook](https://github.com/umputun/agterm/tree/714ec616c81add35627a1f8c3d5cb50ab9e030a1/cookbook).

| Recipe and evidence | Required surface | Acceptance and adaptations |
| --- | --- | --- |
| `html-doc-overlay/scripts/show.sh`, four HTML templates | Static HTML, theme aliases, navigation, reload, `htmlOverlays` state/error, background owner | Open all four templates, reload same file, exercise failure and occupied program slot; adapt AGTERM environment/transport/path handling |
| `annotate-document/scripts/webrev.py`, `assets/page.html` | JS, markdown-it/highlight.js, localStorage, pageID, result, chromeless, DIP geometry/zoom | Add explicit `--bridge review`; submit file/block notes, empty submission, cancel, timeout recovery, saved-note reload and unsaved-draft theme change; adapt settings and pane-status routing |
| `scheduled-jobs/SKILL.md` | HTTP localhost with JS, htmx buttons/filters and five-second refresh | Run a pinned local dashboard fixture and verify actions/refresh; porting launchd/Homebrew/cronn scheduling is outside this change |
| `session-snapshot/snapshot.sh`, `assets/page.html` | Standalone JS viewer, search, details and user copy | Upstream uses macOS `open`, not an embedded overlay. Adapt viewer launch only; ensure copy works. No automatic restore or privileged bridge |

The remaining 42 recipes have no direct embedded-web dependency in the reviewed baseline; `sqlite-browser` uses a terminal program. This does not certify their overall Windows parity. `html-doc-overlay` publishing additionally depends on native ask, Python HTTP hosting and cloudflared; its publish flow and scheduler installation remain separate work. Retain third-party notices for any copied templates and bundled libraries.

### 7. Measurement and release criteria

Record a before/after Release baseline on the same Windows machine, power settings and app state, with at least 20 starts for median/p95. Distinguish terminal ready, first local page paint, repeat page paint and network page timing. Record runtime/SDK/OS/GPU versions. Measure app plus browser process tree, not only the parent working set: private bytes, working set, CPU, GPU and disk/cache growth at 0/1/5/10 pages, hidden and closed, plus after 30 open/close cycles.

Hard gates: no browser processes for terminal-only startup; no synchronous runtime/network initialization on that path; terminal-ready median regression no greater than max(5 percent, 20 ms) and p95 no greater than max(10 percent, 50 ms); renderer/controller counts return to baseline after closes; no repeated-cycle unbounded growth. Report cold/warm page timings and package deltas rather than inventing a universal RAM or page-load target. Investigate any stable post-warmup memory growth and any terminal input/scroll regression under a busy dashboard before acceptance. Common browser/GPU processes may remain briefly while environments release: observe bounded shutdown and distinguish caches from leaked page controllers.

Report installer bytes, portable bytes, installed application bytes, loader extraction, runtime installation and profile/cache separately. A small bootstrapper is not the runtime footprint. Exercise a clean missing-runtime machine and an offline machine without making ordinary terminal use dependent on installation.

### 8. User-approved upstream parity extension

This section supersedes initial-scope limitations in sections 3–6. Match pinned
upstream contracts rather than retaining accidental differences. Track acceptance
independently in tasks 7.1–7.11; earlier evidence does not certify new behavior.

Use stable session/pane slot identity, so swaps move pages with their pane and
hidden splits retain controllers. Session-wide and per-pane slots remain distinct.
URL-only persistent storage uses an application-owned shared profile with explicit
clearing; temporary pages keep isolated profiles. Persist storage/zoom preferences,
not live page sessions. Soft-close retains pages only for the existing undo lifetime.

CLI reload defaults to the original source; --current and toolbar reload target
the shown document. Defer window.close to the UI queue with identity guards.
Allow about navigation without retaining local bridge authority. Preserve user
activation through redirects for confirmed external handoff; denial suppresses
requests until real native input. External actions use the original local file or
shown URL, never the last refused destination. Open files in the default HTTP
browser, not their editor association.

Without --cwd, local HTML can read only its entry document. Explicit --cwd grants a
canonical directory containing that document and rejects filesystem/home roots.
Retain handle-based Windows/WSL escape checks. Block native file selection, external
drops and file paste; browser permissions alone do not enforce these restrictions.

Unless chromeless, expose native identity/title/close; --navigation adds history,
current reload, browser, copy-link/reveal and hide. Add corresponding readback.
Web focus routes font actions to a shared saved zoom value. Preserve review drafts
during theme changes, documenting this compatibility choice; provide default local
styling and remote theme variables without granting remote host authority.

Trusted local HTML supports the pinned declarative controls and asynchronous
general bridge, enforcing upstream command exclusions and current owning context.
Explicit --bridge review retains its narrower contract. Bind every bridge to its
top-level document generation; URL/image/replica pages receive no local authority.
Local --block waits for the exact page outcome and maps submit/dismiss/error to
0/2/1, without reusing program-overlay waiters.

Image pages validate format/decoded dimensions and upstream byte/pixel limits.
Companions are shell-less owned sessions, not dummy PTYs, and reject terminal-only
commands. Remote display/replication is deferred by the user's 2026-10-31 decision
to a separate change; transport selection (likely agterm/zmx) remains open.
Browser link preferences
open URL pages with upstream browse/JS/navigation/persistence policy. These are
required parity tasks, not implied acceptance of existing terminal infrastructure.

## Risks / Trade-offs

- HWND overlap/input defects -> early native UI gate and the defined composition fallback before cookbook rollout.
- Runtime initialization completes after close -> owner and document generation checks, disposal on stale callbacks.
- JS bridge exposes control authority -> opt-in local-document grant, two-command handler and negative cross-frame/navigation tests.
- Temporary profiles increase overhead -> shared compatible environment, process-tree measurements; preserve review isolation rather than silently sharing storage.
- Background dashboards consume CPU -> explicit close and transparent measurements; suspension deferred to a future policy.
- Windows/WSL file access changes or symlink escapes -> canonical grants, bounded asynchronous IO, explicit errors and traversal tests.
- Evergreen updates affect behavior -> record tested runtime and validate newer runtimes; keep native terminal functional independently.

## Migration Plan

Ship additive CLI/API fields and ephemeral web state; no existing settings/session migration is required. Include runtime detection, explicit installation guidance and portable-loader validation. Update control API/user guide/installed agent guidance and cookbook parity notes alongside implementation. Rollback removes the optional web feature without changing persisted terminal state; do not delete a shared Evergreen installation. Runtime and temporary profile artifacts are application-owned cleanup concerns, never a reason to modify user browser data.
