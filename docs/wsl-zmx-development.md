# WSL ZMX dependency gate

This is development tooling for OpenSpec `add-wsl-zmx-session-recovery`, not
shipped live-session support. Rust remains the Windows PTY host; ZMX runs only
inside WSL. No profile is enabled by these scripts.

## Explicit isolated provisioning

The developer must authorize the download first. The recipe requires existing
Git, curl, tar, Python 3, coreutils and x86_64 Linux. It downloads Zig 0.16.0 to a
new Linux directory, validates the archive SHA-256, and builds from pinned
sources. It does not invoke a package manager, edit PATH, modify shared Zig
caches or replace an installed ZMX. A failed build directory is retained for
diagnosis; use a new directory for another reproducible run.

From the repository on Windows (adjust the mounted repository path):

```powershell
wsl.exe -d Ubuntu --exec bash /mnt/d/Projects/Agwinterm/tests/integration/zmx/build-linux.sh --download-zig /home/USER/.local/state/agwinterm-zmx-gate-UNIQUE
```

The build records `build.log`, `build.exit`, `provenance.txt`, combined
`patches.sha256`, per-file `patch-files.sha256` and root license texts in
`licenses/`. Source checkouts retain dependency licensing information.

| Component | Pinned revision |
| --- | --- |
| agterm patches and upstream probe | `0716007052a6cedb8c43bb745ce6e5e7d3bc6d2b` |
| ZMX | `8bab1f0173b07e79835ea372d749af3dbf0d0842` |
| ZMX's Ghostty | `8af6897c0afc63037a8a3efee4162a380e3a4572` |
| Zig | `0.16.0`, x86_64 Linux |

Combined patch SHA-256 in upstream application order (ZMX patches, then Ghostty
patches): `671d836781c96a192f44a889650cee6ac9837382b1e386f5df45caa322680dc7`.
The root licenses of all four projects are MIT; retain their notices. This list
does not replace the third-party notices in their source trees.

## Routed runtime probe

```powershell
pwsh -File tests/integration/wsl-zmx-dependency.ps1 -Distro Ubuntu -BuildDirectory /home/USER/.local/state/agwinterm-zmx-gate-UNIQUE
```

The entry point routes through `wsl-ui.ps1` before runtime work. The upstream
probe uses a private Linux socket directory and differently sized test PTYs.
It first runs the pinned Zig unit tests, including atomic socket publication.
The PTY probe checks managed leader/follower notices, authoritative screen dimensions,
acknowledged input, no implicit leadership takeover and snapshot formatting.
Its transcript and private desktop result are retained under `artifacts/wsl-ui`.
Never run it with CI flags to bypass routing.

A successful compile or version string is not readiness evidence. The upstream
probe alone does not prove incarnation-safe
termination, Bash launch coverage, Rust transport survival or complete recovery.
Those gates remain required before enabling live mode. Shutdown of the WSL
distribution or Windows reboot loses the workload; a resumed conversation is a
new process. Native profiles, transient surfaces and non-Rust hosts are outside
the planned initial live-mode support.

## Evidence: 2026-11-02

Ubuntu x86_64 on WSL kernel `6.18.54.1-microsoft-standard-WSL2`:

- Isolated ReleaseSafe build passed with Zig 0.16.0. Binary SHA-256:
  `db4685c86581b66b8a10ddbd242811ae52580dff29c299e28b60b6790f77620c`.
- `zig build test` passed, including both patched socket publication tests.
- All 16 upstream PTY capability checks passed, `RESULT ok`.
- Runner result exit 0, private desktop `AgwintermWsl-34f3472b756341c5bbcffbf2c2ddcd27`,
  input desktop `Default`. Logs: `artifacts/wsl-ui/20261102T095821-3912195f/worker.log`
  and `result.json`; build provenance/log/licenses: `artifacts/zmx-gate/20261102/`.
