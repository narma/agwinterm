# WSL origin export development

Updated 2026-11-02. Stage 3, change [export-wsl-sessions-to-agterm](../openspec/changes/export-wsl-sessions-to-agterm/proposal.md). Inbound terminal export is implemented behind explicit endpoint provisioning. Pinned actual-client acceptance and regression evidence are recorded below; no SSH service is installed automatically.

## Implemented foundation

`WslOriginExport` projects a captured origin topology into the agterm `ok/result/remote` envelope. It reuses the stage-2 wire records and decoder. Input consists of an explicit distribution/user/origin/executable/socket scope, per-pane saved bindings and separate native observation evidence. The pure projection performs no process launch, installation or lifecycle mutation itself.

Eligibility requires an owned, valid, non-pending local WSL binding, current attached/non-closing pane, matching pane ID and recovery state, and an independent ready observation of the exact binding and process/socket identity. Endpoint IDs are per binding; namespace comparison deliberately uses actual endpoint fields. One ineligible side omits the whole split. Canonical left/right ordering follows the current topology, including pane swaps. Duplicate identities, inconsistent window/workspace metadata and malformed topology refuse the projection. Output is capped at 4 MiB and input at 4096 sessions. No presentation capability is advertised.

The executable supplied to the projection must be a separate Linux adapter path, not the raw ZMX executable. The local projection checks its shape; the Linux relay publishes the actual immutable attachment executable after native verification. `WslOriginDiscovery` obtains fresh observations and compares a second UI snapshot before publishing; checkpoint metadata is not evidence of current liveness.

## Local origin control

The internal raw control command `zmx.export.tree` requires an exact open `window` ID, no pane target, and all six string arguments: `distribution`, `user`, `origin`, `executable`, `socketDirectory`, `adapter`. Unknown/duplicate fields, missing selectors, active/prefix window selectors and an origin different from the application's ID are refused. The supplied adapter path is not a provisioning operation or a claim that the path is installed/compatible.

The window captures ordinary session topology (including hidden split roles), then verifies eligible bindings through the existing exact native screen operation off the UI thread. It refuses legacy helpers instead of implicitly installing one. Probes have a shared 20-second cancellation budget; a failed pane removes its whole session. A second capture must match before publication; concurrent close, replacement, window movement or metadata/layout changes refuse the request. This is a snapshot, not an attachment authorization: the native attachment shim must recheck exact identity later.

The response includes `result.remote` plus private `result.bindings` identity evidence for the native relay. The relay strips this private evidence before returning the agterm envelope. It requests each exact open window and checks duplicate session/daemon identity and the open-window set before publishing. Neither this command nor the pure projection enables global inbound readiness flags. The CLI spelling is `agwintermctl zmx export tree --window ID --distribution DISTRO --user USER --origin APP_ID --executable ZMX --socket-directory SOCKET_DIR --adapter ADAPTER --json`.

## Native endpoint and setup

The shipped `agwinterm-hook` contains the adapter; no additional runtime is installed. Explicit provisioning copies it as a private `agtermctl` executable. Discovery publishes a content-addressed `manifests/HASH/manifest.json` and a hard-linked `attach` executable beside it. A persisted viewer endpoint therefore retains its original incarnation authority across rediscovery. A name-to-incarnation registry also prevents the name-only agterm screen command from following a reused name.

Attachment accepts `attach NAME` and the exact create-error guard emitted by pinned agterm. It never executes that guard or an arbitrary trailing command. Native code verifies the manifest scope, Linux UID, process/workload/socket identity, dependency capability evidence and then passes the same connected socket to the existing-FD client. Inherited ZMX selection variables are cleared. Managed nonce/claim values retain explicit leadership semantics. Viewer disconnect closes only its transport.

