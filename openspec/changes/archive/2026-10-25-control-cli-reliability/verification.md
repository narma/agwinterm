# C0 verification

Verified on Windows x64, 2026-10-25. Implementation and local acceptance are complete.
No release, live installation, OpenSpec archive/sync or C1–C8 implementation was performed.

## Commands and results

| Command | Result |
| --- | --- |
| `dotnet build Agwinterm.slnx -c Release -m:1 -p:BuildInParallel=false --verbosity minimal` | Passed; zero errors. Two pre-existing xUnit analyzer warnings in SessionNewWorkspaceTests and RestoreStateTests. |
| `dotnet test Agwinterm.slnx -c Release -m:1 -p:BuildInParallel=false --no-build --verbosity minimal` | Passed: 433 Core and 1267 Pty tests; zero skipped. |
| `dotnet test tests/Agwinterm.Pty.Tests/Agwinterm.Pty.Tests.csproj -c Release -m:1 -p:BuildProjectReferences=false --filter FullyQualifiedName~QueuedTargetRemovedBeforeExecutionNeverFallsBack --verbosity minimal` | Three subsequently added queue/liveness tests passed; no new analyzer warnings. |
| `pwsh -NoProfile -File tests/integration/control-cli-reliability.ps1` | All 27 C0 acceptance checks passed against the fresh x64 Release Win32 build. |
| `pwsh -NoProfile -File tests/integration/win32-control.ps1 -Strict -Exe src/Agwinterm.Win32/bin/x64/Release/net10.0-windows/win-x64/Agwinterm.Win32.exe` | Full existing Win32 control-host regression passed. AGWINTERMCTL explicitly selected the newly built Release client. |
| `pwsh -NoProfile -File tests/integration/wsl-control.ps1 -NativeHooks` | Passed with Ubuntu and its default Linux user, alzo, using native Linux helpers and an isolated HOME. All four targeted command modes verified. |
| `openspec validate control-cli-reliability --strict` | Passed. |
| `git diff --check` | Passed. |

Local integration transcripts are in ignored `artifacts/c0-acceptance.log` and
`artifacts/c0-win32-regression.log`. Testhost and desktop/WSL checks needed normal local
process access; the restricted runner could not establish the testhost loopback connection.
Builds and live integration launches must run sequentially because Windows locks loaded DLLs.

## Acceptance coverage

| Spec behavior | Evidence |
| --- | --- |
| Truthful JSON/text exit status; malformed envelopes, transport, UTF-8, version/help and picker exceptions | CtlRequestTests, CtlUsageTests, picker tests; actual CLI subprocesses with disposable pipe endpoints. |
| Strict option vocabulary, duplicate selectors, missing/invalid values, no connection on usage failure | CtlArgumentsTests plus existing HUD/text/split/frame/picker tests. Legacy Win32 stdin/socket and saturation checks passed. |
| Literal text, boolean flags, stdin conflicts and launch conventions | CtlArgumentsTests, existing stdin/help tests, SessionCommandTests and real direct-launch integration fixtures. |
| Omitted target ignores caller environment for the three C0 verbs | CtlRequestTests captures actual requests with a different AGWINTERM_SESSION_ID; C0 integration checks active single-pane refusal while caller names a background split. |
| Window bounds; unknown/ambiguous/empty/incompatible targets | ControlApiTests, actual Win32 resolver tests in SessionInputTests, two-window C0 integration, scratch/quick refusals. |
| Target removed while queued | Three SessionInputTests use a private message-only HWND to hold the production UI action, remove the target, then execute it; each refuses with active context untouched. |
| Background focus/ratio, stable swapped IDs, axes/units, unseen and MRU preservation | C0 integration observes both sessions, notification counts and MRU walk; existing Win32 suite checks axes and live pane geometry. |
| Named/raw command context, send isolation and readonly/input-closed/write failure | C0 integration plus actual Win32 handler tests with observable input sessions; broadcast path remains covered by existing SessionInputTests. |
| New/overlay/detached destination and quick restrictions | C0 integration reads files produced by the actual child processes, checks source/created session state and target overlay slot, and tests quick send/detached plus new/overlay refusal. |
| WSL distro/user/cwd/AGW context | Existing WSL fixture extended with a selected Windows session and explicit Linux target for send/new/overlay/detached. Reads the resulting process identity and cwd from Linux files. |
| Timeout/input uncertainty | CtlRequestTests and write-failure tests preserve uncertainty diagnostics; no completion or rollback guarantee is introduced. |

## Compatibility review

- Strict validation delegates text line counts to the existing saturating parser. Large valid counts
  still read the full available buffer, and malformed counts retain the established refusal.
- Earlier validation can now identify an unknown/duplicate flag before the stdin conflict guard.
  Regression assertions recognize the precise reason and still verify exit 2 and unchanged input.
- Detached Windows command quoting now preserves quoted paths through cmd's parser; real commands
  with spaced cwd/path arguments are covered by the C0 fixture.
- Legacy integration assertions were made tolerant of terminal line wrapping and localized ping
  output. The mouse fixture zooms until its original metric-distinction requirement is satisfied;
  its acceptance threshold was not weakened.
- Serving Win32 support is required for explicit targeting. The shared Lite conformance file is
  unchanged. Windows PowerShell/new and cmd/overlay/detached conventions, pane IDs, overlay result
  policy, quick restrictions and existing omitted-target defaults remain documented.
- Preserved unrelated pending ConPTY project/test changes and all existing OpenSpec/skill files.
  No native ABI, persistence format, release version or installation destination changed.

Coverage is local Windows/Ubuntu acceptance, not a claim about every installed WSL distribution,
every display scale or older serving binaries. Those limits do not replace any C0 acceptance case.
