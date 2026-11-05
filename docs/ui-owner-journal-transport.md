# Journal transport optimization

## Baseline profile

Baseline commit 4083c34, pinned journal-v9-app. Reproduce the diagnostic breakdown:

```powershell
./tools/journal-transport-report.ps1 -Directory artifacts/single-owner/journal-v9-pilot/results/evidence -Output artifacts/single-owner/journal-v9-transport-profile.json
```

Whole-process sampled intervals (include startup/warmup; different from fixed-work acceptance intervals):

| Diagnostic | Host-owner | UI-owner with checkpoints |
|---|---:|---:|
| UI CPU, s | 3.16 | 9.84 |
| Host CPU, s | 2.30 | 4.77 |
| UI managed allocations, MB | 23.50 | 264.84 |
| Host managed allocations, MB | 29.88 | 206.52 |
| Native feed elapsed total, s | 0.37 | 0.33 |
| UI capture-lock elapsed total, s | about 0.01 | about 0.01 |
| T4–T5 p50, ms | 24.49 | 40.84 |
| T5–T6 p50, ms | 8.69 | 13.84 |
| T7–T8 p50, ms | 15.58 | 1.35 |

T4–T5 includes producer work, transport and feeding the final marker; T5–T6 waits for frame capture. Percentile segments do not add to the end-to-end percentile. Stage 23 is output byte counts, never elapsed time. Feed/lock timers measure elapsed time, not exclusive CPU: they cannot establish a precise CPU stack attribution. The measurements nevertheless identify a large allocation/transport gap, not slower native parsing. The first controlled change replaces nested async reads and per-record header/payload arrays with the existing dedicated overlapped IO and reusable validated framing storage on the UI read thread.

The payload is borrowed until the next read and is fully consumed by Feed/RestoreSnapshot first. Large checkpoint storage remains bounded and independent. Event identity and feed boundaries are unchanged; no batching timer or acknowledgement is introduced. Tests cover fragmented/coalesced records, maximum output, larger checkpoints, invalid headers, partial EOF and cancellation with buffered data.

## Reader-only experiment (v10)

`journal-v10-pilot` completed 22 contracts, 11 lifetime checks and all four 200-event variants. 87 host-free checks passed. Relative to the preceding v9 pilot, UI-owner/checkpoints-on fixed-work CPU fell from 11.61 to 7.14 seconds, presentation p50 from 64.50 to 48.89 ms, and input p50 stayed near 0.30 ms. The contemporaneous host-owner presentation p50 was 49.26 ms and CPU 3.86 seconds. These are separate single-pair pilots, not an acceptance CI or isolated causal CPU profile.

Whole-process UI managed allocation fell from 264.84 to 115.02 MB and sampled UI CPU from 9.84 to 4.50 seconds. Broker allocation/CPU remained near 209 MB / 4.94 seconds. T4–T5 p50 fell from 40.84 to 26.97 ms. This supports retaining the reader optimization and testing the broker publisher as the second bounded step.

v11 uses the same existing overlapped writer on a dedicated publisher thread. A cancellation-aware Monitor wait replaces per-event async publisher continuations; the journal no longer allocates a new TaskCompletionSource for every append when no asynchronous reader is waiting. Data, completion, disposal and cancellation wakeups have explicit host-free regression tests. Async readers still rearm correctly. No event merging, batching timer, changed renderer cadence or user-input acknowledgement is introduced.

## Rejected publisher experiment (v11)

The v11 pilot passed 89 host-free checks plus the same 22 contracts and 11 lifetime checks. Broker managed allocation fell to 140 MB and whole-process CPU to 4.00 seconds, but UI CPU rose to 5.91 seconds. Fixed-work combined CPU was 7.52 seconds, versus 7.14 in v10; T4–T5 p50 rose to 38.44 ms, versus 26.97. Presentation p50 remained 48.90 ms, illustrating why the end-to-end median alone is insufficient. These single-pair observations do not prove a regression, but provide no clear reason to retain the added publisher mechanism. The entire v11 publisher/signal experiment was reverted; final source and measurements select v10 reader-only. Its pinned binaries and evidence remain available independently.

