# Host-owner completion and upgrade

PR #36 targets `codex/ghostty-vt-foundation`, from which the host-owner
branch was created at `83d82b1`. On 2026-11-06 the maintainer approved
squash integration with a reduced performance gate. Historical experiment
results below retain their original sample counts and confidence verdicts.

## Integration evidence

The v31 candidate passed migration from installed 0.20.19 and the Ghostty
preview, real Windows default-terminal handoff, and all seven Windows UI
suites in both hosted and local modes. The Mac SSH, WSL/ZMX survival and
WSL origin-export suites passed on the same application build. Windows CI
ran 118 hosted contracts, including a fresh attachment recovering an image
over 1 MiB, alternate-screen selection and completed-command output.

| Pinned v31 comparison | Legacy hosted | Host-owner |
| --- | ---: | ---: |
| Continuous ASCII, child input p50 / p95 / p99, ms | 0.224 / 0.914 / 1.809 | 0.570 / 1.615 / 2.738 |
| Continuous ASCII, EndDraw p50 / p95 / p99, ms | 9.57 / 16.53 / 21.37 | 4.64 / 9.09 / 16.31 |
| Bulk output, MiB/s | 5.15 | 10.47 |
| Combined CPU per 128 MiB, seconds | 32.62 | 7.05 |
| Combined peak private memory, MiB | 99.30 | 83.78 |

The ASCII results use five alternating pairs and 10,000 measured echoes
per variant. Their confidence gates are inconclusive. The installer
SHA-256 is `D5F24244108EB3A612B48B392C246767F5BF6259B904F498D28ACA682C1B27C4`.
The portable SHA-256 is `8B91DF38BF5011A036C4D923B331467D269ABEAABF085D2A1E64E5CF1E5F0124`.
Raw samples, package hashes, run configurations and paired intervals are
under `artifacts/single-owner/host-completion-v31-final`.

The maintainer accepted the measured input/throughput tradeoff. Exhaustive
burst and workload permutations, additional noise runs and a trace-overhead
sweep can continue separately. The first v30 32-session run had an
unexplained timeout. Its full 20-minute repeat delivered 17,836 correlated
echoes with the same production binaries. Both runs remain in the record.

## Compatibility scope

The shipped Windows terminal will use Ghostty and the application session host,
with direct ownership retained for serverless/handoff surfaces. Retiring the old
Windows Rust host does **not** retire the Rust Linux helper, WSL, ZMX, or remote
sessions. Their helper deployment, transport identity, recovery and lifecycle
contracts remain required. Hosted ZMX and remote import must accept the new
`server` configuration instead of requiring a `server-rust` preference.
The version-1 ZMX readiness payload still reports the historical
`host: "server-rust"` label to clients. Both the Linux helper and the
Windows inventory emit that wire value. The effective Windows session
backend for this configuration is `server`. The GUI starts `Agwinterm.Host.exe`
on demand and reconnects to its existing pipe after a GUI-only update.

Upgrade compatibility means saved-state import, not attaching to old Windows
hosts. The installer must checkpoint the previous UI, preserve a backup, close
the previous UI and hosts, and restore layout/settings/history in the new version.
Running Windows programs are terminated; saved terminal state is not execution
resumption. The user explicitly accepted this boundary. The currently installed
application has only been inspected; no live user session was stopped.

Read-only installed-version evidence is in
`artifacts/single-owner/host-completion-upgrade/installed-before.json`:
installed UI `0.20.19+b48dbb9`, plus Ghostty preview `1.0.0+a9efbfe`.

## Fresh baseline before completion changes

Raw evidence: `artifacts/single-owner/host-completion-before-fixed`.
Report: `report.json` inside that directory. Immutable candidate package:
`artifacts/single-owner/journal-v10-app`. Five sequential pairs, 2,050 events
per run, first 50 excluded: **10,000 samples per variant**. Windows Hyper-V worker,
91×32 grid, fixed 256 KiB output per event, single-key input, tracing enabled.
All ten runs passed; worker test time was 1,751.292 seconds.

Values below are arithmetic means of per-run percentiles, not pooled percentiles.
Presentation ends at `EndDraw`, not physical display scanout. CPU and memory are
combined host/UI, excluding the echo child. CPU values are sampled interval CPU.