All adapter state is account-private on a Linux filesystem; symlinked files/paths, foreign owners, public file modes and untrusted writable ancestors refuse. JSON authority rejects duplicate fields, malformed/oversized input and conflicting immutable records. Root-owned sticky temporary directories are accepted for isolated tests. Each finite relay is bounded, and the adapter command has an overall deadline. No Rust PTY host pipe is exposed.

Provision from the selected WSL distribution/user with an existing compatible ZMX and an existing Windows CLI/control endpoint. Prepare a private parent directory (`0700`), choose a **new** child path, then send one newline-terminated JSON object to:

```sh
/absolute/agwinterm-hook --zmx-export-provision /private/parent/endpoint < request.json
```

The required JSON fields are `distribution`, `user`, `origin` (the Agwinterm app ID), `executable` (Linux ZMX), `socketDirectory`, `cli` (the absolute Linux-mounted path of agwintermctl.exe), and `pipe` (the normal application control pipe). Provisioning validates UID/distribution/machine identity, capability evidence and a read-only ping. Failure removes only files in the newly created endpoint directory; existing directories are never overwritten. WSL interop must be available to the authenticated SSH account; unavailable interop/control fails explicitly.

Make this endpoint's `agtermctl` available on the remote account's noninteractive PATH through an explicit user-selected configuration. Do not overwrite a different CLI. The test fixture gives only its own SSH invocations a private PATH wrapper; it does not edit the user's shell or SSH configuration. An SSH server, routing, account keys and host trust must already be configured independently.

Supported remote commands are `agtermctl zmx tree --json` and `zmx screen NAME --json [--all] [--lines N]`. Input and resizing use the existing terminal stream. Other commands, lifecycle mutation and `zmx present` refuse. Tree/screen require the Windows UI; existing streams and immutable exact attachment remain available during UI restart. Presentation is omitted from discovery and shown as unsupported by agterm.

Disable new discovery/attachment with `agwinterm-hook --zmx-export-disable /private/parent/endpoint`. Existing terminal streams and workloads remain alive. Re-enable through a newly provisioned endpoint and explicit PATH selection. Retain old manifests while viewers still refer to them; no background pruning or SSH reconfiguration occurs.

## Verification

The new host-free tests cover independent binding IDs, scope isolation, ownership, pending/unavailable/closing states, daemon/workload/boot/namespace/socket replacement, single and both split axes, swaps, partial splits, duplicate identities, endpoint paths, bounded output and roundtrips through the stage-2 decoder. Additional tests cover fresh observations, cancellation without retry, concurrent close/replacement/rename/window movement, equivalent fresh snapshots and exact multi-window dispatch.

```powershell
dotnet test tests/Agwinterm.Pty.Tests/Agwinterm.Pty.Tests.csproj -c Release --filter FullyQualifiedName~WslOriginExport --no-restore
openspec validate export-wsl-sessions-to-agterm --strict
```

These tests launch no UI and are permitted directly. Future Windows UI tests must use `sandbox-ui.ps1 -Background`; WSL acceptance must retain the `wsl-ui.ps1` private-desktop routing.

The final combined Agterm/Wsl/ZmxCompatibility/RestoreState/SessionStartFence/CreationTickets/CtlArguments selection passed 344 tests. Native hook tests passed 26 cases (one subprocess worker is intentionally ignored as a standalone test); the provisioning protocol suite passed four cases. Linux x64/arm64 static builds and the Windows Release build passed. The private-desktop routing harness passed 18 checks. Live acceptance is recorded separately below.

## Runtime acceptance

The [task list](../openspec/changes/export-wsl-sessions-to-agterm/tasks.md) tracks runtime acceptance and regressions. Native manifests, existing-only attachment, restricted relay and explicit provisioning are implemented. No global SSH-ready flag or automatic setup is enabled.

Read-only connectivity preflight used the user-supplied Mac and existing Ubuntu SSH agent. WSL-to-Mac authentication succeeded (Darwin). WSL already has sshd on port 2222. The reverse Mac-to-WSL check also succeeded (Linux) using the user-supplied `wsl` SSH alias and macOS SSH agent. No host keys, authorized keys, SSH services, installed applications or live sessions were changed.