## Final trace-disabled bulk comparison

`journal-v10-bulk-series`: five runs per variant, 128 MiB each, rotating three-way order, 15/15 completed through the installed Hyper-V worker. Identical pinned fixtures and grids; CPU includes UI + broker, excludes producer and conhost. Completion polling is 100 ms; these are throughput observations, not latency measurements.

| Mean metric | Legacy | Host-owner | UI-owner v10 |
|---|---:|---:|---:|
| CPU interval, seconds | 31.825–31.878 | 6.188–6.231 | 18.019–18.097 |
| Elapsed seconds | 27.385 | 10.004 | 14.181 |
| Throughput, decimal MB/s | 5.488 | 13.454 | 9.466 |
| Private memory, decimal MB | 88.63 | 76.29 | 106.57 |
| Maximum simultaneous sampled private memory, MB | 106.57 | 84.34 | 123.68 |

Against legacy, both CPU gates pass: host-owner conservative ratio CI 0.179–0.214; UI-owner 0.534–0.606. Throughput CIs against legacy remain inconclusive because of baseline run variability; no outlier is excluded.

The direct UI-owner/host-owner comparison is decisive on this workload: even the conservative CPU lower ratio CI is **2.778–3.010**, failing the 1.05 non-regression limit. Throughput ratio CI is **0.653–0.758**, failing the 0.95 limit. Memory ratio is 1.397 (1.379–1.415), informational. This comparison uses matched runs from the same series, not the earlier v9 baseline.

The earlier v9 five-pair UI-owner series averaged 29.484–29.606 CPU seconds and 6.428 MB/s: v10 materially improves the observed result, but the previous and current series are not interleaved before/after pairs, so do not attach a paired CI to that cross-version delta. Reproduction: `session-bulk-report.ps1 -Directory artifacts/single-owner/journal-v10-bulk-series/results/evidence -BaselineVariant single-owner -Output artifacts/single-owner/journal-v10-bulk-vs-host-owner.json`; omit BaselineVariant for the legacy comparison (`journal-v10-bulk-report.json`).

## Final fixed-output latency comparison

`journal-v10-fixed-series`: five rotating pairs of four variants (legacy, host-owner, journal/off, journal/on), 200 events each. All 20 runs plus the 22 contract and 11 lifetime preflight checks completed. Values are means of per-run post-warmup percentiles. There are 900 post-warmup samples per variant, below the predefined 10,000-sample latency acceptance threshold: **all formal latency gates remain inconclusive**, even where the paired-run CI below excludes zero.

| Metric | Legacy | Host-owner | UI-owner v10 + checkpoints |
|---|---:|---:|---:|
| Input p50 / p95 / p99, ms | 0.245 / 1.381 / 2.980 | 0.677 / 1.392 / 2.610 | 0.309 / 1.220 / 2.873 |
| Present p50 / p95 / p99, ms | 70.713 / 87.005 / 141.162 | 49.188 / 68.461 / 90.238 | 48.801 / 64.710 / 74.372 |
| Traced fixed-work CPU, s | 12.044 | 3.947 | 7.459 |
| Active output, MB/s (traced) | 5.248 | 9.385 | 8.627 |
| Mean private memory, MB | 84.50 | 81.44 | 96.73 |

UI-owner versus host-owner input p50 delta is −0.368 ms (95% CI −0.407 to −0.330); present p50 −0.387 ms (−0.555 to −0.219). Present p95 CI spans −12.281 to +4.778 ms, p99 −29.860 to −1.871 ms. Input-tail intervals remain broad. The former roughly +15 ms presentation-median gap is absent in this repeated series, without changing the rendering policy.