| Metric | Legacy replica | Host-owner before completion |
| --- | ---: | ---: |
| Input p50 / p95 / p99, ms | 0.301 / 1.071 / 1.984 | 0.900 / 1.650 / 2.351 |
| Presentation p50 / p95 / p99, ms | 71.092 / 87.577 / 287.956 | 49.264 / 65.439 / 81.048 |
| CPU per run, seconds | 139.100 | 41.059 |
| Mean private memory, MiB | 76.881 | 77.102 |
| Active output, MiB/s | 4.678 | 9.580 |

Predeclared paired confidence gates: presentation p50 and fixed-work CPU pass;
input percentiles, presentation p95/p99 and throughput remain inconclusive.
The high legacy tail/throughput variability is retained in the evidence. This
series used legacy-first ordering in every pair, so it is not an alternating-order
revision comparison. Dedicated trace-disabled bulk runs are still required.

## Revision comparison

`SessionPerformance` and `SessionBulk` now support `RevisionComparison`: both
packages run the same owner architecture, labelled `before` and `after`, reversing
order on even pairs. The executable under `baseline/` is pinned independently.
Raw files retain actual architecture, hashes, and a separate measurement label.

An early five-pair, 200-event fixed-output comparison completed at
`artifacts/single-owner/host-completion-v2-early-fixed`. Input p50 was
1.028 → 1.033 ms; presentation p50 49.318 → 49.326 ms; mean private memory
78.07 → 78.53 MiB. All confidence gates remain inconclusive.

The trace-disabled v4 bulk comparison (five alternating pairs, 128 MiB per run)
completed at `artifacts/single-owner/host-completion-v4-early-bulk/report.json`.
Mean throughput was 10.36 → 10.02 MiB/s, CPU upper estimate 6.76 → 6.89 seconds,
and mean peak private memory 80.57 → 81.51 MiB. Performance gates remain
inconclusive. These are development checks, not final rollout acceptance.
The failed v3 bulk run is excluded: its package selector and cached readback
were corrected before the v4 comparison.

The v19 revision check includes the owner-only runtime gate, startup input
queue and retained history after child exit. Five alternating fixed-output
pairs, 180 measured events per run (900 per variant), are in
`artifacts/single-owner/host-completion-v19-early-fixed/report.json`.

| Mean of per-run metrics | Before | v19 |
| --- | ---: | ---: |
| Input p50 / p95 / p99, ms | 0.910 / 1.700 / 3.627 | 0.893 / 1.697 / 3.413 |
| EndDraw p50 / p95 / p99, ms | 49.203 / 74.739 / 137.951 | 49.104 / 68.177 / 87.371 |
| Combined interval CPU, seconds | 3.969 | 3.706 |
| Mean private memory, MiB | 78.288 | 78.280 |

The revision comparison uses host-owner on both sides. All six latency gates
remain inconclusive; the shorter series is a development check.

Trace-off fixed work (`host-completion-v19-early-bulk/report.json`, five
alternating 128 MiB pairs): throughput 12.08 → 11.62 MiB/s, upper CPU
estimate 5.94 → 6.22 s, mean peak private bytes 80.00 → 84.40 MiB.
The CPU and throughput gates are inconclusive; the measured throughput
decrease and CPU increase require final comparison, not a speedup claim.

The v21 architecture comparison uses the original pinned legacy binaries and
the same echo fixture for both variants. Five alternating 128 MiB pairs per
mode, no tracing, 91×32 grid. Reports:
`host-completion-v21-bulk-server/report.json` and
`host-completion-v21-bulk-in-process/report.json`. Values are run means.

| Mode | Throughput, MiB/s | CPU upper estimate, s | Mean peak private memory, MiB |
| --- | ---: | ---: | ---: |
| Hosted legacy → host-owner | 5.30 → 12.78 | 31.50 → 6.23 | 99.82 → 83.13 |
| Direct legacy → local adapter | 9.65 → 10.38 | 10.01 → 9.09 | 64.04 → 65.83 |

The throughput and CPU non-regression gates passed in both modes. The
hosted paired throughput ratio has a wide 95% interval (1.08–4.32);
the ratio of the arithmetic means is 2.41. Memory is informational.
This measurement predates the multi-window and dense-history review fixes.

## Prototype separation

The complete UI-owner journal experiment is retained on
`codex/ui-owner-ghostty-journal` at `9e78958`. Its runtime, protocol negotiation,
UI switches and dedicated tests are removed from the host-owner completion branch.
Historical comparison report readers still accept its archived measurement label.
Host-owner uses a separate .NET process with the native Ghostty C wrapper;
a Rust host rewrite is not part of this change. The Rust Linux helper remains.

