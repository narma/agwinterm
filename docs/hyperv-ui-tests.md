# Parallel Windows UI tests with Hyper-V

`tests/integration/hyperv-ui.ps1` runs each test invocation on its own Windows
desktop in a new generation-2 Hyper-V VM. Different worktrees and sessions can run
at the same time. Each invocation gets a GUID VM name, a differencing OS disk and
a separate payload/results disk. The base Windows image is shared read-only.
Hyper-V can report the base as attached while another guest is using its
differencing disk. The runner accepts that state for a read-only base, provided
Windows has no host device for it and no active VM uses it as a direct disk.
No network adapter, host folder mapping, clipboard sharing or viewer is used.
No additional service or interpreter is installed on the host or guest.

The runner uses the existing Sandbox test registry, payload staging, strict test
dispatch and evidence export. Hyper-V is the default Windows UI backend; explicit
Sandbox selection remains available with `AGWINTERM_UI_BACKEND=Sandbox`.
WSL tests still use `wsl-ui.ps1` and the host's configured distribution.

## Ordinary sessions: no per-run UAC

Install the prepared image once from an administrator PowerShell 7. Obtain the
SID from the **ordinary account that will run tests**, before elevating:

```powershell
[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
# In the administrator terminal, pass that SID, not the administrator's SID:
./tools/hyperv-ui-install-host.ps1 -BaseVhd 'D:\HyperV\Bases\agwinterm-optimized-01.vhdx' -UserSid '<ordinary-user-SID>'
```

After installation, use an ordinary PowerShell 7 session in any checkout with
these updated runners. No environment variables, image argument or UAC prompt
are needed:

```powershell
./tests/integration/hyperv-ui.ps1 -Suite Quick,Navigation
./tests/integration/hud-ui.ps1 -Suite Navigation
```

