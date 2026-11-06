# Single-owner measurement preparation

Implementation branch: `codex/single-owner-ghostty-sessions`.
Baseline source: `83d82b1c55501bac97e5995d50910df59a8e7bf3`.

An explicit, non-default text/input prototype is under measurement. OpenSpec
tasks 1.1, 1.2, 2.1 and the consumer inventory 2.5 are complete; remaining tasks are not marked complete by this prototype.
Input-to-present acceptance, the paired A/C and B/D matrix,
and rollout remain pending; render timings below do not satisfy those gates.

## Resource counters

Set `AGWINTERM_GHOSTTY_COUNTERS=1` before starting each measured process. Read
`GhosttyDiagnostics.Snapshot()` in that process. Sum host and UI snapshots for a
hosted session. The switch is sampled once, and disabled instrumentation performs
no counter locking, logging or per-feed allocation.

Counters distinguish native owners, legacy replicas, active frame captures and
search handles. Feed bytes are split between owners and replicas. Allocation and
release counts refer to native terminal handles, **not native heap allocations or
bytes**. A search handle can keep a terminal alive after session disposal; the
terminal counter decrements when its native SafeHandle actually releases. Frame
capture counts end after completion/abort, including deferred terminal disposal.
No terminal contents are included in the counters.

Validation: 228 native contract checks passed, including deferred capture
disposal and per-role feed accounting. The routed Ghostty suite passed 82 UI
checks plus the server harness. It observed one owner/one replica, owner-only
after detach, and zero native resources after host/client disposal. Evidence:
`artifacts/single-owner/native-diagnostics.log` and
`artifacts/single-owner/diagnostics-native/results`.

`ServerSession` classifies both initial and replacement Ghostty cores as legacy
replicas before feeding output. In-process cores and host cores remain owners.
Current hosted mode is still the original owner-plus-replica implementation.

## Initial render evidence

The unmodified Release build and recursive SHA-256 manifest are retained in
`artifacts/single-owner/baseline/app` and `hashes.json`; `commit.txt` pins source.
The existing `TerminalPerformance` suite ran with Ghostty on the installed
Hyper-V worker: 96 checks passed. Raw samples and the guest configuration are in
`artifacts/single-owner/baseline/render-worker`; derived ranges are in
`artifacts/single-owner/baseline/render-summary.json`.

For ASCII with ligatures disabled, the three round medians ranged from 1.98 to
2.46 ms and p95 from 2.77 to 3.40 ms. These are full-window render timings,
including EndDraw. This fixture forces repaint and is unsuitable for measuring
echo latency. EndDraw is not physical display time. One invocation is not a
baseline noise study or a regression verdict.

The post-counter build (counters disabled) also passed all 96 render checks.
ASCII without ligatures had median ranges 2.11–3.05 ms and p95 3.17–4.54 ms,
versus 1.98–2.46 ms and 2.77–3.40 ms before. Evidence is in
`artifacts/single-owner/diagnostics-render` and
`artifacts/single-owner/diagnostics-render-summary.json`. The later invocation
was slower; this must not be reported as a non-regression pass. With only one
invocation per build, no paired baseline-noise study and no confidence bounds,
the performance result is **inconclusive**, pending task 1.4. Neither invocation
measures input latency or the future single-owner implementation.

Reproduce the render fixture with an unused output directory:

```powershell
& tests/integration/hyperv-ui.ps1 -Suite TerminalPerformance `
    -TestParameters @{EmulatorCore='ghostty'} `
    -OutputDirectory artifacts/single-owner/render-reproduction
```

Run comparisons sequentially on the same worker configuration. The native
contract harness runs without a desktop. The server harness must be published
under the staged application's `ghostty-server-tests` directory and invoked by
the routed `Ghostty` suite; its ConPTY tests must not run on the user desktop.

## Outstanding measurement work

The compiled echo fixture and routed `TerminalLatency` suite now correlate T0–T8
using unique input/output IDs. T6–T8 identify the same successfully submitted
frame, and its actual captured cells must contain the marker. The baseline's T6
and T7 coincide because its viewport is captured/applied in the UI; T5 is always
the owner parse. Trace analysis rejects missing/duplicate events, loss, mismatched
clock frequencies, backwards causality and mismatched frame IDs. Nine C# marker
checks and seven analyzer failure checks pass. Routed local, hosted and Unicode
output pilots pass; failed pilot and VM-start attempts are retained separately.

