# Local UI tests: desktop or Windows Sandbox

**Direct desktop and Windows Sandbox are equally supported local UI test modes.**
Run `hud-ui.ps1` directly on Windows, or use the optional `sandbox-ui.ps1` wrapper
to run the same suites in Sandbox. Core/Pty unit tests and the runner's fixture
checks do not require Sandbox.

Both modes use the standard Release outputs. From the repository root, build first:

```powershell
dotnet build Agwinterm.slnx -c Release
```

## Direct Windows desktop

```powershell
pwsh -NoProfile -File tests/integration/hud-ui.ps1 -Suite Navigation -Strict
```

This mode does not require Sandbox. Keep mouse position and focus steady and avoid
interacting with test windows during the run. Private instance data and control
pipes do not isolate the tests from desktop input events.

If the maintainer's suite-token helper is installed, supply `-TokenOwner` or set
`AGWINTERM_TEST_OWNER`. Without that helper, the runner requires `CI=true`; set it
only in a child test process so the current shell environment is unchanged:

```powershell
pwsh -NoProfile -Command '$env:CI = "true"; & ./tests/integration/hud-ui.ps1 -Suite Navigation -Strict'
```

## Windows Sandbox

Use this optional mode to run the suites on an isolated guest desktop while
continuing to work on the host. The wrapper never sends mouse or keyboard input to
the host. The Navigation tooltip fixture positions its test window under the
cursor inside the guest. Application behavior and CI execution are unchanged.

### Requirements

- Windows x64 with Windows Sandbox enabled, and PowerShell 7 x64.
- Built Windows application and `agwintermctl`, including their output-directory
  dependencies. Self-contained builds work without an installed .NET runtime.
  For framework-dependent builds, the host needs the corresponding Windows .NET
  runtime; its `host` and `shared` directories are copied, without the SDK.
- Enough disk space for a staged copy of the binaries, PowerShell and runtimes.
  No dependencies are downloaded during a test run.

Enable **Windows Sandbox** in Windows Features and restart Windows if necessary.
Open Windows Sandbox from Start once to finish client installation. On Windows 11
24H2 and later, the client is updated through Microsoft Store; enabling the Windows
component alone may not make the launcher available yet.
Alternatively, an administrator can run:

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All
```

### Run

From the repository root, launch the suite:

```powershell
pwsh -NoProfile -File tests/integration/sandbox-ui.ps1 -Suite Navigation
```

To keep the Sandbox viewer off your desktop and avoid interrupting your work:

```powershell
pwsh -NoProfile -File tests/integration/sandbox-ui.ps1 -Suite Navigation -Background
```

`-Background` requires the modern Store-serviced client (`wsb.exe` and the
`WindowsSandboxRemoteSession.exe` execution alias). It connects the viewer on a
private host desktop without switching your input desktop. The viewer remains
unminimized there so the guest can render its UI; no viewer window appears on your
working desktop or takes focus from your programs. Running without a viewer does
not create a logged-in guest desktop, and simply minimizing the normal viewer can
let the client restore its window during connection.
Omit `-Background` to watch tests in the normal viewer. Both modes return the same
results and require stopping the guest with the command printed by the wrapper.
The background viewer is closed when the wrapper finishes, including on failure
or timeout; the guest VM remains available until you stop it. After a run you can
open a normal viewer with `wsb connect --id <id>` to inspect that guest.

The default paths match the solution's build outputs: the app under
`src/Agwinterm.Win32/bin/x64/Release/net10.0-windows/win-x64/` and the CLI under
`src/Agwinterm.Ctl/bin/Release/net10.0-windows/`. No manual staging into
`artifacts/local/` is needed. Pass both `-Exe` and `-CtlExe` for a packaged build
or other output directories; the runner does not search old artifact directories.
`-PowerShellHome` and `-DotnetHome` override the host dependency locations.
The wrapper supports every `hud-ui.ps1` suite, including the fork's `Ask`,
`HudAdvanced`, `Recent`, `WorkingSets`, `Windows` and `Presentation`. The full list
is `Hud`, `HudAdvanced`, `Recent`, `WorkingSets`, `Windows`, `Presentation`, `Quick`,
`Navigation`, `Picker`, `Ask`, `Accessibility`, `Command` and `Menu`.
The runner always passes `-Strict`.

The host generates a unique `artifacts/sandbox/<run>/ui-tests.wsb`, opens Sandbox
and waits up to 900 seconds. `-TimeoutSeconds` changes this deadline.
On Store-serviced clients, the wrapper uses `wsb.exe` to start the environment
and connect its viewer; older Windows installations use `WindowsSandbox.exe`.
The guest starts the suite automatically after copying the payload to its own disk.
Networking, clipboard, microphone, camera and printer redirection are disabled.
Only the input payload (read-only) and that run's result directory (writable) are mapped.
`CI=true` is set only inside the isolated guest so the existing UI runner does not
require the developer's desktop suite-token helper.

For a visible run, keep the Sandbox desktop unlocked and avoid interacting with its
windows during the run. Work in host windows outside the Sandbox viewer. Do not close Sandbox
before `result.json` appears. Use `-Background` rather than minimizing the visible
viewer. vGPU is enabled by default; use `-DisableVGpu`
to try software rendering when diagnosing graphics issues.

Results contain `stdout.log`, `stderr.log`, `evidence/` (transcripts and PNGs), and
an atomic `result.json`. The host returns the suite's exit code: 0 for success,
1 for test failure, 2 for a guest setup/cleanup failure. A missing result or timeout
fails the host command; it never counts as a pass. Sandbox remains open for
inspection, including after timeout; stop it before another run. With the modern
client, closing the viewer leaves the guest running: use the `wsb stop --id <id>`
command printed by the wrapper. With the legacy client, close the Sandbox window.
The runner refuses a second simultaneous run or an already running Sandbox,
including a modern CLI environment running without an open viewer.

To stage and inspect the configuration without opening Sandbox:

```powershell
pwsh -NoProfile -File tests/integration/sandbox-ui.ps1 -PrepareOnly
```

This prepares files only and does not execute UI tests. Sandbox isolation does not
fix application defects or all timing-related failures.

## CI

The existing Windows jobs run `hud-ui.ps1 -Strict` and its suites
directly on the CI runner desktop. They do not use or require Windows Sandbox.
The same test cases and assertions run locally inside Sandbox and directly in CI.

The core CI job also runs wrapper checks with fixture binaries and a fake launcher,
without starting any UI or requiring Windows Sandbox. These checks verify suite
parity with `hud-ui.ps1`, staging and guest dispatch for every suite, result
handling, both modern and legacy launchers, and evidence export. Run them locally with:

```powershell
pwsh -NoProfile -File tests/integration/sandbox-ui.tests.ps1
```

See Microsoft's [Sandbox configuration documentation](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file).