The installer copies the image and reviewed host code into the protected
`%ProgramData%\AgwintermUiHost` directory and registers two on-demand SYSTEM tasks
under `\AgwintermUi\`. The authorized account can run/read these tasks but cannot
modify them, their code, configuration or image. Workers exit after 60 idle
seconds and restart on demand, including after a reboot. This uses the existing
Windows Task Scheduler and PowerShell; it installs no service or interpreter.

Each worker accepts only a bounded archive over a local, SYSTEM-owned named pipe.
Only the configured user SID can connect; network connections are denied. Uploaded
scripts execute **only inside the VM**. Requests cannot select host commands,
host file paths, arbitrary base disks or resource policies. Archive traversal,
links and oversized uploads are rejected; exported guest evidence cannot contain
links into the host. Results are returned to the calling session and saved in its
own worktree. Limits remain two VMs, 8 GiB guest RAM and two vCPUs per VM, with a
maximum 1800-second worker run. The normal default run timeout is 900 seconds.

For an approved host-code update, wait until no jobs are active and rerun the
installer with `-Update` and the same SID/image. This maintenance step requires
elevation; ordinary test requests never update privileged code from a checkout.
A job marked for recovery with its matching VM confirmed off can remain in
place during an update. The installer preserves its disks and VM registration.
Custom VM resources, a different base image and `-KeepVM` require an explicit
administrator run. If the installed host is absent or unavailable, routing fails
closed and does not launch UI on the host desktop.

An automation tool may separately restrict access to Task Scheduler or named
pipes. In that case allow the command to run outside the tool's containment,
still under the ordinary Windows account. This is not Windows elevation and
requires neither `RunAs` nor another UAC prompt. The UI worker remains mandatory.

The host was installed and verified locally on 2026-11-03 for `ALZ\user`.
Both the direct wrapper and the normal `hud-ui.ps1` entry point passed Quick
without Windows administrator rights, image arguments or backend environment
variables. Two simultaneous ordinary-account requests then passed 59 checks
each in 85 and 93 seconds, with 47 seconds of actual VM overlap and 8 GiB total
guest RAM. The account's inability to write installed host code was also checked.
Evidence is in `artifacts/hyperv-validation/no-uac-parallel-proof.json` and the
corresponding `no-uac-*` result directories. The installed image is the protected
copy at `%ProgramData%\AgwintermUiHost\windows.vhdx`; the preceding direct-run
measurements below used the source image under `D:\HyperV\Bases`.

## Requirements

- Windows x64 with Hyper-V and its PowerShell management tools enabled.
- PowerShell 7. Elevation is needed once for host/image setup, or for direct
  administrator runs with custom VM settings; ordinary sessions use the installed
  workers because creating/mounting disks requires administrator rights.
- A prepared Windows 11 x64 desktop image (instructions below), kept on local NTFS
  storage. Avoid network shares, OneDrive and encrypted/compressed image folders.
- Built Release application and CLI, as for the Sandbox runner. The runner copies
  PowerShell and required .NET runtimes from the host; neither SDK nor Git is
  required in the image. Existing web-test runtime requirements still apply.
- By default, each running VM uses 4 GiB fixed RAM, two virtual processors,
  a reduced CPU scheduling weight and a 1920×1080 console display. Each virtual
  processor is capped at 50% on hosts with fewer than 16 logical processors;
  larger hosts use 100%. Override with `-CpuMaximum 50` when desired.
  Allow disk space for
  each OS delta, payload, exported evidence and the installed Windows image.

The script does not install Hyper-V, download Windows, accept license terms or
create an image silently. Admission is coordinated across worktrees and Windows
sessions: by default at most **two test VMs / 8 GiB of guest RAM** run at once.
Additional invocations wait before booting their VM. Hypervisor/host process
overhead is additional to guest RAM. The memory check also leaves 25% of host RAM
free, with a floor of 4 GiB and ceiling of 8 GiB, when admitting a new VM.

The CPU cap is per virtual processor, not 50% of the whole host. Reduced scheduling
weight helps other VMs under CPU contention; the cap limits test CPU consumption
even without contention. No currently running VM is killed because host load rises.

## 1. Obtain Windows

Download an **x64 ISO**, not the Installation Assistant:

- [Windows 11 ISO from Microsoft](https://www.microsoft.com/en-us/software-download/windows11).
- [Windows 11 Enterprise Evaluation](https://www.microsoft.com/en-us/evalcenter/evaluate-windows-11-enterprise)
  provides a time-limited 90-day evaluation. Record its expiration; it is not a
  permanent test image.

Use an edition/license appropriate for your VM usage. The scripts do not extend
evaluation periods or bypass activation, TPM or Secure Boot requirements.

## 2. Create the installation VM

From an elevated PowerShell 7 terminal at the repository root:

```powershell
./tools/hyperv-ui-new-image.ps1 `
    -IsoPath 'D:\ISO\Windows11-x64.iso' `
    -ImageDirectory 'D:\HyperV\AgwintermImage' `
    -Name 'Agwinterm-UI-Image' `
    -SwitchName 'Default Switch'
```

The directory must not exist. This creates a VM with Secure Boot, a local vTPM,
an 80-GiB dynamic OS disk, installation DVD and a small setup disk. It does not
start the VM or open a window. `-SwitchName` is optional; supply an existing switch
if installation, updates or activation need network access. Test VMs always have
their network adapters removed.

Start this installation VM in Hyper-V Manager and open its console manually.
Install Windows onto the **80-GiB disk**, leaving the small setup disk untouched.
Finish OOBE, updates and any required activation. Use a disposable image without
personal files, domain enrollment or work accounts. Do not enable BitLocker or
device encryption: clones receive fresh vTPMs and cannot unlock disks tied to the
installation VM's TPM. Verify that the Windows volume is fully decrypted before
sealing; an encrypted base will fail to boot unattended.

## 3. Configure the guest desktop

### Automatic installation into a new empty VM

Instead of manually installing/configuring Windows, the installation VM created
above can be provisioned from its ISO in an elevated PowerShell 7 terminal:

```powershell
$image = Get-Content 'D:\HyperV\AgwintermImage\image.json' -Raw | ConvertFrom-Json
./tools/hyperv-ui-install-image.ps1 -VMId $image.vmId `
    -IsoPath 'D:\iso\Windows11_Client_x64_ru-ru_26300_9457.iso' `
    -StateDirectory './artifacts/hyperv-image-setup' `
    -BaseDestination 'D:\HyperV\Bases\agwinterm-windows11.vhdx'
```

