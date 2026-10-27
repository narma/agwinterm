# C5 verification — 2026-10-25

Implemented `recent-closed-work` after archived C0–C4.

- Release solution build passed. Full regression: 460 Core and 1391 Pty tests passed; two existing xUnit analyzer warnings remain.
- 25 focused recent-history/control tests passed: durable round-trip, count/member/byte eviction, corruption retention, invalid/duplicate IDs and raw fields, write failure, clear isolation and actual CLI usage refusals.
- Owned native fixture: 22 checks passed, including swapped split, context/name/flag/pins, missing-profile partial recovery and retry, empty workspaces, closed/deleted source windows, exact destination, real process restart and stale-history/live-ID collision folding.
- Existing full Win32 control-host integration passed. Actual WSL recovery/restart passed, including distinct per-pane directories, distribution/user context and retained restore pins.

Evidence: ignored `artifacts/c5-build.log`, `c5-regression.log`, `c5-recent-tests.log`, `c5-recent-ui.log`, `c5-win32-regression.log`, `c5-wsl.log`. Native evidence under `.revmux/recent-ui-20261025T230505-d97226`.

Limits: history recovery starts shells using existing profile launch behavior; it does not certify later child-process success or replay pins/foreground commands. Partial workspace failures are accepted structured outcomes with member errors; failed single-session opens refuse and retain their entry. No release/live installation. Unrelated ConPTY edits are excluded.

Historical local acceptance record: the exact tested application commit was not recorded.
Raw logs and screenshots remain local and are intentionally not versioned; the paths above
identify original runs, not a permanent archive available after cloning. These checks were
not rerun as part of repository cleanup.
