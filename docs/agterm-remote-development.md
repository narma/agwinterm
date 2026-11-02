# Outbound agterm attachment development

Updated 2026-11-02. OpenSpec: `add-agterm-remote-attach`. Implementation and real-peer acceptance are complete; PR checks and review are tracked in [PR #25](https://github.com/narma/agwinterm/pull/25).

## Implemented slice

`AgtermSshDiscoveryClient` executes read-only discovery through explicitly resolved WSL distribution/user and an SSH destination, optionally using an existing agent socket. It uses a non-PTY connection, batch authentication, strict host-key checking, no key updates, no forwarding and no local SSH command. It appends the standard CLI locations to the origin PATH through `/bin/sh`; the account-shell boundary escapes apostrophes and backslashes. No shell profile launch command, helper upload, dependency installation or workload creation occurs.

Handshake timeout is 5 seconds, overall timeout is 20 seconds. Stdout is limited to 4 MiB, stderr to 64 KiB (both byte limits). Concurrent reads avoid pipe saturation. Timeout, cancellation, invalid UTF-8 or an oversized pipe fails the request and stops only its local process tree. There is no automatic retry. A nonzero process exit rejects even success-shaped stdout; stderr is not surfaced as trusted terminal text.

`AgtermRemoteDiscovery` decodes `ok/result/remote`, validates origin executable/socket paths and bounds the inventory to 4096 sessions and JSON depth 32. It rejects duplicate properties, IDs, daemon claims, roles, conflicting window/workspace mappings, missing left panes, invalid split axes, truncated responses and absent endpoints. Split panes are ordered by role; `vertical` and `horizontal` retain upstream wire meaning. Required session fields are id/name, windowID/windowName, workspaceID/workspaceName, cwd and panes. splitAxis is required for two panes and absent/null for one. Optional parent/content determine eligibility; other additive metadata is ignored. Page and terminal companions remain visible but are unsupported for import. A valid empty inventory is success. Origin metadata is not daemon-incarnation proof and never enables attachment.

The initial discovery slice was followed by the persisted provider and native bridge described below.

## Evidence

Source shapes checked against agterm revision `9133258994a6cafb330ee354df949b76b1b2834b`: `RemoteTree.swift` and `ControlPayloads.swift` under `agtermCore/Sources/agtermCore`. The old stage-1 tree fixture is a derived model, not a raw control envelope. `Fixtures/WslZmx/remote-empty.json` captures the actual supplied peer's empty response, with JSON slash escaping normalized. No private session titles or IDs are captured.

Read-only macOS arm64 peer observation:

- agterm bundle version/build: `0.38.1`.
- ZMX version: `0.8.1`; installed binary SHA-256: `4db635396d83ded73e102fb365375a9216c01da515d58c796537b83210b6eb01`.
- Origin executable: `/Applications/agterm.app/Contents/MacOS/zmx`.
- `agtermctl zmx tree --json`: successful empty sessions.
- `agtermctl zmx list --json`: live mode configured/active/requested at launch all `none`; claimed daemons absent. No settings or sessions were changed.
- The installed binary's source revision/patch manifest has not been established. Version text is not proof that the existing-FD or managed-exit patches are present.

Host-free targeted tests: 45 passed (new discovery/transport tests and existing `ZmxCompatibilityTests`), including hidden process fixtures for exit codes, stderr saturation, output limits and cancellation. These tests create no terminal UI. Existing unrelated xUnit analyzer warnings remain in `SessionNewWorkspaceTests` and `RestoreStateTests`.

Final Release verification: the same 45 tests passed, plus 89 host-free WSL profile/identity and restore-state regression tests (`WslIntegrationTests`, `WslLiveIdentityTests`, `RestoreStateTests`). Strict OpenSpec validation and `git diff --check` passed. UI/terminal acceptance has not been run for this discovery-only slice.

Read-only acceptance runner (no UI, PTY or workload creation):

```powershell
dotnet run --project tests/Agwinterm.RemoteDiscoveryHarness -- <distribution> <user> <ssh-destination> [agent-socket]
```

The runner successfully returned `count: 0`, `terminalCandidates: 0`, `attachmentVerified: false` against the supplied host using its existing SSH agent. Endpoint settings and agent paths are invocation inputs, not committed personal defaults.

## Stage-2 implementation and shared native code

The remote provider now implements explicit list/import, borrowed close/detach, leadership,
authoritative screen/input, exact-ticket UI adoption, bounded reconnect and Recent reopen.
`agwinterm-zmx` is a shared Cargo workspace member used by both `agwinterm-hook` and
`agwinterm-remote`. It owns bounded ZMX framing and existing socket/process identity.
Linux ownership records, creation, pidfd fencing and termination stay in the hook. Linux
retains its original process/inode checks: `SO_PEERCRED` on a listening socket can identify
the pre-fork creator rather than the ZMX daemon. macOS additionally verifies `LOCAL_PEERPID`.
Zombie processes retain `ESRCH`, so the hook recognizes completed workloads during cleanup.
The
remote executable exposes no creation, restart or termination operation. No interpreter
or other new installed runtime is required; Python/Swift in integration scripts are test tooling.

Remote metadata uses a separate validated model, discriminated by `Provider: agterm-ssh`
inside the existing raw `LiveBinding` checkpoint envelope. Malformed metadata remains an
error surface. Older builds refuse the unknown binding rather than replay its saved commands.
Bindings pin the WSL distribution/user, SSH destination/agent socket, optional origin control
socket, endpoint paths, origin topology, daemon incarnation and client build hash. Removing
profiles or changing defaults cannot redirect them. Clearing Recent/restore metadata does
not send a lifecycle operation to the origin. A missing origin cannot be recreated by this viewer.

Example (all paths and identities are explicit inputs):

```powershell
agwintermctl zmx remote list --distribution Ubuntu --user USER --host HOST --agent-socket /home/USER/.ssh/ssh-agent.sock
agwintermctl zmx remote attach --distribution Ubuntu --user USER --host HOST --agent-socket /home/USER/.ssh/ssh-agent.sock --bridge /absolute/agwinterm-remote --session ORIGIN-ID --window LOCAL-WINDOW-ID
agwintermctl zmx remote lead --target LOCAL-PANE-ID
agwintermctl zmx remote detach --target LOCAL-PANE-ID
```

`--origin-socket` addresses an explicitly selected agterm instance, particularly isolated
tests. Import re-reads discovery and probes all pane identities before publication. Each
pane then reports its own attachment state. It never reports a split as connected because
only one side succeeded. Finite requests are bounded to 20 seconds each; CLI import allows
90 seconds for discovery, two probes and UI publication. SSH terminal keepalives detect
dead connections. Five automatic retries use 1/2/4/8/16-second backoff; successful attachment
or explicit retry resets the budget. Reconnect is follower-only; `lead` is the explicit geometry claim.
Input is never retried after an uncertain reply. Styled authoritative queries are refused.

## Explicit real-peer provisioning and rollback

The user authorized all stage-2 source/test transfers, isolated builds and test processes.
`tests/integration/zmx/build-origin.sh NEW_ROOT SOURCE_ROOT` builds only in a new `/tmp`
prefix on an Apple Silicon Mac. It pins agterm patch sources to
`9133258994a6cafb330ee354df949b76b1b2834b`, ZMX to
`8bab1f0173b07e79835ea372d749af3dbf0d0842`, Ghostty to
`8af6897c0afc63037a8a3efee4162a380e3a4572`, and Zig 0.16.0 with its published checksum.
Upstream patches include managed exit; local patches add inherited-FD attach and hidden
primary-history replay. `remote-origin-tests.py` verifies no-create FD attachment before
publishing the client SHA manifest.

Replay comes from the daemon. The installed stock ZMX was insufficient for hidden primary
history; a patched viewer alone does not fix it. The bridge therefore requires an identical,
explicitly provisioned daemon/client build, in addition to process/socket identity. The
running daemon must also answer bounded IPC tag 26 with the pinned source/feature build
from `zmx-runtime-build.txt`. A changed file on disk cannot attest an older mapped image;
missing or mismatched runtime replies refuse before probe, screen, input or attach.
The
initial application was agterm 0.38.1 (`09727610`), copied into an isolated directory with
the patched ZMX. Final acceptance also builds and runs the exact `9133258` application pin.
macOS signing can
change binary bytes: attest the final signed binary and use that same binary for the viewer.
No installed app, SSH service, host key or user session was replaced.

`remote-peer-fixture.py start /tmp/aw-stage2-peer-UNIQUE [BUILT_ZMX]` creates separate state,
control socket and, when a binary is supplied, a separate app bundle. Per-process macOS
restoration flags prevent the system's unrelated saved window state from suppressing its
first window. `stop` verifies the control socket's peer PID against the recorded PID before
closing fixture windows. Keep isolated directories until evidence is collected; removal
must target only those explicit test directories. Primary user state is not a rollback target.

## Current acceptance evidence (2026-11-02)

- Linux: 21 hook tests and 4 shared-library tests passed; the ignored hook entry is a
  subprocess crash worker exercised by its parent test.
- Windows: 202 targeted remote/SSH/WSL/identity/restore/ticket unit tests passed; Release build passed.
- macOS: existing-FD attach, missing socket/no-create, exact process identity, authoritative
  read/input, borrowed detach and socket/name replacement refusal passed.
- Private Windows desktop against real agterm: single and split import, per-pane input/read,
  exact-ticket UI restart, forced Rust-host loss, follower reconnect, borrowed close/Recent,
  explicit leadership and detach passed.
- Local WSL profile/lifecycle regression passed, including split/swap, two-window restore,
  owned close and fresh Recent semantics. Ordinary Windows RestoreRoundtrip passed in
  background Windows Sandbox.

Terminal continuity and the expanded fault matrix passed on the exact application pin.
CI and review status must be read from the PR for its current head.


The initial copied-app signed ZMX pair had SHA-256 `bee4afb0bd75bdd06f21fb2f73e5031e2ccbddb43b1e0b50bb367a6985602345`. The final pinned-app hashes are recorded below.

Real-client terminal continuity passed: styled wide glyphs, alternative screen and input modes, follower geometry, one query response, hidden primary history exactly once, and exact reconnect. SSH transport loss, saved buffer/command suppression, and metadata clear passed. Background Sandbox Recent passed all 25 checks.

Expanded fault acceptance passed: six separate SSH outages recover as followers, a closed
destination window cannot redirect import, one failed split pane leaves its healthy sibling
usable, lost input replies produce failure after exactly one delivery, and an absent-origin
Recent entry stays unavailable without changing the original workload. Tests run through
`wsl-agterm-remote.ps1`, which routes to the private non-input Windows desktop. The fault
bridge is test-only Python in an isolated `/tmp/aw-stage2-fault-*` directory; it is not shipped.
Background Windows Sandbox `Win32Control` passed, in addition to RestoreRoundtrip and Recent.

For exact application-pin verification, `build-pinned-app.sh` builds the app at `9133258`
using its pinned Ghostty library, the already built compatible ZMX, and XcodeGen 2.44.1.
It needs Xcode's build-only Metal component and gettext; it does not run upstream setup's
implicit package installation. All app/source/build products remain under the explicit
test build root. Pass that resulting bundle as the fourth argument to `remote-peer-fixture.py
start ROOT BUILT_ZMX BUILT_APP` to create a separate test instance. The normal installed app
is never a replacement target.

## Pinned acceptance and build manifest

The actual app and its bundled CLI were built from agterm `9133258994a6cafb330ee354df949b76b1b2834b`.
The single and vertical-split raw control envelopes are retained as `remote-pin-single.json`
and `remote-pin-split.json` under the Pty test fixtures, with only names, IDs, daemon names,
paths and JSON formatting anonymized. Required/optional fields match the pinned Swift
contracts described above; the optional `presentation: 1` never grants local presentation.

Final SHA-256 values after signing the isolated fixture:

| Artifact | SHA-256 |
| --- | --- |
| agterm executable | `8fa3bc6d7bfb47a5457f272366700b83d12c8467b684462e14528be1cc312cdd` |
| daemon and existing-only viewer (identical bytes) | `93735bb87860cddc4c9189ddf56b49cae601cd50a5e5f89e622617fcd29c785d` |
| final Rust origin bridge | `d1fa60b82360aeb46bded37ad0e8edcd5d63cfaaa9573021cba0dd64cbc147dc` |
| standalone bridge Cargo.lock | `ecbe699177bf440aafdef144db1b3f1808a7382c6f008057381345157960f5d2` |

XcodeGen 2.44.1 resolves to `21ac9944b0ab546a07422dbed86f33dd2ebd76f8`.
App Ghostty is `683d8db643b95cf229bfb5fe9fab9ae677920343`; ZMX's independent Ghostty pin
is listed in the build recipe above. Local tests use macOS arm64 and Windows x64 -> Ubuntu
WSL -> SSH. CI executes Linux x64/arm64 helper tests and macOS x64 bridge tests, and checks
the macOS arm64 compilation target. The Intel macOS runner avoids GitHub's reported ARM
capacity cancellations; real ARM terminal acceptance remains on the supplied Mac.

Reproduction commands (supply explicit test-only selectors and paths):

```powershell
dotnet test tests/Agwinterm.Pty.Tests/Agwinterm.Pty.Tests.csproj -c Release --filter 'FullyQualifiedName~Agterm|FullyQualifiedName~Wsl|FullyQualifiedName~ZmxCompatibility|FullyQualifiedName~RestoreState|FullyQualifiedName~SessionStartFence|FullyQualifiedName~CreationTickets'
./tests/integration/wsl-agterm-remote.ps1 -Distro DISTRO -LinuxUser USER -Destination HOST -AgentSocket AGENT -OriginSocket FIXTURE_SOCKET -Bridge BRIDGE -TerminalFixture FIXTURE -FaultBridge FAULT_BRIDGE
./tests/integration/wsl-zmx-survival.ps1 -Distro DISTRO -BuildDirectory PINNED_WSL_BUILD -ProfileLive -LifecycleControls
./tests/integration/sandbox-ui.ps1 -Test RestoreRoundtrip -Background
./tests/integration/sandbox-ui.ps1 -Suite Recent -Background
./tests/integration/sandbox-ui.ps1 -Test Win32Control -Background
openspec validate add-agterm-remote-attach --strict
```

Run the remote matrix once with a single-pane origin (omit FaultBridge), then with a split.
Wrong Rust tickets are refused without replacing the current stream. Both viewer sizes
preserve origin geometry, styled wide cells, cursor, paste/keyboard modes and one query reply.
Hidden primary history returns exactly once after alternate-screen reconnect. The native
origin runner verifies managed exit emits input-mode resets without RIS or screen clearing.

Limitations: only the explicitly provisioned matching ZMX pair is supported; installed stock
agterm/ZMX is not silently upgraded. Authoritative styled reads are unsupported. Input ACK
means queued, not application consumption; uncertain delivery is never retried. Remote
presentation and inbound WSL export belong to later roadmap stages. No real-peer stage-2
acceptance item is substituted by a host-free fixture.

Crash recovery checkpoints the Rust reservation ticket and attachment nonce before the
host can create SSH, including before the managed-role handshake. A failed checkpoint
creates no child; a restart cancels only an unadoptable exact reservation. The routed fault
test kills the UI while the SSH bridge deliberately delays its first role notice and verifies
recovery. The origin runner additionally replaces a test daemon's executable file atomically
while its old image continues running and verifies that runtime build attestation refuses it.
