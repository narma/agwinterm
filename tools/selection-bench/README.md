# Selection microbenchmark

Run `./tools/selection-bench/run.ps1` from PowerShell. It extracts the pre-optimization selection
implementation from commit `8924812` into ignored `obj/`, then compares it with the current core
in the same Release process. No native library, window, clipboard or external benchmark package
is required. JSON results default to ignored `artifacts/selection-bench/results.json`.

The runner first checks 18,000 deterministic scrolling/editing steps against the original
implementation, including rectangle and linear selections. Measurement uses tiered compilation
disabled, warmup and calibration followed by seven samples, reporting median microseconds and
thread-allocated bytes per operation. Maximum **sample average** is also recorded; it is not a
per-operation p99. GC is forced between samples, not inside measured loops.

Sizes: 120×40, 240×100 and a 400×200 stress case. Rows have unique prefixes except in the explicit
repeated-text scenarios. A near-identical-row case differs only in a suffix, guarding against
eager all-pairs comparisons. Cases cover capture, capture+first copy, cached copy, unchanged frames,
status updates, scrolling, missing words and repeated full-screen selections. `scroll-word`
includes rotating the fixture's rows, identically for both implementations; status cases include
one cell write. Counts are calibrated separately. Compare per-operation costs, not raw totals.

These measure the selection component, not Direct2D, PTY throughput, application FPS or end-to-end
input latency. Selection reconciliation runs under the session lock, so expensive cases matter to
both rendering and input/output. The optimized code adds no counters or work to ScreenBuffer's
write path; without an active content selection its new read helpers are not called.

Use `-Scenario similar-status-all` (or another case name from `Program.cs`) for a focused run.
