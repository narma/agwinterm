# UI-owned Ghostty journal prototype

Branch: `codex/ui-owner-ghostty-journal`. The earlier host-owner experiment is preserved at `b8e7a9c` on `codex/single-owner-ghostty-sessions`. Neither experiment changes the default backend.

## Contract and scope

Set `AGWINTERM_JOURNAL_OWNER=1` with `emulator-core = ghostty` and `session-host = server` for an isolated experimental instance. The UI owns the only Ghostty; the .NET broker owns ConPTY, raw output/resize events and checkpoint storage. There is no new runtime dependency or language rewrite. The broker loads the existing application package but creates no native terminal or parser.

Journal revision 1 negotiates separate output and ordered input pipes, an attachment epoch and a checkpoint pipe named `<input-pipe>-checkpoint`. Checkpoint transfer never shares the input pipe. Its prefix is the input submission cut (little-endian int64), followed by a framed checkpoint. The broker accepts that cut before publishing the owned snapshot and trimming the covered prefix. A one-byte publication receipt and client receipt complete the upload before pipe reuse. Normal input waits for neither receipt.

Storage is in the surviving broker's memory. Host crash/power-loss recovery is explicitly outside this prototype; the shell itself is also owned by that host. Retention is capped at 64 MiB of accounted events and a 64 MiB checkpoint (these are payload/accounting limits, not a process RSS limit). A slow/absent owner causes backpressure, never arbitrary tail deletion. Automatic snapshots begin after 8 MiB of processed events or 8192 responses. `AGWINTERM_JOURNAL_CHECKPOINTS=0` disables them for bounded comparison workloads; such a run must remain below the retention cap.

Snapshot serialization happens under the UI terminal lock on a background worker, after any in-flight render capture finishes. Upload/storage is outside that lock. Diagnostics distinguish capture acquisition delay (stage 50), capture lock time (51), snapshot bytes (52), upload/publication latency (53), broker publication time (54) and retained-event high-water bytes (55). Capture is not assumed free merely because transfer is asynchronous.

Responses carry event position and response index. Replacement epochs revoke stale input; the surviving broker deduplicates replayed responses. User input is never replayed. Replay suppresses transient notifications, clipboard writes and bells. Explicit output injection travels through the journal. Native snapshot recovery retains partial parser state and ordered dimensions.

Closing the last window parks its existing owner without rendering. Another ordinary launch of the same experimental instance selects that owner. Explicit session close still terminates that session's shell. Owner crash recovery is allowed to pause terminal replies; opening the application restores the journal into a new owner without respawning the surviving shell.

## Early measurements (before checkpoints)

Pinned package: `artifacts/single-owner/journal-v3-app`, with SHA-256 manifest `journal-v3-manifest.json`. Identical compiled echo fixtures were used on both sides; baseline application binaries were preserved. All samples have correlated T0–T8. EndDraw is the presentation endpoint, not physical display latency.

One 100-event fixed-output pair (90 post-warmup events per side), `journal-v3-fixed-pilot2`:

| Metric | Legacy | UI owner |
|---|---:|---:|
| Input p50 / p95 / p99, ms | 0.733 / 7.093 / 26.016 | 0.392 / 5.168 / 13.023 |
| Present p50 / p95 / p99, ms | 112.423 / 348.555 / 585.145 | 96.797 / 143.631 / 159.499 |
| Sampled UI + broker CPU, s | 6.828 | 5.453 |
| Active output, decimal MB/s | 2.129 | 2.986 |
| Mean private memory, decimal MB | 88.98 | 107.61 |

One 2050-event idle pair (2000 post-warmup events per side), `journal-v3-idle-pilot`:

| Metric | Legacy | UI owner |
|---|---:|---:|
| Input p50 / p95 / p99, ms | 0.257 / 2.372 / 6.514 | 0.293 / 1.180 / 2.349 |
| Present p50 / p95 / p99, ms | 2.219 / 17.386 / 33.230 | 1.470 / 16.834 / 20.412 |
| Sampled UI + broker CPU, s | 3.969 | 4.031 |
| Mean private memory, decimal MB | 63.61 | 65.53 |

These are noisy single-pair pilots, **not acceptance results**. All statistical latency/resource gates remain inconclusive; memory is informational. The first failed fixed-output run (`journal-v3-fixed-pilot`) had an invalid fixture argument-array shape and produced no comparison samples. Its evidence was retained and excluded. The fix does not alter baseline binaries.

