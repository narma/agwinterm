# WebView2 release validation

## Local parity extension — published-build evidence (2026-11-01)

Remote display/replication was explicitly deferred on 2026-10-31 to a future
OpenSpec change, likely using agterm/zmx interoperability. Manual IME and
clean/offline Windows checks remain pending by user decision. The measurements
and earlier acceptance history below this section describe the preceding builds.

`artifacts/webview2-parity-published-integration.log` passes independent split
pages, pane swaps/collapse, mixed program/web slots, history and original/current
reload, general local commands with explicit cross-window targeting, blocking
submit/cancel/timeout exits, companion ownership/refusals and PNG/JPEG/GIF/WebP.
Single, batch and workspace Undo preserve page IDs and document state; expiry,
explicit `undoable:false` and parent teardown of parked companions release pages.
Cookies, localStorage and IndexedDB survive close/reopen and application restart;
clear refuses Undo-retained persistent pages and then removes all three stores.

`artifacts/webview2-parity-published-cookbook.log` passes 180 assertions, including
native input/focus, settings, synthetic DPI, theme changes without DOM reload,
local declarative forms with document scripts disabled, child-frame isolation,
actual Ctrl-click link opening, current-URL copying, external confirmation/cancel
suppression and reset by native keyboard input. All four cookbook fixtures run,
including annotation submission/cancel/backup recovery, busy terminal plus dashboard
and snapshot copying. Missing-runtime simulation preserves existing program overlays.

The host gate passes temporary profile isolation, native file-paste and chooser
refusal, permission/download/popup refusal, creation cancellation and profile cleanup
(`artifacts/webview2-parity-host-gate.log`). Core tests: 569; PTY/CLI tests: 1,560.
The five opt-in WSL local-source tests pass, including canonical external-file
resolution and symlink escape refusal. Logs: `webview2-parity-core.log`,
`webview2-parity-pty-full.log`, `webview2-parity-local-source-wsl.log` in `artifacts/`.
Environment: .NET SDK 10.0.303, Windows 11 build 26300, WebView2 Runtime 156.0.4314.58.

Twenty warm-cache starts on the same machine and Balanced power plan give median
485.65 ms and p95 523.84 ms versus baseline 479.01/516.96 ms. Both gates pass:
limits are 502.96/568.65 ms. Every terminal-only start has zero browser processes.
Installer: 32,639,378 bytes; portable: 76,517,658 bytes; staged payload: 97,633,935
bytes. Evidence: `artifacts/webview2-parity-startup.json` and `.log`.

Fresh published-payload resource measurements aggregate the owned process tree;
CPU is percent of one core over one second, and GPU is committed memory counters.

| State | Browsers | Private RAM MiB | CPU % | GPU MiB | Temporary profiles |
| --- | ---: | ---: | ---: | ---: | ---: |
| Terminal only | 0 | 108.16 | 35 | 23.09 | 0 |
| 1 page, visible | 6 | 274.45 | 11 | 47.43 | 1 |
| 1 page, hidden | 6 | 269.03 | 3 | 41.50 | 1 |
| 5 pages, one visible | 10 | 447.05 | 12 | 184.70 | 5 |
| 5 pages, hidden | 10 | 451.79 | 5 | 151.06 | 5 |
| 10 pages, one visible | 15 | 665.92 | 5 | 329.67 | 10 |
| 10 pages, hidden | 15 | 663.79 | 0 | 293.14 | 10 |
| All closed, 5 seconds | 0 | 194.87 | 0 | 23.10 | 0 |
| All closed, 30 seconds | 0 | 166.81 | 0 | 23.10 | 0 |
| 30 cycles, settled | 0 | 113.54 | 0 | 23.10 | 0 |

The all-closed samples retain ten terminals; repeated cycles use one. All six
five-cycle checkpoints have zero browsers/profiles. RAM varies from 111.87 to
124.24 MiB and settles at 113.54 MiB without sustained growth. Shared cache reaches
11.49 MiB at cycle 20 and remains flat. First document request-to-loaded is 553.73
ms (browser first-contentful-paint 260 ms); the second is 361.18 ms (156 ms paint).
These timings have different starting points. Raw CPU/RAM/GPU, load and cleanup
samples are in `artifacts/webview2-parity-resources.json` and `.log`.

The portable EXE also passes the full 180-assertion host/cookbook suite from a
fresh folder containing only that EXE. A fresh `DOTNET_BUNDLE_EXTRACT_BASE_DIR`
contains the bundled 194,912-byte WebView2Loader.dll; no adjacent SDK/loader is
required. Evidence: `artifacts/webview2-parity-portable-cookbook.log` and
`webview2-parity-portable-directory.txt`. This validates extraction on this machine,
not the explicitly pending clean/offline Windows acceptance.

