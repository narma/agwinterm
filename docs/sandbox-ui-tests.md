# Local UI tests in background Windows Sandbox

Local Windows UI entry points automatically route to **background Windows Sandbox** before
creating windows, touching the clipboard or launching processes. This includes
`hud-ui.ps1`, `win32-control.ps1`, conformance, restore, CLI/event, split, overlay,
web and profiling entry points. Failure to prepare Sandbox fails the command;
there is no fallback to the user's desktop. Host-free unit and fixture tests run
directly. GitHub Actions still runs real UI tests directly on its own runner.

Tests use the standard Release outputs. From the repository root, build first:

```powershell
dotnet build Agwinterm.slnx -c Release
```

## Local entry points

```powershell
pwsh -NoProfile -File tests/integration/hud-ui.ps1 -Suite Navigation -Strict
pwsh -NoProfile -File tests/integration/win32-control.ps1 -Strict
pwsh -NoProfile -File tests/conformance/conformance.ps1 -Strict
```

These commands now run inside background Sandbox automatically. Parameters such
as `-Dpi 144`, `-Only replay`, `-CookbookOnly` and explicit `-Exe`/`-Ctl` paths are
forwarded as data; host paths for binaries are replaced by their staged guest paths.
Tests supporting strict mode always run strictly. The clipboard recovery operation
`win32-control.ps1 -RestoreClipboard <snapshot>` remains a maintenance command;
it restores the named snapshot without launching UI tests.

You can also select standalone runners explicitly:

```powershell
pwsh -NoProfile -File tests/integration/sandbox-ui.ps1 -Test Win32Control -Background
pwsh -NoProfile -File tests/integration/sandbox-ui.ps1 -Test Conformance -Background
./tests/integration/sandbox-ui.ps1 -Test ProgramOverlay -TestParameters @{Dpi=144} -Background
```

| Test | Entry point / parameters |
|---|---|
| `Hud` (default) | All 13 `hud-ui.ps1` suites, selected with `-Suite` |
| `Win32Control` | `win32-control.ps1` |
| `Conformance` | `tests/conformance/conformance.ps1`; optional custom `Spec` is staged |
| `RestoreRoundtrip` | `restore-roundtrip.ps1`; `Only` |
| `ControlCli` | `control-cli-reliability.ps1` |
| `ControlEvents` | `control-events-and-wait.ps1` |
| `SplitRetention` | `split-retention.ps1` |
| `ProgramOverlay` | `program-overlay-controls.ps1`; `Dpi` |
| `WebOverlayHost` | `web-overlay-host.ps1`; `Cookbook`, `CookbookOnly`, `NavigationOnly` |
| `WebOverlayParity` | `web-overlay-parity.ps1`; `ReviewOnly` |
| `WebViewGate` | `webview-gate.ps1` / `tests/WebViewHost.Gate`; optional wrapper `-GateExe` |
| `ProfileMemory` | `tools/profile-memory.ps1`; `Cycles` |
| `WebPageMeasurements` | `tools/web-page-measurements.ps1`; `Output` |
| `WebStartupBaseline` | `tools/web-startup-baseline.ps1`; `Runs`, `Installer`, `Portable`, `Output` |

Build the C# gate first with
`dotnet build tests/WebViewHost.Gate/WebViewHost.Gate.csproj -c Release`.
Launching that gate directly (including `dotnet run`) also routes to background
Sandbox before its first window is created.

The baseline harness also requires local installer and portable files. Its Git
commit and SDK version are recorded on the host and passed as data; neither Git
nor the SDK is required inside Sandbox. Explicit
`Output` paths receive the exported guest report on the host after success.
The registry is `tests/integration/sandbox-ui-tests.psd1`; shared UI code, fixtures,
conformance contracts, logs, screenshots and guest state are staged/exported too.

**WSL tests:** `wsl-control.ps1` and `wsl-hook-bench.ps1` automatically run on a
private non-input Win32 desktop using the host's configured WSL distribution.
This is window isolation, not a disposable VM: the tests still use their private
app IDs and disposable Linux HOME directories. Build the native helpers as
described in [WSL validation](wsl.md) first. Existing test parameters are preserved:

```powershell
pwsh -NoProfile -File tests/integration/wsl-control.ps1 -Distro Ubuntu -NativeHooks -NoPython
pwsh -NoProfile -File tests/integration/wsl-hook-bench.ps1
./tests/integration/wsl-ui.ps1 -Test WslControl -TestParameters @{Distro='Ubuntu'; NativeHooks=$true}
```

The worker verifies its actual desktop before UI work. An environment marker
alone cannot bypass that check. The runner never switches the input desktop,
creates no console window, preserves failures and logs under `artifacts/wsl-ui`,
and terminates its Windows process job on completion, failure or timeout.
`sandbox-ui.ps1 -Test WslControl` / `-Test WslBenchmark` delegates to this runner too.

Setting `CI=true` alone no longer bypasses local routing. Direct execution is
reserved for the marked guest, verified WSL private desktop, or GitHub Actions
(`GITHUB_ACTIONS=true`, `CI=true`).

## Windows Sandbox

Use the wrapper to run tests on an isolated guest desktop while
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
- Web tests also need a local WebView2 runtime (auto-detected, or `-WebView2Home`).
  Cookbook, navigation-only web and web parity tests need Python 3 (auto-detected using `py -3`, or
  `-PythonHome`). Only the interpreter, DLLs and standard library are copied;
  third-party site-packages are excluded. All web servers run inside the guest.

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

Results contain `stdout.log`, `stderr.log`, `evidence/` (transcripts and PNGs),
`artifacts/`, `state/` (guest-only private application data), and
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
pwsh -NoProfile -File tests/integration/sandbox-ui-routing.tests.ps1
```

See Microsoft's [Sandbox configuration documentation](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file).