The earlier v16 host-owner series has five paired runs / 10,000 post-warmup events per side. Fixed-work CPU's conservative ratio CI was 0.264–0.334, but input delivery under load remained inconclusive. Those runs occurred earlier; do not treat their absolute timings as a simultaneous three-way comparison with these UI-owner pilots. Raw evidence and gates remain in `paired-v16-fixed-report.json` and `paired-v16-fixed-series`.

## Verification evidence

- Host-free journal/framing/input/legacy capability tests: 82 passed, including a separate upload waiting for input acceptance, queued resize while the writer is blocked, and revocation of pending publication after owner replacement.
- `journal-v9-pilot`: 22 Windows VM contract checks and 11 owner-lifetime UI checks passed before measurement. One UI owner/zero replicas, exact query response, Unicode, snapshot with unfinished CSI, replay effects, successive checkpoint publication, journaled injection and restored resize, final output/exit and native resource release.
- Owner-lifetime checks: no rendered frames while parked; query replies and parsing remain active; ordinary launch reuses the owner; forced owner termination retains the shell; replacement restores it; explicit close kills that original shell. Recovery including application startup was 841.1 ms in `journal-v9-pilot` (`journal-recovery.json`), versus 4199.7 ms in the earlier single `journal-v6-lifetime2` run. These are individual recovery observations, not a steady-state performance acceptance metric.

## Intermediate architecture comparison

`journal-v7-comparison` rotates four variants over five pairs of 200 events: legacy, host-owner, journal/checkpoints-off and journal/checkpoints-on. All 20 runs have valid T0–T8 samples. This sample count is below the predefined 10,000 post-warmup events per side: latency acceptance gates remain inconclusive even when a paired run confidence interval is narrow.

Means of per-run percentiles, fixed-output:

| Metric | Legacy | Host owner | UI owner + checkpoints |
|---|---:|---:|---:|
| Input p50, ms | 0.267 | 0.721 | 0.307 |
| Present p50, ms | 73.873 | 49.209 | 64.483 |
| Input p95, ms | 1.293 | — | 1.340 |
| Present p95, ms | 87.253 | — | 77.113 |

UI-owner versus host-owner input median improves by 0.413 ms (paired 95% CI: 0.347–0.480 ms), but presentation median worsens by 15.274 ms (15.215–15.333 ms). Its fixed-work CPU ratio is approximately 3.01 (2.55–3.47), and active-output throughput ratio is 0.637 (0.571–0.703). This is a real trade-off to investigate, not evidence that the journal design wins on every performance dimension. These CPU/throughput observations include tracing; trace-disabled bulk measurements are reported separately.

Checkpoint on/off input-median delta is +0.012 ms (CI −0.011 to +0.034); presentation-median delta is +0.054 ms (−0.025 to +0.134). Tail intervals remain wide. Snapshot capture itself holds the terminal lock for means of 0.14–0.18 ms and maxima of 0.72–0.90 ms in this workload. Snapshots are only 3.3–3.5 KiB here: this fixture does not establish capture costs for large scrollback/images.

The v7 broker prefix-trim lock averaged 10.7–11.9 ms, with 30–32 ms maxima. v9 replaces repeated sorted-prefix enumeration with contiguous dictionary removal; its pilot averages 1.20 ms, maximum 4.82 ms. Capture averages 0.17 ms, maximum 0.86 ms; upload/publication averages 1.70 ms, maximum 6.44 ms. Seven checkpoints keep accounted retention near 8.42 MB. v9 batches already-available output without adding a timer, but the fixture's small sparse chunks still produce about 197,000 writes, versus about 204,000 on v7; batching alone does not remove the transport cost.

The v7 trace-disabled 128 MiB pilot completed beyond the 64 MiB journal budget: legacy 2.71 MB/s and 32.97–33.03 CPU seconds, journal 6.16 MB/s and 29.84–29.86 CPU seconds. Mean combined private memory was 87.42 / 102.66 MB; simultaneous sampled peaks were 110.04 / 123.40 MB. This is one pair, not an acceptance result. Host-free small-capacity tests separately verify full-buffer backpressure, failed persistence retaining the prefix, cancellation and disposal wakeups.

Raw evidence: `journal-v7-vs-legacy.json`, `journal-v7-vs-host-owner.json`, `journal-v7-checkpoint-comparison.json`, `journal-v7-checkpoints.json`, `journal-v9-pilot-report.json`, `journal-v9-pilot-checkpoints.json`, under `artifacts/single-owner`. Do not mix package versions into one statistical series. Default rollout remains disabled.

## Final v9 idle input series

`journal-v9-idle-series`: five pairs, 2050 keys per run, 50 warmup keys excluded; **10,000 correlated post-warmup T0–T8 samples per side**. The pinned v9 package is identified by `journal-v9-manifest.json`; all ten runs completed successfully in the routed VM. Values below are means of per-run percentiles, not pooled percentiles.

