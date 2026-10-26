# Selection performance

Measured locally on 2026-10-26: Windows x64, Ryzen 9 7950X (16 cores / 32 logical processors),
.NET 10.0.14, Release, workstation GC, tiered compilation disabled. Baseline is commit `8924812`
(the first selection fix in PR #12). Both implementations run in the same process and use the
same fixtures. Seven calibrated samples follow warmup; numbers below are median time per
operation. These are component microbenchmarks, not application FPS or end-to-end input latency.

## Results

| Workload | Grid | Before, µs | After, µs | Allocated bytes/op, before → after |
| --- | --- | ---: | ---: | ---: |
| Unchanged frame, selected word | 120×40 | 10.16 | 3.90 | 0 → 0 |
| Status update, selected word | 120×40 | 17.80 | 3.98 | 135,704 → 0 |
| Status update, selected word | 240×100 | 88.09 | 20.22 | 675,224 → 0 |
| Status update, repeated full-screen selection | 240×100 | 12,450.60 | 68.23 | 675,224 → 0 |
| Status update, repeated full-screen selection | 400×200 | 83,829.04 | 255.78 | 2,246,424 → 0 |
| Status update, nearly identical full-screen lines | 400×200 | 1,105.90 | 95.34 | 0 → 0 |
| Selected word absent from viewport | 400×200 | 1.04 | 0.53 | 0 → 0 |
| Capture word | 120×40 | 4.59 | 5.55 | 136,248 → 22,400 |
| Capture word | 400×200 | 107.05 | 93.49 | 2,246,968 → 327,200 |
| Capture and first copy of full viewport | 240×100 | 214.01 | 134.13 | 1,500,752 → 351,512 |

The small-grid word capture trades approximately 1 µs for six times fewer allocated bytes.
Repeated-frame work is the important improvement: the large stress case falls from 83.83 ms to
0.256 ms (about 328×), without allocating another screen snapshot on every update. This work
runs under the session lock, so reducing it also reduces time during which PTY output waits.
The benchmark does not directly measure that wait or claim a corresponding whole-app speedup.

## Changes

- Store only Unicode scalar and cell width in a reusable compact frame, rather than copying
  full coloured cells into new arrays after each match.
- Intern equal selected spans and context rows. Hash buckets are verified by exact comparison;
  hashes never decide text equality.
- Memoize exact span comparisons on demand within each changed frame. Eagerly comparing every
  pair improved identical rows but regressed nearly identical rows, so it was rejected.
- Cache both successful and unsuccessful location results. Missing single-line selections have
  a short rejection path that avoids reading the whole viewport again.
- Build the copied string lazily from immutable spans, with no per-character BMP string allocation.

No counters or additional operations were added to screen writes. With no active content selection,
the new text-read helpers are not called. Column/row resizes invalidate the relevant caches.

## Reproduction and validation

Run `./tools/selection-bench/run.ps1`. The script extracts the baseline from Git into ignored
`obj/`, checks 18,000 deterministic mutations against it, and writes raw JSON to ignored
`artifacts/selection-bench/results.json`. Use `-Output` for another path or `-Scenario` for one
case. See [the benchmark instructions](../tools/selection-bench/README.md) for measurement details.

Validation on the final implementation:

- 18,000 differential checks passed (linear/block selection, scrolling, edits and clears).
- All 509 Core tests passed, including managed/Rust selection regressions.
- New regression tests check zero allocations after capture on changing repeated text, lazy Unicode
  copying, negative-cache recovery, resize invalidation and equal spans at different columns.

Timing thresholds are deliberately kept out of unit tests; functional and allocation regressions
are deterministic, while performance measurements depend on the machine and concurrent load.

After the review fixes (reject blank context, retain history eviction tracking with mouse reporting,
and preserve hidden rows during extension), the 18,000 differential checks still passed. The separate
regression cases intentionally correct baseline behavior. A repeat of `status-word` measured
16.27 → 4.12 µs at 120×40, 81.16 → 18.95 µs at 240×100, and 279.83 → 63.46 µs at 400×200,
with zero bytes allocated per update in all three optimized cases.
