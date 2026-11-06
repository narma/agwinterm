# VT adapter baseline

Run from the repository root (no UI, VM or PTY required):

```powershell
dotnet build tools/vt-bench/vt-bench.csproj -c Release
dotnet tools/vt-bench/bin/Release/net10.0/vt-bench.dll ghostty artifacts/vt-ghostty.json D:/Projects/Agwinterm/artifacts/ghostty-build/bin
./tools/vt-bench/summarize.ps1 -Reports artifacts/vt-ghostty.json -OutputCsv artifacts/vt-summary.csv
```

For Ghostty, first run `./native/ghostty-vt/build.ps1`. Its benchmark also refuses
missing or incompatible libraries. Capture/text extraction happens after the
timed section: these numbers measure ingestion, not total frame cost.

Each run covers 1/10/20 cores, 120×40/240×100/400×200, ASCII scrolling, Unicode scrolling and sparse cursor updates, with 64-byte and 4096-byte chunks. UTF-8 and VT sequences deliberately cross chunk boundaries. Each configuration has five fresh-core samples, one untimed warm-up and four timed payload repetitions. Compare medians of the five samples; keep raw samples. Run revisions in separate processes and alternate their order across repeated runs on the same quiet host. Do not run competing benchmarks concurrently.

This measures `Feed` **including adapter work**, including terminal mutation and scrollback storage. It does not establish Unicode correctness. Managed allocated bytes exclude native allocation; process CPU and absolute private bytes/working set include runtime overhead. Memory samples are not cold-start, peak or retained-memory measurements. Session counts are sequentially fed cores, not concurrent PTY workloads. Short samples can fall below process CPU timer resolution; zero CPU milliseconds do not mean no CPU usage. A single warm-up does not establish tiered-JIT convergence. These are exploratory measurements: acceptance needs longer calibrated runs, stable runtime settings and repeated process launches in alternating backend order. Do not use these results alone for rollout acceptance.

The remaining baseline requires worker-routed UI measurements: visible/hidden Windows and WSL panes, GPU dedicated/shared memory with driver identification, idle CPU, input-to-paint latency, capture/render cost, and retained memory after image/session/resize churn. Use the existing Hyper-V UI worker and WSL private desktop routing; never start benchmark windows on the user's desktop. `TerminalPerformance` already measures full-window rendering, but does not supply all these metrics.