| Metric | Legacy | UI owner + checkpoints | Gate |
|---|---:|---:|---|
| Input p50, ms | 0.197 | 0.292 | pass within 0.5 ms tolerance |
| Input p95, ms | 1.063 | 1.152 | inconclusive |
| Input p99, ms | 2.042 | 1.896 | inconclusive |
| Present p50, ms | 1.612 | 1.443 | pass |
| Present p95, ms | 11.222 | 2.337 | pass |
| Present p99, ms | 20.485 | 4.604 | pass |
| Mean private memory, decimal MB | 62.93 | 64.48 | informational |

Input-median paired delta is +0.095 ms (95% CI +0.053 to +0.137). Input p95 delta CI is −0.535 to +0.713 ms, p99 −1.934 to +1.641 ms: neither proves non-regression at the retained thresholds. Presentation p95 delta CI is −16.654 to −1.118 ms, p99 −23.047 to −8.714 ms. CPU is approximately 3.89 / 3.95 seconds per observed run, descriptive only: idle echo is not a fixed-output CPU acceptance workload. Detailed gates: `journal-v9-idle-report.json`.

The final v9 four-way pilot also repeats checkpoint-on/off and host-owner comparisons (`journal-v9-checkpoint-comparison.json`, `journal-v9-vs-host-owner.json`). It is a single pair and remains inconclusive. Broker input queue residence with checkpoints averages 0.08 ms, maximum 4.30 ms (without checkpoints 0.05 / 1.74 ms); this includes writer-lock acquisition. Corresponding writer session-lock acquisition maxima are 0.02 / 0.01 ms. These cumulative diagnostic observations do not establish a causal latency regression by themselves.

## Final v9 trace-disabled bulk series

`journal-v9-bulk-series`: five paired runs, 128 MiB each, same pinned binaries and fixture, all ten completed. CPU and memory sum the UI and broker, excluding the producer. Completion uses external readback with 100 ms polling; this measures sustained throughput, not input or display latency.

| Metric (mean across runs unless specified) | Legacy | UI owner + checkpoints |
|---|---:|---:|
| CPU observation interval, seconds | 32.256–32.309 | 29.484–29.606 |
| Elapsed seconds | 27.152 | 20.902 |
| Throughput, decimal MB/s | 5.509 | 6.428 |
| Mean private memory, decimal MB | 89.30 | 103.42 |
| Maximum simultaneous sampled private memory, decimal MB | 107.18 | 123.44 |

The conservative paired CPU upper ratio is 0.918 (95% CI 0.881–0.955): **CPU non-regression passes**, with about 8% lower observed CPU. The companion lower-bound ratio is used only to prove regressions; its `inconclusive` label does not negate the upper-bound pass.

Throughput ratio is 1.296 (CI 0.585–2.008): **inconclusive**. The first baseline took 49.22 seconds; the other four took 21.29–22.06 seconds, versus candidate runs of 19.96–21.97 seconds. No run is excluded, and the earlier single-pilot 2.3× gain must not be presented as a stable improvement. Memory ratio is 1.158 (CI 1.125–1.192), informational. Every candidate processed twice the event-retention budget without losing its final marker, exercising repeated checkpoint/trim cycles. Raw samples, binary hashes and gates: `journal-v9-bulk-report.json`.

## Decision and remaining product scope

The UI-owner architecture is viable for this Windows prototype: one parser, independent window lifetime, surviving-shell recovery, bounded history, improved ordinary echo presentation tails and modestly lower trace-disabled fixed-work CPU than legacy. A modest memory increase is consistent with the agreed priorities. Asynchronous checkpoint upload works without a per-key acknowledgement, although snapshot capture and prefix publication still have measurable lock costs.

**Keep both experimental modes opt-in; do not select a default architecture yet.** The journal path has not demonstrated an overall win over host-owner: the contemporaneous fixed-output comparison still favors host-owner for output presentation and CPU, while UI-owner delivers input sooner. The likely next optimization target is raw-output transport/scheduling and repeated small-record allocation, not a Rust rewrite or added crash-path complexity. That is a measured follow-up direction, not a claimed result of this prototype.

Before a full migration, resolve input-tail and throughput acceptance uncertainty, repeat a sufficient fixed-output comparison on the selected implementation, and measure checkpoints with large scrollback/images. WSL/remote ownership, broader product state transitions and durable host restart are outside this change. The original `single-owner-ghostty-sessions` migration remains incomplete; this change completes the comparative prototype only. No new runtime dependency, default backend change or automatic adoption of existing sessions is included.
