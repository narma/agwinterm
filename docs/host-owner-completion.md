# Host-owner completion and upgrade

Work branch: `codex/host-owner-ghostty-completion`. Integration target selected by
the user: `codex/ghostty-vt-foundation` (`83d82b1`), rather than the fork's default
branch. This is a work-in-progress record, not rollout approval.

## Compatibility scope

The shipped Windows terminal will use Ghostty and the application session host,
with direct ownership retained for serverless/handoff surfaces. Retiring the old
Windows Rust host does **not** retire the Rust Linux helper, WSL, ZMX, or remote
sessions. Their helper deployment, transport identity, recovery and lifecycle
contracts remain required. Hosted ZMX and remote import must accept the new
`server` configuration instead of requiring a `server-rust` preference.

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
