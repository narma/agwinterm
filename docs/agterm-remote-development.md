# Outbound agterm attachment development

Updated 2026-11-02. OpenSpec: `add-agterm-remote-attach`. Implementation and real-peer acceptance are in progress; see the stage-2 evidence below and the task ledger for remaining coverage.

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
tested application is agterm 0.38.1 (`09727610`), copied into an isolated directory with
the patched ZMX. This is distinct from the patch-source revision above. macOS signing can
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
- Windows: 177 targeted remote/SSH/WSL/identity/restore unit tests passed; Release build passed.
- macOS: existing-FD attach, missing socket/no-create, exact process identity, authoritative
  read/input, borrowed detach and socket/name replacement refusal passed.
- Private Windows desktop against real agterm: single and split import, per-pane input/read,
  exact-ticket UI restart, forced Rust-host loss, follower reconnect, borrowed close/Recent,
  explicit leadership and detach passed.
- Local WSL profile/lifecycle regression passed, including split/swap, two-window restore,
  owned close and fresh Recent semantics. Ordinary Windows RestoreRoundtrip passed in
  background Windows Sandbox.

Final terminal continuity, expanded fault/race coverage, final hashes and PR checks are
still being verified; this section does not yet declare stage 2 accepted.


The final signed test ZMX pair has SHA-256 `bee4afb0bd75bdd06f21fb2f73e5031e2ccbddb43b1e0b50bb367a6985602345`. The native bridge will be hashed again after final changes.

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