- Build retained at `/home/alzo/.local/state/agwinterm-zmx-gate-20261102-02`.
  An earlier successful build under `/tmp` disappeared after WSL became idle;
  use a persistent private build directory for multi-run verification.

This passes task 1.1 only. Exact owned termination has not yet been accepted.

## Blocking survival result

Run the Rust transport fixture through its normal routed entry point:

```powershell
pwsh -File tests/integration/wsl-zmx-survival.ps1 -Distro Ubuntu -BuildDirectory /home/USER/.local/state/agwinterm-zmx-gate-UNIQUE
```

It requires local Release app/CLI builds and `agwinterm-ptyhost.exe` beside the
test app. It creates a unique app ID, Rust host namespace, Linux socket directory
and sentinel record directory. The profile launches Bash, which execs a Python
sentinel. Exclusive record-directory creation detects a duplicate workload
launch. Only its recorded process handle is used for Linux cleanup; an absent or
replaced process is never signalled. Test evidence/configuration is retained.

On 2026-11-02 the first complete lifecycle run **failed task 1.2**:

| Transition | Result |
| --- | --- |
| Graceful UI quit (targeted WM_CLOSE) | Same daemon and workload incarnation |
| UI restore, then forced UI-only exit | Same daemon and workload incarnation |
| Kill only the isolated Rust host, wait 20 seconds without WSL probes | Both daemon and workload absent |

Initial daemon PID/start ticks: `1167/6761`; workload: `1168/6761`.
Kernel boot ID before and after: `bd0178cf-68ea-48ee-b580-2cafc8b6a466`.
An unchanged kernel boot ID does not rule out distribution userspace shutdown.
The fixture did not issue WSL shutdown/terminate or kill any user host/session.
It did not run a keepalive during the idle interval. Exact mechanism of loss
(WSL distribution lifetime versus transport teardown) remains unresolved.

Evidence: `artifacts/wsl-ui/20261102T100230-0ea89cc6/worker.log`, `test.log`,
`result.json` (exit 1, private desktop distinct from `Default`). Durable sentinel:
`/home/alzo/.local/state/agwinterm-zmx-gate-20261102-02/survival-937573aade8241de83c7d4c2f0d0a441/identity.json`.

**Historical blocker, resolved below.** Do not proceed to production
lifecycle integration on the strength of the capability probe. Next investigate
the distribution/transport lifetime boundary with a diagnostic fixture. If a
separate lifetime keeper or service is required, revise the design explicitly
before provisioning one; silently adding a keepalive would mask this failed gate.
Rust hosting and WSL-only ZMX remain the required architecture.

## Root cause and corrected survival gate (2026-11-02)

An isolated Ubuntu Base 24.04.5/systemd distribution named
`Agwinterm-Zmx-Gate-20261102` was imported under
`artifacts/zmx-disposable/distro`. Rootfs SHA-256:
`e77b6f10c2590cef872b33ee9f635a0e3fd1f57fb074c0e52b5c7f56147a0c86`.
Only this disposable distribution was configured/restarted; user Ubuntu was
never stopped. Python 3 and systemd were provisioned only in this fixture.

The no-lease negative control reproduces the failure reliably:
`artifacts/wsl-ui/20261102T111859-0a421aac/worker.log`. After host loss,
init start ticks changed `32261 -> 38858` and PID namespace changed
`4026532257 -> 4026532258`, while kernel boot ID remained unchanged.
The sentinel recorded SIGHUP. This is distribution userspace shutdown, not a
Rust command terminating ZMX. Other user WSL sessions masked the failure in
the initial repeated runs. Microsoft documents that systemd services do not
keep a WSL instance alive: https://learn.microsoft.com/en-us/windows/wsl/systemd.

The initial test-only `zmx_lifetime.py` prototype pins the exact workload with a pidfd and remains
a foreground WSL invocation independently of UI/PTY host. It does not allocate
a PTY, send signals, restart work or change global WSL settings. It exits when
the pinned workload exits. Bounded input and readiness reject stale identities.