The first hosted idle noise study contains ten sequential runs in one worker VM,
forming five pairs with 2,000 measured events per run after 50 warmup events.
Evidence: `artifacts/single-owner/noise-hosted-idle`; report:
`artifacts/single-owner/noise-hosted-idle-report.json`. Input p50/p95/p99 and
input-to-present p50/p95 resolve the predeclared tolerances. Present p99 remains
inconclusive (paired difference 95% interval approximately -4.73 to +9.64 ms).
No p99 non-regression claim is justified by this noise study.

Metrics include combined owner/UI private bytes and CPU time, with the echo child
excluded, plus native budget-allocator counters, GC, feed/lock timings and IPC
bytes. Native allocator accounting excludes CRT/COM, the small C wrapper, and
Ghostty's directly allocated screen/history pages. Combined process private bytes
already includes them. New supplemental `nativePageResident`/`nativePageVirtual`
counters query Ghostty's memory-usage API on the owner-locked info path at most
every 250 ms; older reports without these columns do not provide full native
page attribution. Image bytes are a separate subset and must not be added twice.
These sampled CPU intervals are not yet fixed-output-work acceptance results.

At the user's request, the next decision checkpoint prioritizes latency **and**
fixed-work CPU, throughput and matched-content memory on the minimal text/input
single-owner prototype, before migrating richer consumers. The complete matrix
and long soak remain required before rollout; the pinned old-path binaries remain
available for later paired measurements. Preliminary/inconclusive gates cannot
enable the new path by default.

Task 1.3 still requires trace-disabled overhead comparisons and full metric validation.
Task 1.4 must then pin instrumented A/B binaries and collect the complete paired
baseline/noise matrix before ownership changes. Keep the predeclared tolerances
in the OpenSpec design unchanged until measured baseline noise is reviewed.

## Early architecture checkpoint (user-requested priority)

The local idle noise study also completed: ten runs, five pairs, 20,000 measured
events total. All six latency noise gates passed; this is baseline repeatability,
not evidence about the prototype. See `noise-local-idle-report.json`.

The first long fixed-output run exposed a **measurement harness** failure:
per-feed traces grew to 233 MB and PowerShell's eager Get-Content exhausted the
4-GiB VM while analyzing them. That run is not accepted. High-frequency stage
20+ observations now use cumulative count/total/max/log2 histograms sampled to
`timings-PID.csv`; T0–T8 remain individually correlated. The analyzer streams only
trace/driver files with FileShare.ReadWrite and disposes readers even on invalid
input. Seven analyzer rejection checks pass. A preliminary ReadLines sharing-mode
failure is retained separately; its raw 100-event traces validate offline.

`early-baseline-app` preserves the legacy Win32/Pty binaries, replacing only Core
with the same aggregated instrumentation used by the candidate. The added frame
types are unused by the baseline. Both sides use the same compiled echo fixture
and native binaries. Separate recursive SHA256 manifests pin each package.

The experimental switch is `AGWINTERM_SINGLE_OWNER=1`, only for Ghostty server
sessions, using the separate `-presentation-v1` namespace. It is not a rollout:
history queries, graphics, full consumer migration and legacy adoption routing
remain unfinished. No existing shell is migrated by these tests.

First fixed-work comparison: 100 events, 10 excluded as warmup, 23,592,960 bytes
of deterministic producer output per side. Results below are **exploratory**, one
run per side; every statistical gate is inconclusive, not passed.

| Metric | Legacy baseline | Prototype v1 | Prototype v2 |
|---|---:|---:|---:|
| Combined sampled CPU seconds (lower bound) | 6.516 | 9.359 | 2.266 |
| Combined sampled CPU seconds (upper bound) | 6.953 | 9.844 | 2.672 |
| Combined mean private MiB | 84.28 | 77.23 | 83.01 |
| Active output MB/s (T4–T5) | 4.93 | 3.83 | 10.17 |
| Input-to-present p50 ms | 63.62 | 79.92 | 47.33 |
| Input-to-present p95 ms | 98.64 | 112.38 | 65.06 |
| Input-to-child p95 ms | 0.888 | 2.402 | 2.138 |

V1 performed excessive captures while output arrived faster than the UI rendered.
V2 grants the publisher one frame credit initially and after successful EndDraw;
output only coalesces a wakeup while credit is exhausted. There is no fixed frame
timer and no pipe write under the owner feed lock. V2 also pools decode buffers,
removes raw-output copying on this path, and services owner input on a dedicated
writer. This reverses the preliminary CPU/throughput regression, but the input
tail remains unresolved. V3 is testing buffered record reads to avoid separate
overlapped reads for each input message prefix/payload. Rich migration waits on
this checkpoint. Idle memory must be measured separately; the earlier tiny idle
pilot showed an increase, so the loaded result cannot stand in for that gate.