The automatic path selects Windows 11 Pro (or an explicit `-ImageIndex`), applies
the image only to the VM's **empty, non-system OS VHDX**, and configures UEFI boot,
Russian locale with a US keyboard, a disposable setup account and test-account
autologon. It does not install a product key or bypass activation. It disables the
setup account after verifying the test user's interactive shell and an unencrypted
OS volume, then shuts down and seals a base copy. No viewer or host UI input is
used. PowerShell Direct is used only during image setup, not during test runs.

Progress/errors are recorded in `install-state.json` and deployment logs in the
state directory. The recovery setup credential is DPAPI-encrypted for the Windows
administrator running the command; it is never printed and is deleted after a
successful seal. The guest image retains its dedicated test autologon credential,
as described below. A partial failed deployment is retained. To resume after DISM
has successfully applied Windows **but before Windows setup has booted**, explicitly use `-ResumeAppliedImage`; this
requires the installer-created `AGW_EFI` and `AGW_WINDOWS` partitions and never
reformats them. A failed first-boot setup needs a new installation disk. Running
VMs and pre-existing partitions are refused by default. On hosts where BCDBoot
cannot import its template, the installer builds a guest-only BCD store using
the existing Windows `offreg.dll`; no registry hive is attached to the host registry.

After automatic installation succeeds, skip the manual steps below and proceed
to **Validate and run**. Image boot/login readiness is checked by setup; actual
single and parallel UI test validation is still required.

### Manual guest configuration

Inside the installation VM, open **Windows PowerShell as administrator**. Find the
small disk labeled `AGWINTERM_SETUP` (the drive letter may differ):

```powershell
Get-Volume | Where-Object FileSystemLabel -eq AGWINTERM_SETUP
Set-ExecutionPolicy -Scope Process Bypass
E:\hyperv-ui-prepare-guest.ps1 -Bootstrap E:\hyperv-ui-bootstrap.ps1
```

The script prompts for a password for a new `AgwintermUi` local account. Use a
unique disposable password. Built-in Windows autologon stores this credential
in the image's Winlogon registry; keep the image private and never reuse a
personal/domain credential. The account is a local administrator so the
interactive bootstrap can record the run identity and shut down the guest.
No guest credential is supplied to or saved by the host runner.

Setup installs a task triggered at that user's interactive logon, enables Hyper-V
Data Exchange and disables sleep, display timeout and machine inactivity locking.
It refuses physical machines and domain-joined images. This is a dedicated lab
image, not a script to apply to an everyday Windows VM.

Restart the image VM and verify automatic login in the **basic console**, not an
RDP/enhanced session. Finish first-login prompts, disable the test user's screen
saver, use 100% display scaling, and verify that the desktop stays unlocked.
The task waits for a labeled test payload; without one it exits after 90 seconds
and leaves the image VM running. No test runs during image preparation.

Then shut down Windows fully from inside the guest. Do not save the VM state.
Do not run Sysprep after this configuration: the local account/profile and task
are part of the template, and clones run offline. Do not put `C:\AgwintermTests`
or test-result directories into the base image.

## 4. Seal a base copy

Back on the host, after the installation VM reports `Off`:

```powershell
./tools/hyperv-ui-seal-image.ps1 `
    -VMName 'Agwinterm-UI-Image' `
    -Destination 'D:\HyperV\Bases\agwinterm-windows11.vhdx'
```

This copies the standalone OS disk to a new path, marks it read-only and records
a SHA-256 hash in the adjacent `.json`. Existing destinations are never replaced.
The setup disk/DVD and the installation VM are not copied. The template can be
maintained separately; seal each revision at a **new path**. Never modify or
replace a base referenced by existing differencing disks, including retained VMs.
Sealing does not prove desktop readiness: the first UI run below is required.

## 5. Validate and run

Build in the worktree whose changes you want to test:

```powershell
dotnet build Agwinterm.slnx -c Release
./tests/integration/hyperv-ui.ps1 `
    -BaseVhd 'D:\HyperV\Bases\agwinterm-windows11.vhdx' -Suite Quick
```

Once that passes, independent elevated terminals can run concurrently, including
from separate worktrees:

```powershell
# Terminal/session A
./tests/integration/hyperv-ui.ps1 -BaseVhd 'D:\HyperV\Bases\agwinterm-windows11.vhdx' -Suite Navigation