```powershell
pwsh -File tests/integration/wsl-zmx-survival.ps1 -Distro Agwinterm-Zmx-Gate-20261102 -BuildDirectory /root/zmx-build -LifetimeGuard
```

Passed: `artifacts/wsl-ui/20261102T112221-a669fac5/worker.log` and `result.json`
(exit 0, private desktop). Same daemon `159/52881` and workload `160/52881`
survived graceful quit, forced UI exit and Rust host loss with 60 seconds of
idle time. Guard automatically exited after exact sentinel cleanup. This closes
task 1.2; production creation/ledger integration of the lease is still pending.

## Exact termination prototype

Task 1.3 passed in `artifacts/wsl-ui/20261102T112536-a1cf4131/worker.log`.
The pinned x86_64 Linux ABI uses an 8-byte packed IPC header and 552-byte Info
reply. Info identifies the workload; `/proc` identifies its daemon parent.
The prototype validates daemon executable, kernel boot ID, PID namespace,
process start ticks and socket device/inode while holding a daemon pidfd.
It repeats Info on the **same connected Unix socket** before sending Kill on
that connection. It never checks a name and then runs `zmx kill NAME`.
The connected socket cannot retarget a replacement daemon; the pidfd confirms
the old process exited even when the termination reply is deliberately lost.

Tests cover external-ownership refusal, real daemon-name reuse, a deterministic
numeric-PID reuse model (current PID with stale start evidence), lost replies,
old pidfd exit versus a live replacement, and exact owned termination. The test
does not force kernel PID allocation or claim that arbitrary PIDs were recycled.
Supported first baseline: Linux with `pidfd_open` (kernel 5.3+),
x86_64 ABI and the pinned patched ZMX. Other ABIs require separately pinned
fixtures. No mutation is authorized by PID or name alone.

## Readiness and provisioning contract

The developer explicitly builds and runs `wsl-zmx-dependency.ps1`. Only after all
runtime checks pass does that runner write `<executable>.agwinterm-capabilities.json`,
containing SHA-256, tested capabilities and build provenance. Keep it beside the
binary when explicitly provisioning another endpoint. The file is local trusted
test evidence, not a vendor signature; it must not be synthesized from `--version`.
Replacing or upgrading the binary requires rerunning the capability suite.

Readiness is read-only and starts no daemon:

```powershell
'{"verb":"probe","executable":"/ABSOLUTE/PATH/TO/zmx"}' | wsl.exe -d Ubuntu --exec /ABSOLUTE/PATH/TO/agwinterm-hook --zmx
```

It resolves the actual distribution/user, checks pidfd availability, verifies
the executable's bytes against runtime evidence, and returns `ready`,
`missing-executable` or `unsupported` with a reason/missing capabilities. It
does not run an unverified binary to discover commands. Missing or malformed
evidence refuses, as does a stock binary without the required patched capability
evidence. SSH/remote attachment and presentation remain false. Checked on the
real pinned build plus host-free malformed/missing/changed-binary fixtures.

Bash `--noprofile --norc -c` launch is exercised by the survival and identity
fixtures. This is the development baseline, not yet production profile support.
Live mode requires ordinary WSL panes and the Rust host; Windows-native profiles,
quick/scratch/program terminals and other host backends remain unsupported.
Explicit WSL shutdown, loss of the lifetime lease and Windows logout/reboot can
destroy workloads. Recovery must report loss and require explicit relaunch.

## Checkpoint format and downgrade

`PaneState.LiveBinding` is an optional additive JSON value. Its binding object
has version 1; the existing window checkpoint gains no top-level version.
Absent means ordinary restore. Present but null, corrupt or unknown-version
means unavailable live recovery, preserving the original value and suppressing
buffer/agent/pin/captured-command replay. Recorded distribution/user and origin
identity are authoritative even when profile/defaults change. Valid bindings
adopt the exact Rust creation ticket first, otherwise reconcile their operation
and attach the exact Linux connection. Invalid bindings display an unstarted
error surface without falling back to another shell/backend.
Write failures retain the in-memory binding and show a checkpoint failure toast;
they do not terminate the Linux workload or claim durable recovery.