Evidence: `early-fixed-report.json`, `early-v2-fixed-report.json`, their raw VM
directories and package manifests under `artifacts/single-owner`. Owner metrics
confirm one host owner and zero UI owners/replicas. Native frame harness now passes
235 checks; frame/input codec tests pass 11 checks. All production defaults remain
unchanged while acceptance is unresolved.

## Early paired transport experiments (v3–v6)

The user's requested early architecture checkpoint takes precedence over completing
all baseline workloads before prototyping. Legacy binaries remain pinned, and
richer feature migration remains pending until this checkpoint is resolved.
`SessionPerformance` runs baseline and candidate sequentially in the same worker
VM, using the same fixture, grid and native instrumentation. Each pilot below has
2,050 events per side, excluding the first 50. One pair is insufficient for the
predeclared five-pair confidence gates; these are diagnostic results, not acceptance.

| Pilot | Baseline input p95 ms | Candidate input p95 ms | Baseline private MB | Candidate private MB |
|---|---:|---:|---:|---:|
| v3 idle | 0.337 | 1.648 | 63.54 | 80.65 |
| v4 idle | 0.296 | 1.525 | 63.29 | 75.03 |
| v5 idle | 0.391 | 1.407 | 63.69 | 74.15 |
| v6 idle | 0.351 | 1.602 | 64.62 | 73.30 |
| v6 history | 0.340 | 1.480 | 86.29 | 82.03 |

Private bytes are summed across UI and host, averaged over the measured interval;
MB here means decimal MB. V4 replaces shared cell pools with bounded session pools.
V5 batches immediately available input records without a timer. V6 uses a dedicated
buffered host input reader, eliminating per-record prefix/payload allocations.
Idle sampled CPU was 6.219 seconds baseline versus 5.125 seconds v6; history CPU
was 5.609 versus 5.266 seconds. These observer-paced CPU numbers are descriptive;
fixed-work runs are required for the CPU acceptance gate.

V6 measures Ghostty page allocations separately from the budget allocator. Resident
page bytes in the history pilot were 7,946,240 baseline versus 3,973,120 candidate.
The fixture emits 50,000 lines, with the existing 5,000-line retention limit on both
sides. These counters demonstrate removal of duplicated history pages, but overall
idle private memory still regresses. Pages are sampled at most every 250 ms while
the owner updates native info; they are not a replacement for process private bytes.
Image and allocator counters must not be blindly added because their scopes overlap.

V6 idle T1–T2 p95 was 1.189 ms versus 0.082 ms baseline; T2–T4 was 0.430 versus
0.287 ms. Owner encode-lock time averages about 4 microseconds and owner input queue
age about 32 microseconds. Capture/publication T5–T7 remains short. The next pilot
removes the extra sender-thread wakeup by submitting overlapped writes directly
when idle; a full pipe yields asynchronously to the same bounded ordered queue.

Latest checks: 238 native contract checks; 16 frame/input codec and sender tests;
82 routed Ghostty UI checks and 48 routed server lifecycle checks (prototype
contract package). Evidence, raw traces and SHA256 manifests are retained under
`artifacts/single-owner/paired-v*-*`. Production defaults remain unchanged.

V7 (inline overlapped sender) and V8 (committed-text semantics fix) did not resolve
input tails: candidate p95 remained 1.37 and 1.52 ms respectively. The committed
text fix is a compatibility correction: WM_CHAR/IME is native text, while physical
key press/release still uses owner Win32 input mode. The message-loop translation
hook now recognizes presentation sessions before TranslateMessage as well.

V9 adds T9 at UI semantic Enter submission and T10 at host decode, without changing
T0–T8 or benchmark pacing. `tools/session-input-stage-report.ps1` validates these
extra identities/order and reports diagnostic distributions. V9's first worker
attempt failed its prepared-desktop check before starting tests; its retry passed.
The retry is diagnostic only: host unit checks overlapped part of its run, so it
must not be used for statistical acceptance. Candidate stage p95: T1–T9 0.04 ms,
T9–T10 0.98 ms, T10–T2 0.09 ms, T2–T4 0.41 ms. The transport dominates the extra
input delay. V10 tests a dedicated reader using event-completed Windows overlapped
I/O, draining cancellation before releasing the buffer/OVERLAPPED. The event's low
bit suppresses managed IOCP notification for that operation. This follows Windows
[completion notification](https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-getqueuedcompletionstatus)
and [cancellation lifetime](https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-cancelioex)
requirements; ordinary pipeline bounds and session ownership remain in force.

