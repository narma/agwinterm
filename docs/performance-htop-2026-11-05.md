# htop partial repaint investigation

A user reported a jumping selection and a transient blank first process row in
WSL htop at a 0.1-second update interval, with both Ghostty and Rust. This is a
shared PTY ingestion issue: publishing every individual pipe read allows paint
to capture curses insert/delete-line operations before the rest of the update.

The reader now drains already-available pipe data under the same ingestion lock
before notifying the UI. A burst stops after 256 KiB or 4 ms of work (checked
between reads). It never sleeps or waits for future output under that lock.
The raw-output tap and exit byte counters still observe every byte in order.
This is not an atomic-frame guarantee for arbitrarily delayed producers; apps
that need that guarantee should use synchronized output.

The test entry point `tests/integration/wsl-tui-rendering.ps1` runs on the existing
private non-input WSL desktop, with a private app identity and htop config. It
uses real htop 3.4.1, CPU sorting, three Down presses, and an approximately
237-by-74 grid. `AGWINTERM_PAINT_TRACE` records actual captured cells, rather than
later control-API reads. Traces and raw VT output stay in ignored artifacts.

Thirty-second bundled-ConPTY runs (highlight counts exclude the first five
seconds of startup/navigation):

| Core / reader | htop frames | Blank first process row | Displaced selection frames |
| --- | ---: | ---: | ---: |
| Ghostty / per-read notification | 564 | 2 | 10 |
| Rust / per-read notification | 571 | 0 | 1 |
| Ghostty / drain available burst | 350 | 0 | 0 |
| Rust / drain available burst | 348 | 0 | 0 |

Evidence directories: `artifacts/htop-baseline-cpu-ghostty`,
`htop-baseline-cpu-rust`, `htop-drain-ghostty`, and `htop-drain-rust`.
The live process list changes between runs; these are reproductions and
comparisons, not statistically calibrated latency measurements. Trace logging
adds overhead. Inbox-ConPTY/private-desktop acceptance is not yet established:
both the baseline and changed build failed to produce useful htop paint traces
in that fixture. Do not count those attempts as passes.

All 1,925 existing Pty tests pass. A new real-pipe test additionally checks exact
byte delivery and final output across several drain budgets and EOF.

The separate report of persistent text corruption after shrinking and restoring
the window is still under investigation; this repaint fix does not claim to
resolve resize/reflow corruption.