Back up checkpoints before first enabling live mode. Older binaries ignore and
drop this field and may replay ordinary restore sources; do not open live
checkpoints in an older version. Disable new live creation without stripping
existing bindings. Downgrade only after explicitly detaching/terminating work
and preserving a backup; clearing checkpoint/history is not workload cleanup.

## Linux lifecycle implementation and exact attachment

The bundled Rust `agwinterm-hook --zmx` accepts a single versioned operation-ledger
request (64 KiB maximum), with exact fields for create/reconcile/terminate,
inventory and attach. Ledger directories are Linux-user-owned mode 0700;
records are mode 0600, serialized with flock, written with file/directory fsync
and atomic replacement. A canonical operation UUID permanently reserves a
random independent daemon name and launch fingerprint. Corrupt/partial records
refuse; leftover temporary files never replace committed records. Do not delete
ledger entries to retry an uncertain launch. Reconcile the original operation.

The workload wrapper commits an exclusive one-shot marker before exec. Ready
requires matching marker, daemon/socket/process evidence and an acquired pidfd
lifetime lease. A concurrent retry cannot change a live creator's launch state.
Helper restart reacquires a lease for the same incarnation; it never executes
the command again. Missing socket alone is uncertainty if the workload is alive.
A committed lease survives a broken stdout pipe after a lost creation reply.

Pinned upstream `attach` can create a replacement daemon. The local reproducible
`patch-existing-fd.py` seam adds `ZMX_ATTACH_FD`: it skips session creation and
uses the already connected socket inherited from the exact-identity helper.
It refuses labels and session switching on this path. No wire ABI or Rust host
change is involved. All inherited ZMX variables are removed before controlled
values are set. Readiness now also requires `existing-fd-attach`; the earlier
binary's evidence is insufficient for production recovery. New test binary SHA:
`ba05218d53ac78de872d2e7061052ee30e0a6348798d78774fd945ca498f6380`.

Dependency suite including existing-fd positive/invalid-fd probes passed in
`artifacts/wsl-ui/20261102T115256-240b6121`. Lifecycle/identity tests passed in
`artifacts/wsl-ui/20261102T115456-fc040aef`: five concurrent/repeated creates,
one command counter, helper restart, two exact attachments, one-client detach,
read-only inventory, malformed/stale/missing refusals, and a lost termination
confirmation reconciled against the recorded process. Ledger crash/partial-write
and readiness host-free tests also pass. These are Linux coordinator gates;
Windows UI/CLI lifecycle integration and full acceptance remain pending.

Detach means ending only the selected client/Windows transport. The Linux
helper does not call upstream global detach. Owned close uses the ledger's exact
identity and connected kill channel; external identity alone has no authority.
An unconfirmed kill remains uncertain. A retry of a durable terminating record
can confirm the original daemon's exit even when its old name has been reused.

## Windows integration and acceptance (29/29 tasks)

`WslLiveClient` installs only the bundled native application helper into a private,
content-addressed Linux directory. It never installs Python/ZMX, changes PATH or
creates a service. The initial resolve records actual distribution/user/cwd;
later operations use those selectors and the persisted helper/ledger paths.
An owned pending binding is synchronously saved before the create request.
Transport tickets remain separate from workload identity; the existing Rust
protocol is unchanged. The optional tree `liveSessions` map reports endpoint,
incarnation, ownership, recovery/error and owned-close consequences.