Validation before v10: 664 Core tests and 1,950 Pty tests passed. New direct-pipe
partial-read/EOF and repeated cancellation-race tests pass alongside input tests
(14 selected tests). New owner committed-text/physical-key checks are added to the
routed server harness and await its next VM run. The consumer inventory is in
`docs/single-owner-consumer-checklist.md`; it explicitly lists unfinished migrations.

## User priority update

On 2026-11-05 the user explicitly clarified that modest memory growth is acceptable;
input/output latency and overall performance have priority. The fixed +5% memory
cutoff is therefore removed as an independent rollout gate in the design and
reporter. Memory remains measured and disclosed, with leaks/unbounded growth still
blocking acceptance. No new numerical allowance is assumed. Historical report
files are preserved as originally produced; their earlier memory gate policy is
superseded by this instruction. Latency, CPU and throughput gates are unchanged.

## Input transport diagnosis through v13

V10 reduced transport delay but moved the tail to the next writer handoff. V11
lets the dedicated reader drain the same ordered PTY queue when its writer is
idle; native reply callbacks still only enqueue under the feed lock. V12 also
used event-completed writes, but retained a sender-thread hop for every key.
Neither passed the early idle latency checkpoint.

V13 starts overlapped writes directly from the persistent UI thread when idle.
The call never waits; pending completion and following queued records belong to
the dedicated writer. Other/transient callers always queue (Windows can abort
pending pipe IO when its issuing thread exits). Tests cover actual named-pipe
ordering, slow-reader cancellation and pin lifetime across this handoff.

In the v13 pilot, T9–T10 p95 dropped to 0.04 ms (from 0.98 ms in v9), and owner
T10–T2 p95 was 0.01 ms. However T2–T4 p95 was 1.12 ms: records already decoded from
one pipe read were being written to ConPTY one at a time. V14 drains at most 32
complete already-buffered records into the ordered writer before emitting bytes.
No sleep, batching timer or new key round trip is introduced. V13 overall input
p95 was 1.20 ms versus baseline 0.31 ms, and present p95 16.69 versus 16.16 ms;
this remains a diagnostic single pair, not accepted performance evidence.

V11 memory breakdown found approximately 17.7 MiB committed GC heap in the new
host, versus 6.3 MiB currently live allocations before the baseline host's first
GC (its post-GC committed counter was still zero and cannot represent zero heap).
Combined v13 mean private memory was 73.21 MB versus 64.19 MB. Per the user's
priority update, this bounded overhead is reported as a tradeoff while latency
remains the active blocker; memory tuning will not displace the input/output work.

The five-pair v14 idle series completed: 10,000 post-warmup events per side.
Input p50 and present p95 meet their confidence-bound gates. Input p95/p99 and
present p50/p99 are inconclusive, not passing: input p95 mean difference is
+0.58 ms with 95% CI [+0.16,+0.99]; input p99 +0.87 ms [+0.49,+1.24]. Present
p99 varies between roughly 16 and 31 ms even in baseline, consistent with the
existing UI repaint timer/cap. Full tables and raw samples are in
`paired-v14-idle-confidence-report.json`. Fixed-output five-pair collection follows
sequentially in a fresh worker; the app packages remain unchanged.

The next source revision gives the first actual presentation update after accepted
interactive input priority over the existing flood repaint cap. It does not paint
on input, predict echo, inspect fixture markers, or change benchmark injection.
It consumes one per-pane hint when a complete owner frame arrives; later continuous
output keeps the existing cap. A pending redraw timer is cancelled when this real
update is promoted. This is a production scheduling change for the experimental
path, and any improvement must be attributed to it as well as ownership/transport.
Its latency and CPU effect still require measurement.

V14's five-pair fixed-output attempt stopped in the first candidate run at echo 4;
its complete baseline and failed candidate evidence are preserved. T9 was recorded
but T10 was absent: a queued frame credit could be overtaken by an immediate UI
submission because enqueueing did not reserve the writer before releasing its
gate. The host correctly rejected the resulting nonconsecutive sequence. V15
reserves the writer for every queued record; a real-pipe regression test mixes
1,000 queued/immediate submissions and verifies exact decoded order. Do not count
the failed output attempt as a completed comparison or discard it from the log.