Local installation was updated on 2026-11-01 through `installer/update-gui.ps1`
with GUI and CLI enabled: eight files replaced, previous files backed up, existing
Rust ptyhost and ConPTY preserved. An isolated installed-instance smoke test opens
two independent loaded pane pages and closes cleanly; the existing ptyhost remains
running. Installed GUI/CLI/Core/PTY assemblies and WebView2Loader match the validated
installer stage by SHA-256. Logs: `artifacts/webview2-parity-local-update.log` and
`artifacts/webview2-parity-installed-smoke.log`. GUI was left closed for the user.

## Navigation policy revision after local review

The current policy follows agterm's two modes at upstream commit
`714ec616c81add35627a1f8c3d5cb50ab9e030a1`: default original-origin pinning and explicit
URL-only `--browse` for cross-origin HTTP(S) redirects/links. Browse mode is reported
in tree, identifies the committed site in native UI and conflicts with chromeless.
Local/review grants remain bounded. External clicked links request native confirmation;
Cancel is the default and suppresses repeated requests from that page. Automatic
popups stay blocked. The earlier experiment that enabled browsing by default has
been superseded; `--url https://google.com --js --browse` is the intended command.

560 Core and all 1,542 PTY/CLI tests pass. The published GUI/CLI pair passes the full
native web/cookbook suite: both redirect modes, tree/current-site identity, history,
denied file navigation, native external confirmation, cancellation suppression and
dismissal on hide, plus all local review/storage/recovery scenarios. The first
published run encountered a transient Unicode-focus probe failure; the unchanged
probe passed on the complete rerun. Evidence: `artifacts/webview2-browse-core.log`,
`webview2-browse-pty-full.log`, `webview2-browse-published-cookbook.log`.
A real Google smoke test verifies the default redirect is refused with the browse
hint and `--browse` loads `https://www.google.com/` with stable page identity and
correct mode readback (`artifacts/webview2-google-browse-modes.json`).
The package and performance figures below describe the preceding release-validation
build; later navigation validation is recorded separately.

Measured 2026-10-31 on Windows 11 Pro 26300, .NET SDK 10.0.303,
WebView2 SDK 1.0.4314.35 and Evergreen Runtime 156.0.4314.58.
The baseline machine, GPU drivers, power plan and protocol are recorded in
[webview2-baseline.md](webview2-baseline.md). This is a development acceptance
record, not evidence from a clean Windows machine.

## Terminal startup and packaging

Twenty sequential warm-cache terminal starts, same machine and protocol:

| Measurement | Before | After |
| --- | ---: | ---: |
| Terminal-ready median | 479.00545 ms | 470.50845 ms |
| Terminal-ready p95 | 516.9571 ms | 506.191 ms |
| Browser processes during terminal-only starts | 0 | 0 |
| Installer bytes | 32,322,815 | 32,603,866 |
| Portable bytes | 76,014,575 | 76,427,719 |
| Staged application payload bytes | 95,693,400 | 97,512,719 |
| Actual upgraded installation bytes | 100,809,817 | 102,776,259 |

Both startup thresholds pass (median: max(5%, 20 ms); p95: max(10%, 50 ms)).
The installed-size measurement includes uninstall files and retained files from
an upgrade; it is not a clean-install footprint. The final installer completed
successfully and contains the native loader and explicit runtime-install helper.
Runtime binaries and browser cache are separate from these application figures.

Evidence: `artifacts/webview2-before.json`, `webview2-after.json`,
`webview2-installed-after.json`, `webview2-final-install.log`,
`webview2-final-installer.log`, `webview2-final-portable.log`.

## Page resources and cleanup

Final installer-payload measurement uses a private application identity. RAM and
GPU figures below aggregate the owned process tree; CPU is a one-second sample
expressed as percent of one core. GPU committed memory is a Windows per-process
counter, not a promise of exclusive physical VRAM consumption.

| State | Browser processes | Private RAM MiB | CPU % | GPU committed MiB | Temporary profiles |
| --- | ---: | ---: | ---: | ---: | ---: |
| Terminal only | 0 | 106.39 | 23 | 23.24 | 0 |
| 1 page, visible | 6 | 279.09 | 15 | 80.78 | 1 |
| 1 page, hidden | 6 | 279.47 | 3 | 48.49 | 1 |
| 5 pages, one visible | 10 | 451.83 | 3 | 188.72 | 5 |
| 5 pages, all hidden | 10 | 463.29 | 0 | 155.64 | 5 |
| 10 pages, one visible | 15 | 681.46 | 8 | 335.03 | 10 |
| 10 pages, all hidden | 15 | 672.49 | 6 | 301.82 | 10 |
| All closed, 5 seconds | 0 | 191.42 | 2 | 23.10 | 0 |
| All closed, 30 seconds | 0 | 163.43 | 0 | 23.10 | 0 |
| 30 cycles, settled | 0 | 111.82 | 0 | 23.10 | 0 |