Routed Windows/Rust harness passed in
`artifacts/wsl-ui/20261102T120253-776e3abd`: wrong-ticket refusal, same Windows
client on matching adoption, a new Rust transport on the same Linux workload,
exact termination and missing-state reconciliation. Actual opt-in UI profile
passed in `artifacts/wsl-ui/20261102T121025-f704cc39`: graceful quit, forced UI
exit, test Rust-host kill, 60 seconds without probes, and daemon reattachment.
The same sentinel recorded zero input bytes with saved agent, pin and captured
commands enabled during restore. No auxiliary test lifetime guard was used.
Focused checkpoint/profile/control regression suite: 102 tests pass; Win32
Release x64 build has no warnings; strict OpenSpec validation passes.
The repeated lifecycle/Rust harness in
`artifacts/wsl-ui/20261102T121642-9e39434d` also verifies a genuinely closed
creation-response pipe: the committed lease remains alive, reconciliation finds
the original guard/workload, and exact termination releases it.

Recovery cancellation now fences stale callbacks and serializes retry/close.
Deterministic helper tests distinguish dependency unavailable/unsupported from
uncertain socket loss and proven workload loss. Four explicit fresh-start
sources run individually once with new identities, including repeated request
and live-restart refusal checks. `artifacts/wsl-ui/20261102T125005-47e2389d`
also verifies borrowed attachment authority, differently sized followers,
follower resize/reconnect preserving leader geometry, and exact Rust adoption.