## Validation still required

Development validation on 2026-11-06:

- `host-completion-v9-hosted-ui`: 80 checks passed in the installed Hyper-V
  worker, including hosted selection across resize, hidden-session readback,
  Unicode search, image rendering and snapshot/reattach contracts.
- `host-owner-v9.trx`: 196 protocol, input lifecycle, buffer-transfer, effect,
  SurfaceText and backend tests passed. This includes the startup focus race:
  input arriving before attachment is bounded and delivered once after attach.
- `host-owner-v11-control.trx`: 295 control/readback tests passed after moving
  owner history and image waits off the UI executor.
- `host-owner-v11-core.trx`: 24 frame, configuration and reliable-effect tests
  passed, including a role notice arriving before the pane installs its filter.
- `host-owner-completion-v11-survival` passed on the private WSL desktop:
  unchanged workload/daemon incarnation across UI quit, forced exit and host
  loss; no replay; exact termination, fresh start and detach; scratch launch;
  immediate create/close races; two-window checkpoint restore; split/swap and
  survivor identity; missing dependency refusal and Recent reopen. Explicit
  reads now release cached-frame credit even when a window never paints.
  Distribution shutdown remains unverified: it requires a dedicated disposable
  distribution. No user distribution was shut down.

The migration fixture uses a copy of installed version 0.20.19 under the guest's
test package, a private application identity and state directory. It does not
stop or upgrade the developer's installed application. History preservation
respects the previous `restore-buffer` setting; the fixture explicitly enables
it. The upgrade helper refuses a headless old host without a matching UI to
checkpoint, before closing any UI. It backs up and hash-verifies saved state
before closing the old installation; old process execution is intentionally
not continued across this migration.

The OpenSpec task list remains authoritative. In particular, complete selection,
accessibility and persistence migration; installer save/close/import; removal of
old shipped Windows backends; WSL/ZMX/remote recovery; full before/after latency
and trace-disabled CPU/throughput; bounded-memory soak; and current-commit review
must finish before this branch is declared ready.

The v20 installed-version and preview upgrade fixtures both passed, including
refused backup while the previous UI remained usable, successful retry, ordered
split pane identities, context, Unicode, explicit RGB foreground, and no
duplicate history on restart (`host-completion-v20-installed-style`,
`host-completion-v20-preview-style`). The v20 hosted contract harness passed
94 checks and released every counted native resource on disposal. The full
Core suite passed 670 tests; 1,911 host-free Pty tests passed. The dedicated
process suite is separate.

The v20 private-desktop WSL/ZMX survival and WSL origin export suites passed.
The latter covered independent input to split panes, UI loss and exact restart,
partial split refusal and invalidation after explicit origin termination. These
runs do not attest a real remote Mac peer or distribution shutdown.
Installer and portable were rebuilt; standalone portable extraction verified
the shipped hashes and both Linux ELF architectures without installing the app.

The v26 private-desktop WSL/ZMX and WSL origin suites passed. The real Mac
peer also passed in `host-owner-completion-v26-real-remote-final`. It used the
isolated agterm socket and pinned Rust origin bridge. A follower kept the same
workloads across Windows host loss and six SSH outages. The fault fixture denied
the actual right pane of the split and dropped one input acknowledgement. The
left pane remained usable and received its input exactly once. The terminal
harness verified all 90 unique history markers across physical row wraps and
exact reattachment with styled wide cells, alternate screen and follower geometry.

The v28 isolated search workload held the outstanding viewport credit while
256 keys produced 1 MiB of child output. After resize the next viewport
contained the final marker. Across 500 additional ordered echoes in a
40,000-row history, all 250 search-enabled submissions overlapped a query.
Client-observed echo to decoded viewport, in milliseconds:

| Condition | p50 | p95 | p99 |
| --- | ---: | ---: | ---: |
| Without search | 31.58 | 32.56 | 34.45 |
| With search | 32.10 | 83.56 | 220.82 |

This includes polling and viewport publication. It measures responsiveness
during search separately from the T1-to-child and T1-to-EndDraw measurements.
Raw samples are in `host-completion-v28-search-load/results/evidence/owner-search/samples.csv`.
The installed and preview v28 migration runs preserved the last window's
live shell across rapid closes. The real Mac, WSL/ZMX and origin suites
also passed on v28.

