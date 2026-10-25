# C1 verification

Date: 2026-10-25. Scope: `control-events-and-wait`, following C0 commit `5970e69`.

## Executed checks

- `dotnet build Agwinterm.slnx -c Release --verbosity quiet`: success; two existing xUnit analyzer warnings in RestoreStateTests and SessionNewWorkspaceTests.
- `dotnet test Agwinterm.slnx -c Release --no-build --verbosity quiet`: 433 Core and 1314 Pty tests passed, none skipped.
- `pwsh -NoProfile -File tests/integration/control-events-and-wait.ps1`: passed on a private Win32 app identity, pipe and data directory.
- `pwsh -NoProfile -File tests/integration/wsl-control.ps1 -NativeHooks`: passed using Ubuntu, disposable Linux HOME and the installed native CLI wrapper. Includes existing C0 target/profile/cwd checks and actual app restart.
- `tools/build-wsl-hook.ps1`: Linux x64 and ARM64 musl builds succeeded. Linux `cargo test --locked -p agwinterm-hook` through WSL: 10 passed.
- C0 private CLI reliability acceptance: 27 passed on the C1 implementation.
- `tests/integration/win32-control.ps1 -Strict -Exe <Release app>`: all passed on a standalone repeat. The preceding concurrent Windows/WSL run failed only its injected Ctrl+Shift+R reopen check; no production change was needed, and the same build passed that scenario and the full suite when run alone. Keep desktop input acceptance serialized.
- `openspec validate control-events-and-wait --strict`: passed.

Local logs are ignored artifacts, not prerequisites for reproducing the tests: `artifacts/c1-build.log`, `c1-regression.log`, `c1-acceptance.log`, `c1-wsl-final.log`, `c1-c0-regression.log`, `c1-win32-regression-repeat.log`.

## Normative scenario audit

| Contract | Evidence |
| --- | --- |
| Run/window identity and restart refusal | ControlObservationTests empty/recreated journals; real WSL restart rejects previous run |
| Int64, safe pagination, filters and retention loss | ControlObservationTests parser, cursor, limited pages, excluded scans and 1000-record wrap cases |
| Tail baseline and explicit resume | ObservationCliTests pins identity; private Win32 acceptance exercises flushed baseline, next event and exact run/window/cursor resume |
| NDJSON/plain framing, gaps, unsupported or malformed servers | Real CLI process tests in ObservationCliTests; WSL watch transport timeout and terminal Ctrl+C exit 130 |
| Filtered bounded long polls | ControlObservationWaitTests excluded-event flood/deadline; ObservationTransportTests sequential connection reuse and matching wake |
| Stable owners and committed lifecycle payloads | Private Win32 acceptance: background focus, resize, axis, swap, no-op/refusal, scratch, overlay exit/replacement, HUD privacy, keyboard split/scratch equivalents |
| Payload bounds and shared capacity | Unicode truncation test preserves IDs under 8 KiB; 128 combined observers reject overflow and release on cancellation/window close |
| Wait inputs, caller default and scope | CLI validation regressions; real caller-env test; private Win32 auxiliary refusal/pane acceptance, duplicate-name refusal, explicit window boundary |
| Aggregate priority and retained exited panes | Coordinator structural tests; private Win32 split removal, blocked priority, hidden scratch and retained exited-pane snapshot |
| Atomic snapshot/register, brief transitions, history independence | StatusSignalTests ordered concurrent writes/subscription; ControlObservationWaitTests completed/reset and journal wrap, pre-registration history excluded |
| Deadline ordering and distinct outcomes | Deterministic monotonic 99/100/101 ms tests; actual CLI match 0, confirmed timeout 3, usage 2, transport/refusal 1 and cancellation 130 |
| Removal, disconnect, pipeline misuse and slow readers | Real named-pipe tests release within two seconds and preserve ordinary sequential requests; multi-megabyte slow reader leaves another client's ping responsive; Win32 target/window removal acceptance |
| WSL delivery and cancellation | Native hook transition matches wait; timed wait exits 3; actual terminal Ctrl+C yields one client cancellation envelope and exit 130; watch interruption exits 130 |

## Findings resolved during verification

- WSL interop SIGINT does not reliably invoke the Windows ConsoleCancelKeyPress handler. The existing native helper now owns Linux SIGINT for watch/wait, reaps its Windows child, forwards bounded output and preserves completed outcomes. Ordinary CLI commands still exec directly; no Python runtime dependency was added.
- The queued UI boundary wrapped typed observation refusals as generic errors. It now preserves ObservationException, verified by real auxiliary and ambiguous-target refusals.
- Ping can precede window restoration; restart acceptance waits for the observation endpoint before comparing run identities.
- The slow-reader test fixture disposed its cancellation source twice; fixture cleanup is idempotent.

## Limits

ARM64 is cross-built, not runtime-tested here. Native cancellation was exercised on Ubuntu x64. This stage does not add automatic reconnect, persistent journals, C2 overlay hiding/showing, process-exit inference, or Lite parity. Integration runs use private app state and restore their fixtures; no release or live user installation was performed. Unrelated ConPTY working-tree edits are excluded from the C1 commit.