The all-closed samples still include ten terminal sessions. Nine extra sessions
are then removed before the repeated-cycle experiment. At cycle 5, the three-second
sample still sees a delayed browser shutdown (5 processes, 1 profile); cycle 10 and
every later sample see zero browsers/profiles. Private RAM at cycles 10/15/20/25/30
is 114.45/114.45/114.07/112.86/112.09 MiB, falling to 111.82 MiB after settling.
There is no sustained post-warmup growth in this run. Common cache reaches 11.49 MiB
and stays flat from cycle 15 onward; it is retained separately from deleted page
profiles. The final process tree contains only the application, cmd and conhost.

First web request in a fresh application profile reaches loaded state in
5,954.22 ms; the browser reports first contentful paint 556 ms after navigation.
The next document reaches loaded state in 347.88 ms with paint at 184 ms.
Three reloads take 329.26/176.07/179.91 ms, with paint at 136/76/68 ms. Request
timings include CLI and asynchronous host startup; browser paint timings have a
different starting point. No universal memory or cold-page latency limit is implied.

Evidence: `artifacts/webview2-page-measurements.json` and `.log`. The JSON retains
per-process command lines, working sets, all GPU memory/engine counters and every
load sample. The startup, bounded shutdown and repeated-cycle cleanup gates pass.

## Functional evidence

- 532 Core and 1,533 PTY/CLI tests pass. The latter run explicitly enables Ubuntu
  resource tests, including paths with spaces/Unicode and symlink boundaries.
- The production slot coordinator is unit tested for concurrent reservations,
  program/web refusal, delayed HUD replacement and stale owner teardown.
- The native host gate verifies same-origin storage isolation, reload persistence,
  cancellation, permission/download/popup policy, browser shutdown and removal of
  temporary profile directories.
- The cookbook test exercises all four document templates; real review file/block
  annotations, empty Submit, Cancel, timeout/recovery, backup output and two-page
  notes/draft isolation; htmx controls/refresh; snapshot search/details/copy.
- Native UI checks cover palette/settings/switcher/ask overlap, UIA, Unicode typing,
  Tab navigation, focus return, resize, accepted synthetic DPI changes and theme
  updates. Browser fault injection/reload, closing an owner window during async
  creation, and normal restart without restored web pages pass.

Evidence: `artifacts/webview2-final-core-tests.log`,
`webview2-final-pty-tests.log`, `webview2-policy-host-gate.log`,
`webview2-cookbook-test.log`.

## Legacy checks and limitations

Program overlays, 51 HUD checks and 34 native Ask checks pass. The unchanged WSL
suite fails at “Duplicate opens the Linux cwd”: after closing a focused split,
the duplicate inherits `/tmp` rather than the original pane's directory. The same
failure was reproduced using the pre-feature portable executable. It is not
reported as a passing assertion. A separate continuation runner records this
known baseline failure and completes the remaining WSL scenarios successfully,
including overlay lifetime, native Ask cancellation, Linux hooks and real restart.

The published installer payload passes the full cookbook suite. Under a burst of
20,000 terminal lines, the dashboard responds to Run in 146 ms; terminal output
reaches its final marker and subsequent typed input is processed. This is a scoped
responsiveness smoke test, not a general throughput benchmark.
Evidence: `artifacts/webview2-published-cookbook.log`.

The isolated single-file portable passes the same complete cookbook suite,
including missing-runtime refusal, restart and the busy-terminal test (146 ms
dashboard response). Only the exe is placed in the isolated application folder;
the CLI is the separately published test driver. Native loader extraction is
observed under an explicit fresh `DOTNET_BUNDLE_EXTRACT_BASE_DIR`, including the
194,912-byte x64 loader. Evidence: `artifacts/webview2-portable-cookbook.log` and
`webview2-loader-extraction.json`. No adjacent SDK or loader was required by the exe.

Evidence: `artifacts/webview2-program-regression.log`, `webview2-legacy-Hud.log`,
`webview2-legacy-Ask.log`, `webview2-legacy-wsl.log`, `webview2-baseline-wsl.log`,
`webview2-wsl-continuation.log`. The original WSL test has not been weakened.

IME composition is reserved for the user's manual test on another machine;
synthetic DPI evidence was explicitly accepted. No IME or physical mixed-monitor
pass is claimed. A genuinely clean missing-runtime machine and a disconnected
machine are not available here. A private missing-runtime override verifies
actionable refusal and preserved terminal/program overlays, but is not equivalent
to those clean/offline machine checks. These manual release items remain tracked.
The user explicitly instructed that clean/offline validation remain unfinished
while all other checks proceed; it has not been waived or marked as passed.