Before V15: 23 selected Pty frame/input/direct-IO checks and 13 Core frame/trace
checks passed; Win32 and server harness builds succeeded. OpenSpec 2.1 is now
complete: contracts are exposed alongside ISession, immutable generation/lifetime
checks pass, and ownership/threading is documented in `single-owner-contracts.md`.
The direct local adapter and complete UI migration remain pending. V15 runs the
failure-triggering fixed-output pair first, then idle, with unchanged baseline.

V15 completed both pilots (2,000 post-warmup events per side). Fixed output:
combined CPU lower bounds 135.281 s legacy / 44.953 s candidate (upper bounds
135.438 / 45.125 s), active output 4.742 / 9.742 MB/s, present p95
112.23 / 64.54 ms and p99 174.65 / 80.78 ms. Input p95 was 0.269 / 0.462 ms
and p99 0.420 / 0.770 ms. Combined private memory was 80.49 / 80.91 MB.
The idle marker-burst pilot instead exposed input tails: p95 0.35 / 1.33 ms,
p99 0.79 / 2.54 ms, despite present p95 improving 16.95 / 2.83 ms and p99
31.35 / 6.09 ms. These single pairs are exploratory, not confidence-bound acceptance.

The original fixture sends 13 WM_CHAR marker characters before its timed Enter.
That remains the default `marker-burst` workload. V16 adds an independently
reported `single-key` input pattern: only Enter is injected; the compiled child
counts Enter downs and emits the same real marker, validated at all T0–T8 stages.
This separates ordinary key latency from the original burst's queueing. Both
variants use the same updated trace/fixture Core; baseline Win32/Pty remain pinned.
Reports key by input pattern and never pool these workloads. Old burst failures
are retained and still require investigation.

The V16 single-key idle series completed five pairs, 10,000 post-warmup samples
per side (`paired-v16-single-key-report.json`). Mean per-run p95 present fell
13.819 to 2.675 ms, paired difference 95% CI [-16.650,-5.638] ms; p99 fell
23.517 to 5.004 ms, CI [-28.462,-8.565]. All present gates pass. Input p50
and p95 pass; input p99 remains inconclusive (difference -1.404 ms,
CI [-4.406,+1.598], tolerance 1 ms). Private memory means were 62.65 / 68.94 MB,
ratio 1.1005, CI [1.0875,1.1135]. Descriptive CPU was 4.225 / 4.809 s; idle
timing runs are not a fixed-output CPU acceptance measurement. A five-pair
V16 fixed-output/single-key series follows sequentially without concurrent builds.

After pinning V16, source work extends the direct local adapter into keyboard,
mouse, paste and focus dispatch. Raw writes join its same ordered writer, avoiding
interleaving with terminal replies. Local text paint captures through the immutable
frame contract outside the feed lock; graphics still use the existing local native
capture's image view pending task 5. These changes are not in the pinned V16
measurement package and require routed local UI/input validation and B/D timings.

Further unmeasured source work adds a versioned viewport record (resize, pixel cell
size, scroll and visibility), full-frame recovery on revision changes, and UI
rejection/credit release for stale revisions. Attachment cancellation and input
acceptance share a separate fence; input acceptance does not wait for the frame
capture gate. Semantic mouse records now carry both pixel and cell coordinates so
the owner chooses the current protocol mode. These additions still need compiled
and routed validation; no corresponding OpenSpec tasks are checked complete.

The measurement harness now also has a `fixed-bulk` producer with external
UI/owner CPU and private-memory sampling. `SessionBulk` can compare ownership
with instrumentation disabled, or off/counters/full tracing for a single variant.
It uses cached completion readback at 100 ms intervals and makes no latency claim.
The reporter's synthetic confidence/gate and unpaired-run checks pass; actual bulk
runs remain pending. The ordinary echo harness additionally records optional
stage 11 before marker priming, reporting whole-burst and preamble duration while
preserving the original T0–T8 gates. Nine analyzer validity checks pass.

The user raised a UI-owned Ghostty plus host byte-journal alternative while the
V16 fixed-output series was running. No architecture switch has been made.
Comparison needs to cover detached terminal queries/replies, UI-produced checkpoints,
bounded journal retention, resize order and replay without repeated side effects.
The existing prototype and pinned measurements are retained for that decision.