# Terminal/session B
./tests/integration/hyperv-ui.ps1 -BaseVhd 'D:\HyperV\Bases\agwinterm-windows11.vhdx' -Test Win32Control
```

For several HUD suites from the **same worktree/build**, avoid repeated staging
and Windows boots by requesting a batch from PowerShell:

```powershell
./tests/integration/hyperv-ui.ps1 -BaseVhd 'D:\HyperV\Bases\agwinterm-windows11.vhdx' `
    -Suite Quick,Navigation,Picker
```

One VM boots once and runs these suites sequentially, each in a fresh PowerShell
process. The batch stops at the first failure and preserves its exit code. It
shares the guest OS/desktop between suites; use separate invocations when a clean
OS is needed per suite. Separate worktrees always use separate VMs. Standalone
tests cannot be mixed into this HUD batch. Per-suite logs and timings are exported
under `results/hud-batch/`, including `summary.json`. Per-suite logs are written
directly to the results disk so partial output survives a host-enforced timeout.

Resource options for explicit runner invocations:

| Option | Default | Meaning |
|---|---|---|
| `-MaxParallel` | 2 | Concurrent test VM ceiling |
| `-MemoryBudgetInMB` | 8192 | Total guest RAM admission budget |
| `-MemoryInMB` | 4096 | RAM for this VM; must fit the budget |
| `-ProcessorCount` | 2 | Virtual processors for this VM |
| `-CpuMaximum` | 50 / 100 | Per-vCPU cap; 100 on hosts with at least 16 logical processors |
| `-QueueTimeoutSeconds` | 1800 | Maximum wait for capacity, separate from test timeout |

The tightest budget among the incoming and active runners wins. A new worktree
cannot loosen another active run's limits. Stopped retained VMs consume no slot;
running test VMs left by a killed runner still count. Unrelated VMs are not managed,
but their memory consumption reduces the available host memory considered for
admission. Waiting is not FIFO. The budget check/start operation uses a short
machine-wide mutex; tests run concurrently after admission. Staging and disk
preparation happen before admission and can still use host disk bandwidth.

For timing/profiling comparisons, keep VM resources consistent and record them;
CPU throttling and parallel host activity affect measurements. Increase
`-CpuMaximum` only when you deliberately want to trade host headroom for speed.

Or select Hyper-V for normal local entry points in each terminal:

```powershell
$env:AGWINTERM_UI_BACKEND = 'HyperV'
$env:AGWINTERM_UI_BASE_VHD = 'D:\HyperV\Bases\agwinterm-windows11.vhdx'
./tests/integration/hud-ui.ps1 -Suite Navigation -Strict
./tests/integration/win32-control.ps1 -Strict
```

These environment variables are host routing preferences. They cannot authorize
direct UI work. Guest execution additionally checks the Hyper-V machine identity,
prepared account SID, current run ID, VM name from Data Exchange, nonzero session
and an unlocked default desktop. An invalid guest marker fails closed. The guard
prevents accidental routing mistakes; it is not a security boundary against a
guest administrator. Never set `CI`/`GITHUB_ACTIONS` to bypass local routing.

`-Exe`, `-CtlExe`, `-GateExe`, `-TestParameters`, `-PowerShellHome`, `-DotnetHome`,
`-WebView2Home` and `-PythonHome` behave as in the Sandbox runner. For example:

```powershell
./tests/integration/hyperv-ui.ps1 -Test ProgramOverlay -TestParameters @{Dpi=144} `
    -BaseVhd $env:AGWINTERM_UI_BASE_VHD -MemoryInMB 8192 -ProcessorCount 4