Checkpoint-on versus off input p50 delta is +0.005 ms (−0.018 to +0.028); present p50 +0.022 ms (−0.124 to +0.167). Tail intervals remain inconclusive. Across runs, capture-lock means are 0.15–0.17 ms, maximum 0.90 ms; commit-lock means 0.98–1.22 ms, maximum 4.91 ms. One capture acquisition waited 24.53 ms for a paint to finish; that is outside the capture lock and is retained in the evidence. Snapshot size remains only 3.3–3.5 KiB for this fixture; no claim about large-scrollback/image capture follows. Retained journal high-water stays around 8.4 MB.

Reports: `journal-v10-fixed-vs-legacy.json`, `journal-v10-fixed-vs-host-owner.json`, `journal-v10-checkpoints-comparison.json`, `journal-v10-checkpoints.json` under `artifacts/single-owner`.

## Final sparse-input comparison

`journal-v10-idle-series`: five rotating three-way runs, 2050 keys per run with 50 warmup keys excluded, **10,000 correlated T0–T8 samples per variant**, 15/15 completed. This series meets the existing sample-count threshold.

| Metric | Legacy | Host-owner | UI-owner v10 |
|---|---:|---:|---:|
| Input p50 / p95 / p99, ms | 0.269 / 2.167 / 4.666 | 0.265 / 0.796 / 1.424 | 0.377 / 1.273 / 2.237 |
| Present p50 / p95 / p99, ms | 1.780 / 16.713 / 26.653 | 1.588 / 2.567 / 5.776 | 1.551 / 2.644 / 5.253 |
| Mean private memory, MB | 62.77 | 68.83 | 64.23 |

Against legacy, UI-owner passes the input-median and all three presentation non-regression gates; input tails remain inconclusive. Against host-owner, input and presentation medians pass; both sets of tails remain inconclusive. UI-owner/host-owner input-median delta is +0.112 ms (95% CI −0.115 to +0.339), input p95 +0.477 ms (+0.097 to +0.856). Present median delta is −0.037 ms (−0.327 to +0.253), p95 +0.076 ms (−0.750 to +0.903). The journal's roughly 0.37 ms input advantage under fixed-output load does not extend to ordinary idle typing.

Reports: `journal-v10-idle-vs-legacy.json`, `journal-v10-idle-vs-host-owner.json`.

## Verification and decision

Final reader-only source passes **87 host-free tests**, the bulk report tests (including reversed baseline, CPU observation bounds and missing-pair rejection), nine latency-analyzer checks, Release x64 build and strict OpenSpec validation. The rebuilt Win32/Core/Pty DLL hashes exactly match pinned `journal-v10-app` after reverting v11. The final fixed-output preflight passed 22 protocol/recovery checks and 11 window-lifetime checks; recovery observed 808.8 ms, retaining the original shell. All 50 final measurement runs completed (15 bulk, 20 fixed-output, 15 idle), and raw evidence/manifests are retained. No UI tests used the user's desktop.

**Recommendation: continue the host-owner migration and target its input path under load next.** The reader optimization is worthwhile and retained in the opt-in journal prototype: it removes the former roughly 15 ms heavy-output presentation gap and reduces journal CPU substantially. However, the trace-disabled matched series still shows about 2.9× CPU and about 30% lower throughput than host-owner. Ordinary echo presentation is essentially comparable and journal input is not faster there. The remaining journal benefit is approximately 0.37 ms earlier input delivery under fixed-output load, with unresolved tail acceptance. Under the agreed combined latency/performance priorities, that does not outweigh the measured steady-state CPU/throughput gap.

This is a completed bounded optimization/comparison, not a default rollout or completion of the original host-owner migration. Both prototypes remain opt-in. Next host-owner work should isolate queue/lock/transport delay on the loaded input path and retain these same before/after fixtures; a language rewrite is not justified by these measurements alone.