The upstream RemoteTree.swift/RemoteSession.swift sources were refreshed on 2026-11-02; runtime acceptance is pinned to agterm `9133258994a6cafb330ee354df949b76b1b2834b`. The actual isolated app's tree reports commit `9133258`. The first real-peer matrix passed single/split discovery, input/read, explicit leadership, viewer close, Windows UI loss/restart, viewer crash/restart with explicit re-import, partial split omission and explicit owner termination. agterm intentionally does not restore remote rows automatically; the test verifies explicit re-import reaches the same workload identities.

Final expanded real-peer evidence: `artifacts/wsl-ui/20261102T211712-f77101b2/test.log` and `result.json` (exit 0; private non-input desktop), WSL `/tmp/aw-stage3-origin-aAqGHq4H`, Mac `/tmp/aw-stage3-viewer-aAqGHq4H`. The fixture retains `build-hashes.json` and single/split tree captures. This run also passed root-account refusal, follower resize preserving an independent leader's 91x31 grid, actual SSH transport loss and follower reconnect to the same workload, and explicit viewer leadership. The independent leader remains attached during this reconnect check: pinned agterm can take an unowned role when no leader remains. Stale snapshot/namespace replacement and distribution/origin isolation are covered deterministically by model/native tests; no second distribution is installed for testing.

| Accepted binary | SHA-256 |
| --- | --- |
| Linux x64 hook/adapter | `d69d79a28bfcdde935911ff316761e52341c7b34cab093d65c994caf0a445b0e` |
| WSL ZMX | `e87dd9bd723414b45e6b41a5cea0c3ce64165664f73c621e07a2fa7a311bc9f9` |
| agterm | `a52371604324d6beb65d5b0a38f0e9ea43f322b8e59579997be25f9d119f69b0` |
| agtermctl | `1a53da96f5ebc989ccedf952fb17ce013c22fd906cde90445075d32727bcc3a3` |
| Mac bundled ZMX | `00f2bb6e2c4a4dc479cff737d38d60975a3b42da2fc6468f5a4375dfca8711c4` |

Stage-2 regression passed in `artifacts/wsl-ui/20261102T211909-d05e009f`: borrowed input/read, exact-ticket UI restart, Rust-host loss, six separate SSH outages, close/Recent reopen, metadata clear, explicit leadership/detach, immediate close races and absent-origin refusal. Local WSL regression passed in `artifacts/wsl-ui/20261102T211921-4eef431e`: UI/host survival, recovery/retry, exact lifecycle controls, create/close races, multi-window restore, profile fallback and split/Recent behavior. Both recorded exit 0 on private non-input desktops. Distribution shutdown remains outside this regression: that destructive check requires a disposable distribution.

Ordinary Windows `RestoreRoundtrip` passed all eight cells in background Windows Sandbox, exit 0: `artifacts/sandbox/20261102T212226-f31af1e3/results`. This covers graceful/killed restore, rejected context/capture mutations, opt-in command replay, quit-time command capture, split geometry and swapped pane IDs. Strict OpenSpec validation and `git diff --check` passed. No stage-3 PR has been opened; remote CI and Greptile review are not claimed, and the change is not archived.

Reproduce with `tests/integration/wsl-origin-export.ps1 -BuildDirectory PINNED_WSL_BUILD -Destination MAC -AgentSocket WSL_AGENT -MacAgentSocket MAC_AGENT -MacApp PINNED_APP -PeerHost WSL_ALIAS`. The entry point routes every Windows UI process to the private non-input desktop. Omit peer parameters for the local real-daemon matrix. Host-free `export-protocol-tests.py HELPER` covers explicit provisioning, no overwrite, rollback, disable, malformed/duplicate/oversized requests, symlink refusal, unavailable UI and unsupported commands; CI runs it on both Linux architectures.