```

`-PrepareOnly` stages files without elevation, Hyper-V, an image or a Sandbox
client. It does not validate boot/login readiness or execute tests. The generated
`.wsb` file is a byproduct of shared staging; Hyper-V does not launch it.

## Results, timeout and recovery

Each invocation writes to a unique `artifacts/hyperv-ui/<GUID>/` directory:

- `hyperv-run.json`: exact VM ID/name, run ID and resource settings; no password.
- `input/`: staged payload from this worktree.
- `results/`: bootstrap transcript, stdout/stderr, screenshots, reports, guest
  application state and `result.json` bound to this run ID.
- `vm/`: temporary OS delta, payload VHDX and VM configuration.

The guest shuts down after collecting evidence. Only after the VM is off does
the host mount the payload disk read-only and export results. Logs are therefore
available on the host **after shutdown**, not streamed while tests run.
Test exit codes remain 0 (pass), 1 (test failure), 2 (guest setup/evidence failure).
Missing, malformed or stale results fail the host command.

`-TimeoutSeconds` defaults to 900 and covers boot, automatic login, execution
(the entire batch, if requested) and shutdown after `Start-VM`. Time waiting for
capacity has its own `-QueueTimeoutSeconds` deadline. On timeout the host powers off only its own VM and
attempts to export partial evidence. Stopping/removing a VM uses its returned ID,
never a process-name wildcard or another session's VM name. If shutdown or export
fails, the VM/disks are retained and the run fails.

Installed workers preserve any job containing `run/vm` or
`recovery-required.json`, including after a worker restart. These jobs live in
`C:\ProgramData\AgwintermUiHost\jobs\slot-<n>\<job-id>` and require administrator
recovery; they are not automatically deleted. Inspect the job's
`run/hyperv-run.json`, recover evidence, and stop/remove only its recorded VM.
After detaching its disks, remove that exact job directory. Retained running VMs
still count against the shared limit. A host update can proceed with a retained
job in place when it has a recovery marker and its recorded VM is confirmed off.

Normally the VM and its temporary disks are removed after export, on both passing
and failing tests. Use `-KeepVM` to retain the **stopped** VM and disks for manual
inspection. Before restarting a retained VM for diagnosis, detach its payload
disk in Hyper-V Manager to prevent the bootstrap from rerunning that payload.
The OS delta contains `C:\AgwintermTests`; exported results remain on the host.

If the host runner is killed forcibly or Windows restarts, `finally` cannot clean
up. Read the saved manifest and inspect **that VM ID only** in Hyper-V Manager.
Stop/remove it, then remove that run's `vm/` directory after all disks are detached.
Never delete the shared base or a directory belonging to another session.

For boot/login failures, check encryption, expiry/activation, pending OOBE, the
prepared user's password/autologon, task history and Data Exchange. For a locked
desktop, disable screen saver/locking in the image and seal a new copy. Do not
solve setup failures by running UI tests on the host desktop.

## Measuring and tuning the image

Hyper-V defaults to `-TrimRuntime` and `-DirectPayload`. Runtime trimming keeps
installed patches in the major/minor bands requested by the staged runtimeconfigs,
including the underlying core runtime for Windows Desktop. It omits unrelated
host runtime versions. Direct payload mode creates a guest-only junction from
`C:\AgwintermTests` to that invocation's private writable payload disk, avoiding
a second runtime copy into the OS delta. There are no shared writable runtimes
or cross-worktree caches. For comparison or unusual runtime roll-forward policies,
use `-TrimRuntime:$false -DirectPayload:$false`. Sandbox staging is unchanged.

To tune a prepared guest, retain a baseline, then run
`tools/hyperv-ui-optimize-guest.ps1` **inside the image VM as administrator**.
It disables Search indexing, widgets, consumer suggestions, the first-logon
animation and the test account's OneDrive startup, then runs supported component
cleanup. Security, servicing, fonts and UI/accessibility facilities remain enabled.
The report records settings and free space; cleanup does not guarantee a smaller
VHDX. After clean shutdown, seal a new copy with `hyperv-ui-seal-image.ps1 -Compact`.
Compaction applies only to the new destination, preserving the preceding base.

Use `staging-timing.json` (payload bytes/files and staging duration),
`host-timing.json` (disk preparation, admission/start, guest lifetime and cleanup)
and `results/result.json.timings` (guest payload copy, setup, tests and evidence)
to distinguish slow tests from repeated boot/copy overhead. Guest lifetime includes
boot and shutdown; it is not itself a test execution measurement. HUD batches
also report per-suite durations in `results/hud-batch/summary.json`.

Tune a new base revision only after a working baseline is measured. Compare
repeated single and concurrent runs with the same suite, application build and
resource settings. Record wall time, peak guest memory, CPU activity and all test
outcomes. Changes to Windows components, background tasks, startup applications,
first-logon work and registry settings must preserve the Windows/UI facilities
exercised by the tests. Keep the preceding base available for comparison/rollback.
Test payload/runtime caching and warm-start options should likewise be validated
against stale binaries and state leaking between worktrees before adoption.

### Local measurements (2026-11-03)

Windows 11 Pro from the supplied 26300.9457 ISO, 32 logical host processors,
approximately 64 GiB host RAM, two vCPUs and 4 GiB fixed RAM per VM. All Quick
runs below passed 59 checks with no failures. Concurrent rows represent two
separate VM IDs and disks, with a measured peak of 8 GiB guest RAM.

| Configuration | Concurrent runs | Wall time per invocation | Guest payload copy | Test execution |
| --- | ---: | ---: | ---: | ---: |
| Original staging, baseline image, CPU 50% | 1 | 270 s | 152 s | 18 s |
| Trimmed runtime + direct payload, baseline image, CPU 50% | 2 | 115 s each | 0.3–0.7 s | 25–26 s |
| Trimmed runtime + direct payload, baseline image, CPU 100% | 2 | 103–110 s | under 1 s | 25–26 s |
| Trimmed runtime + direct payload, tuned image, CPU 100% | 2 | 80–85 s | under 1 s | about 16 s |

Staged payload size fell from 1,599 MB / 5,818 files to approximately 557 MB /
2,630 files. These are local observations while the host remained available for
other work, not performance guarantees. Component cleanup and VHD compaction did
not reduce the allocated image file in this run; no space saving is claimed.
Raw timings and test evidence are retained under `artifacts/hyperv-validation/live/`.
The final mixed parallel run also passed: Quick + Navigation in one VM (59 + 141
checks, 152 s wall time) and Win32Control in another (all checks passed, 201 s).
Nine VM invocations completed successfully in total. Temporary test VMs and staged
runtime copies were removed after exporting the evidence; `measurements.json`
summarizes the runs. The installation VM is off between maintenance operations.

The locally prepared image is `D:\HyperV\Bases\agwinterm-optimized-01.vhdx`;
`agwinterm-baseline-01.vhdx` in the same directory is retained for rollback.
From an elevated PowerShell 7 in this worktree:

```powershell
./tests/integration/hyperv-ui.ps1 -BaseVhd 'D:\HyperV\Bases\agwinterm-optimized-01.vhdx' -Suite Quick,Navigation
```

## Clean/offline web and IME acceptance

`WebReleaseAcceptance` tests published packages against actual installed or absent
WebView2 in a disposable guest with no network adapters. Use `TestParameters`
`Mode=Installer|Portable`, `Runtime=Present|Absent`, and `Installer` or `Portable`
with the package path. Run the four cells separately. The present-runtime cells
also run the browser cookbook against the installed/extracted binaries.

`WebImeAcceptance` takes the published `-Exe` and `-CtlExe` plus
`TestParameters.LanguageSource`, a ZIP of matching official Windows language,
Basic typing and Hans font CABs. It installs Chinese Pinyin only after checking
the Hyper-V guest identity, then drives native scan-code input and records
candidate selection, cancellation, focus and terminal isolation. It never
installs a language on the host or modifies the base image. See
[the stage-4 evidence](agterm-stage4-hyperv-acceptance.md) for package provenance,
hashes and acceptance status; Windows media are not part of the repository.

## Host-free validation

```powershell
./tests/integration/hyperv-ui.tests.ps1
./tests/integration/sandbox-ui.tests.ps1
./tests/integration/sandbox-ui-routing.tests.ps1
```

The Hyper-V fixtures exercise unique run identities, exit propagation, stale
results, timeout, export/stop failures, retained VMs, ownership cleanup, resource
admission, HUD batch fail-fast behavior and shared payload staging. They do not substitute for booting a real prepared Windows image
and completing both a single Quick run and two concurrent UI runs.

Microsoft references: [differencing disks](https://learn.microsoft.com/en-us/powershell/module/hyper-v/new-vhd),
[VM creation](https://learn.microsoft.com/en-us/powershell/module/hyper-v/new-vm),
[Data Exchange](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/integration-services-data-exchange).
