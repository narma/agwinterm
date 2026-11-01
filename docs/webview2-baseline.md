# WebView2 pre-change baseline

Measured 2026-10-31 before adding any WebView2 dependency. Source commit:
`cf8dee02f1c67c2ef5c51cbe0909ba84b2f42ed6`, with existing unrelated local
installer guidance/update-script and planning changes. No feature code changed.

## Reproduction

Build `installer/build.ps1` followed by `installer/build-portable.ps1`, then run
`pwsh -NoProfile -File tools/web-startup-baseline.ps1` on the same desktop.
The script accepts explicit executable/package paths and output path for the
after measurement. It creates a unique application identity and retains that
profile for inspection. It closes only the instance it starts.

The 20 sequential starts use the installer-published Release 0.20.14 application,
managed emulator, in-process host, no restore, and a fixed `cmd /d /q /k echo`
profile. Readiness means the marker is observable through `session.text`; it
includes launch and pipe readback, not first paint. Poll interval is 10 ms;
connection timeout is 100 ms. This is a warm filesystem-cache baseline, not a
reboot/cold-start benchmark. The same protocol must be used for the comparison.

## Machine and results

- Windows 11 Pro, build 26300 (10.0.26300); .NET SDK 10.0.303.
- AMD Ryzen 9 7950X, Balanced power plan.
- AMD Radeon Graphics driver 32.0.21030.2001;
  NVIDIA GeForce RTX 5080 driver 32.0.16.1742.
- Terminal-ready median: **479.00545 ms**; nearest-rank p95: **516.9571 ms**.
- Browser descendants at each readiness sample: **0**, all 20 starts.
  This sampling does not constitute continuous process-start tracing.
- Installer: **32,322,815 bytes**.
- Portable executable: **76,014,575 bytes**.
- Published application payload: **95,693,400 bytes**. This is the staged
  installer payload, not a measurement after installation; generated uninstall
  files and shared runtime installation are excluded.
- Executable SHA-256:
  `F5B10BF7D187F05F3354207E7B3D89D0012B917FDA4ADA8665D4BA45173D5A4C`.

Local disposable evidence: `artifacts/webview2-before.json`,
`artifacts/webview2-baseline-installer.log`, and
`artifacts/webview2-baseline-portable.log`. Release solution build passed, with
two existing xUnit analyzer warnings in SessionNewWorkspaceTests and
RestoreStateTests. Both packaging scripts passed.

## Outstanding acceptance evidence

Initially, installed byte counts had not been measured on a
clean installation. The existing working installation is 0.20.13; running the
new installer uses the same Inno AppId and could change its installation or
uninstall registration even with a different destination directory. No installer
was run during the initial measurement and the working installation was not updated.

Completing that measurement needs an isolated Windows test environment, or an
explicit decision that staged application payload bytes satisfy this part of
the task. The latter changes the acceptance interpretation and is not assumed.
Later clean-machine, missing-runtime/offline, HWND, IME, UIA and mixed-DPI gates
remain untested. No implementation task is claimed complete by these measurements.

### Installation attempt after GUI closure

The user closed the GUI and authorized continuation. On 2026-10-31 at 19:37
Europe/Moscow, the installer was attempted after backing up the installed files
to `artifacts/webview2-installed-before-update`. It exited 5 because the existing
`agwinterm-ptyhost.exe` was still running (PID 41632), locking its executable.
Automatic application closure was disabled to preserve hosted sessions.
The installer rolled back; a full SHA-256 comparison of every backup file
confirmed that the installed files matched afterward. The host was not stopped.
Evidence: `artifacts/webview2-baseline-install.log`.
The installed-size measurement still requires completing the update after
the user permits termination of the background session host or closes it.

### Completed installed baseline

With explicit user authorization, PID 41632 was stopped after checking its
executable path. The retry succeeded (installer exit 0), updating the application
to 0.20.14.0. The installed directory contains **100,809,817 bytes in 804 files**,
including uninstall files and any files retained by this upgrade. This measures
the actual upgraded installation, not a clean-machine footprint. Task 1.1 is
complete; clean-machine validation remains required by tasks 1.5 and 6.3.
Evidence: `artifacts/webview2-installed-baseline.json` and
`artifacts/webview2-baseline-install-retry.log`.