`artifacts/wsl-ui/20261102T124351-2dfe9040` passes the actual UI/CLI flow:
inventory, second attachment, authoritative follower screen/cursor, readonly
refusal, once-only acknowledged input, explicit leadership, retry, owned
termination, retained missing pane, missing-source refusal, selected pin fresh
start, and attachment-only detach. Four deterministic helper tests include lost
input acknowledgements with exactly one send. 224 focused control/restore/Recent
unit tests pass. Managed-role notices are consumed with nonce/generation checks
instead of becoming terminal titles. UI recovery actions show ownership and
the consequence of owned close. See [control contracts](control-api.md#opt-in-wsl-live-sessions-development).

| Recovery | Meaning | Automatic command replay |
| --- | --- | --- |
| `host-adopted` | Exact saved Rust transport ticket adopted | None |
| `zmx-reattached` | New transport, same verified Linux incarnation | None |
| `fresh-started` | Explicit creation, new incarnation | Only selected creation source |
| `missing` | Recorded workload is proven gone | None; explicit fresh start required |
| `unsupported` | Dependency lacks required capability | None |
| `unavailable` | Dependency/transport cannot currently be reached | None |
| `uncertain` | Outcome cannot be proved, including lost replies | None; reconcile same operation |

The completed stage remains explicitly opt-in and disabled by default. This
repository implementation has been reviewed and archived as `2026-11-02-add-wsl-zmx-session-recovery`; it has not been packaged or deployed.

## Final acceptance: 2026-11-02

- Final ZMX binary SHA-256:
  `e87dd9bd723414b45e6b41a5cea0c3ce64165664f73c621e07a2fa7a311bc9f9`.
  `patch-primary-replay.py` SHA-256:
  `24a764151e40b5a750e011bba4ea364ad6ba98191290210c0fb85860f033d66f`.
  It follows the existing-fd seam and pinned agterm patches. Readiness now also
  requires `primary-history-replay`; earlier development binaries do not qualify.
  Both local patch digests are included in the capability evidence/build recipe.
- Dependency unit tests, the new full-primary-history regression and all upstream
  runtime probes passed in `artifacts/wsl-ui/20261102T131948-bcfcfc51`.
- Rust/managed snapshots now retain keyboard stacks and the hidden primary screen.
  ZMX snapshots retain both screens and flush every history row before clearing
  the viewport. No Rust wire-protocol change was needed.
  `artifacts/wsl-ui/20261102T132850-65738360` verifies styled CJK/emoji wide cells,
  cursor, alternate/normal screen, keyboard/bracketed-paste modes, all 90 history
  lines exactly once on both recovery paths, and one query response with two
  clients. Exact startup orphan cleanup preserves the Linux workload.
- The same run completed three create/adopt/reattach/detach/terminate cycles in
  3,354 ms, returning Windows transport inventory to baseline after every cycle.
  Rust host working set was 7,565,312 bytes, peak 8,511,488 bytes. These are small
  local fixture observations, not performance guarantees.
- `artifacts/wsl-ui/20261102T133009-1671452c` is the final UI matrix (exit 0,
  private desktop, input desktop unchanged). It covers UI quit/crash, Rust host
  loss with 60 seconds of no probes, zero restore input, CLI controls, three
  immediate create/close races, split/swap/survivor promotion, fresh Recent
  reopen, retained unexecuted pins, metadata-only clear, two-window restore,
  backup/disable/re-enable, changed defaults, missing dependency, ordinary
  scratch inheritance, and no migration of an old ordinary checkpoint.
- The matrix uses root and `zmxpeer` only in disposable distribution
  `Agwinterm-Zmx-Gate-20261102`. Stopping that distribution proves missing-state
  readback for both users; only the explicitly selected shell restarts. The user's
  Ubuntu was not stopped and no lifecycle workload was launched there. Actual
  Windows reboot/logoff and cross-distribution lifecycle testing remain unverified;
  the accepted destructive-loss gate uses the dedicated distribution shutdown.
- Ordinary Windows regressions passed in background Windows Sandbox:
  Recent (25 checks), `artifacts/sandbox/20261102T125454-c2c94798`;
  complete RestoreRoundtrip, `artifacts/sandbox/20261102T130434-7fc33a71`;
  Win32Control, `artifacts/sandbox/20261102T131239-2a6b3102`.
  Each test-owned Sandbox was stopped by its exact returned environment ID.
- Environment: Windows 10.0.26300.9818, WSL 3.0.4.0, kernel
  6.18.54.1-microsoft-standard-WSL2, .NET SDK 10.0.303, pinned Zig 0.16.0.
- Final host-free validation: 50 Rust core tests, 595 managed/native Core tests,
  294 focused PTY/CLI/restore/Recent/profile/startup-cleanup tests, and six Python
  readiness/recovery tests passed. Strict OpenSpec validation and `git diff --check`
  pass. The changed Rust file passes formatting; workspace-wide `cargo fmt --check`
  still reports pre-existing formatting in the untouched hook/resize sources.

The supported delivered surface is local WSL only. Outbound/inbound agterm
attachment and presentation remain separate required stages 2–4, not implied by
the compatibility model fixtures or the local control commands.

## Explicit profile setup and rollback

### PR review recovery corrections (2026-11-02)

Greptile identified three edge cases after the initial acceptance. Pending
restarts now retain the exact predecessor binding. Reconciliation atomically
tombstones an unreserved new operation before rolling back, so a delayed request
cannot launch after rollback; an already committed operation is recovered even
if its workload has since exited. Explicit restart refreshes the application's
content-addressed helper at the same saved endpoint, without upgrading ZMX.

The ledger keeps original shell argv separate from each selected restart command,
so a later shell restart cannot repeat a pin, agent or captured command. Discovery
uses a daemon-to-operation index for live sockets and retains historical records
for idempotency instead of imposing a 1,024-operation lifetime limit.
The owner index is published with an atomic, non-replacing hard link only after
the complete temporary file is flushed, then the directory is flushed. Process
exit immediately before or after publication leaves either no lookup or a
complete lookup; replay can repair the former from the committed operation.
Regression tests kill the writer at both boundaries and verify recovery plus
refusal to replace a conflicting owner.

Regression checks cover the pre-reservation interruption, delayed request fence,
committed restart whose reply was lost, chained command-then-shell restart and
discovery after 1,025 historical operations. The routed real Rust/WSL harness also
covers rollback followed by a successful explicit restart and complete terminal
continuity (`artifacts/wsl-ui/20261102T154749-55cc9a04`).

### Native helper migration (2026-11-02)

Production lifecycle control now lives in `native/agwinterm-hook/src/zmx` and
runs as `agwinterm-hook --zmx`, separate from ordinary hook dispatch/watchdogs.
The same static musl x64/arm64 payloads are already part of installer/portable
packaging. `--zmx-install` pins a content-addressed executable under the existing
endpoint; it does not edit shell/agent configuration or install dependencies.
Legacy script helper checkpoints migrate at their recorded distribution/user,
ledger and socket directory. UUIDs, daemon identities, one-shot markers and v1
Python-compatible launch fingerprints remain unchanged. There is no Python
fallback. Missing bundled helpers leave a recovery error.

Python references now live under `tests/integration/zmx/reference` only. Test
drivers may still use Python; the actual lifecycle suite invokes the native
binary with an interpreter-free PATH and an initial `/bin/bash` + `/bin/sleep`
workload. Native protocol tests read old Python-written ledgers, recover both
interrupted and committed restarts, and check inventory after 1,025 operations.
Native unit tests include actual writer process exits before/after atomic index
publication. Readiness tests compare native responses against the former contract.

Local verification: 21 Rust tests plus a subprocess-only crash worker; static
x64/arm64 builds; 76 focused .NET tests; 15 existing WSL hook/install tests;
native readiness and four protocol tests; warning-free Windows release build.
Standalone portable extraction verified the runnable CLI, Windows bridge and
the exact x64/arm64 ELF payloads from the build inputs.
The real Rust/WSL harness passed at
`artifacts/wsl-ui/20261102T163411-724f895b` (including legacy helper migration
and Bash-specific selected-command restart),
and full lifecycle UI matrix at `artifacts/wsl-ui/20261102T162012-7856fac3`
(two Linux users, 60-second idle host loss, explicit disposable shutdown).
These runners use a private desktop, leaving the input desktop unchanged.

Selected command restarts preserve the original recognized shell and its flags,
including options with values and combined `-lc` flags. Non-shell profiles keep
the previous `$SHELL` fallback, with the command passed as a positional argument.
The real lifecycle test uses Bash `[[ ... ]]` and checks `extglob` remains enabled
for agent/pin/captured restarts; subsequent shell restart retains original argv.
Implementation commit `41a4644` passed all CI jobs in run `41454213130` and
Greptile review `37560633` completed at 5/5 with every finding resolved.

No quantified performance improvement is promised. Rust removes interpreter
startup and retained interpreter memory; WSL process invocation and daemon
snapshot/replay remain costs. The three-cycle end-to-end harness measured about
3.5 seconds with both implementations and is not a controlled latency benchmark.

### Setup

Back up the application's `profiles.json`, `windows/` checkpoints and recent
history before testing. Configure a separate profile in the isolated test install:

```json
{
  "name": "WSL live",
  "command": "wsl.exe",
  "args": ["-d", "Ubuntu", "--exec", "bash", "--noprofile", "--norc"],
  "zmx": { "enabled": true, "executable": "/home/USER/private-build/zmx/zig-out/bin/zmx" }
}
```

Use the explicitly built, attested executable; ordinary upstream ZMX is not a
compatible substitute. Python is not a runtime dependency. The application ships
static x64/arm64 Linux helpers; Python remains only in test/build tooling.
Missing dependencies yield a retained error pane, not an ordinary shell fallback.
The embedded application helper installation does not install dependencies.

Disable `zmx.enabled` to prevent new live sessions. Existing saved live bindings
still identify their recorded endpoint and must be detached or terminated by
explicit choice before downgrade. Detach keeps the Linux workload and its
lifetime lease; owned close/terminate ends it. Keep ledger/checkpoint backups.
Clearing restore/Recent metadata is not a workload termination operation.
An older application may ignore additive live-binding metadata and launch
ordinary shells: do not reopen live checkpoints with it. Restore the pre-feature
checkpoint backup only after deciding what to do with retained work.