The v30 candidate passed all seven routed UI suites in each mode
(Navigation, Ghostty, Command, Clipboard, Presentation, Accessibility and
Quick), 112 host contract checks, and both real upgrade fixtures. In a
fresh Hyper-V guest, a normal user launch of cmd was handed to the running
window by Windows. The original pane remained hosted, the handed-off pane
used a direct owner, and its child echoed interactive input. The guest's
bootstrap uses an elevated token, so the fixture runs the per-user COM
activation under its limited interactive token.

The standalone native fixture passed 240 checks, including search during
owner disposal. Real Mac recovery, WSL/ZMX survival and WSL origin export
passed on v30. Standalone portable extraction verified the runnable CLI,
native payload hashes, and both Linux helper architectures. The installer
SHA-256 is `466F8B28208EE00600BC2275B5593AB8341F27F62D013BDB3C301BB8641ABEAB`
and the portable SHA-256 is `657FD3941644F1B5E16FB61C8AF9BC9511F686D9615A55AF91AE5ACC2F0E090A`.
The pinned measurement inputs and individual run reports live under
`artifacts/single-owner/host-completion-v30-final`.

### V30 primary hosted measurements

Five alternating pairs, fixed 256 KiB output per key, 10,000 post-warmup
echoes per variant. Latencies are means of per-run percentiles in milliseconds.

| Metric | Legacy | Host-owner | Gate |
| --- | ---: | ---: | --- |
| Child input p50 | 0.278 | 0.907 | Inconclusive |
| Child input p95 | 1.056 | 1.898 | Inconclusive |
| Child input p99 | 1.720 | 2.763 | Inconclusive |
| EndDraw p50 | 70.950 | 49.247 | Pass |
| EndDraw p95 | 90.424 | 68.421 | Inconclusive |
| EndDraw p99 | 280.013 | 84.387 | Inconclusive |

In the separate trace-disabled 128 MiB runs, hosted throughput averaged
5.08 → 12.43 MiB/s, combined CPU 32.42 → 6.48 seconds and combined peak
private memory 101.38 → 83.32 MiB. All five throughput pairs improved.
The paired ratio's 95% lower bound was 0.9467 against the 0.95 gate.
For direct local sessions the corresponding results were 9.77 → 10.27
MiB/s, 9.79 → 9.05 seconds CPU and 65.90 → 64.20 MiB peak.

### V30 soak

| Sessions | Completed echoes in 20 minutes | Combined CPU, s | First / last quarter median private MiB | Echo p95 / p99, ms |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 18,254 | 51.84 | 58.32 / 58.24 | 32.29 / 44.63 |
| 8 | 18,212 | 55.81 | 95.98 / 98.32 | 32.30 / 46.92 |
| 32, repeat | 17,836 | 63.94 | 235.84 / 244.16 | 32.32 / 44.93 |

The first 32-session attempt timed out waiting for slot 16's 328th echo.
Its last successful observation was at 688.713 seconds. The repeat used
the same production binaries and a fixture with separate counters for child
input consumption and output flush on timeout. It completed with all markers.
The original timeout remains unexplained. Keep the failed attempt alongside
the successful runs when evaluating the rollout gate. These echo timings
include headless polling to decoded viewport, with no UI EndDraw.

### Continuous ASCII investigation

In the original saturated ASCII test, legacy → v30 hosted EndDraw p50 was
11.67 → 102.75 ms. All five pairs showed a presentation regression. The
child's input thread and its continuous log thread competed to write to the
same synchronized output writer. Of the approximately 100 ms in the hosted
median, the T4-to-T5 median was 70–81 ms across the five runs. The separate
T7-to-T8 median was about 15 ms. In the instrumented reproduction, two of
three hosted runs spent 56–69 ms waiting in the child before it acquired the
writer. Once it acquired the writer, the median marker write took 0.03–0.04 ms.
Raw contention results remain in `host-completion-v30-final` and
`host-completion-ascii-v30-probe`.

The revised fixture still saturates stdout. A single writer checks for
accepted echoes between complete log chunks, instead of depending on the
runtime's monitor scheduling. The same compiled echo fixture runs against
both architectures. Separate short pilots compared the v30 and v31 UI:
v30 EndDraw p50 was 21.32 ms (paired legacy 10.05), and v31 measured 9.17 ms
(paired legacy 9.97). V31 lets received frames paint promptly for 120 ms
after interactive input because a frame already in flight can precede the
echo. Both pilots used three pairs and 350 events per run. They are diagnostic
observations; the full acceptance series is `host-completion-v31-final`.
