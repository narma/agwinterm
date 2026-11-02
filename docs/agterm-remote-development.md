# Outbound agterm discovery development

Updated 2026-11-02. Stage 2 implementation has started; terminal interoperability is not delivered. OpenSpec: `add-agterm-remote-attach`, 2/22 tasks complete (2.3 and 2.4).

## Implemented slice

`AgtermSshDiscoveryClient` executes read-only discovery through explicitly resolved WSL distribution/user and an SSH destination, optionally using an existing agent socket. It uses a non-PTY connection, batch authentication, strict host-key checking, no key updates, no forwarding and no local SSH command. It appends the standard CLI locations to the origin PATH through `/bin/sh`; the account-shell boundary escapes apostrophes and backslashes. No shell profile launch command, helper upload, dependency installation or workload creation occurs.

Handshake timeout is 5 seconds, overall timeout is 20 seconds. Stdout is limited to 4 MiB, stderr to 64 KiB (both byte limits). Concurrent reads avoid pipe saturation. Timeout, cancellation, invalid UTF-8 or an oversized pipe fails the request and stops only its local process tree. There is no automatic retry. A nonzero process exit rejects even success-shaped stdout; stderr is not surfaced as trusted terminal text.

`AgtermRemoteDiscovery` decodes `ok/result/remote`, validates origin executable/socket paths and bounds the inventory to 4096 sessions and JSON depth 32. It rejects duplicate properties, IDs, daemon claims, roles, conflicting window/workspace mappings, missing left panes, invalid split axes, truncated responses and absent endpoints. Split panes are ordered by role; `vertical` and `horizontal` retain upstream wire meaning. Required session fields are id/name, windowID/windowName, workspaceID/workspaceName, cwd and panes. splitAxis is required for two panes and absent/null for one. Optional parent/content determine eligibility; other additive metadata is ignored. Page and terminal companions remain visible but are unsupported for import. A valid empty inventory is success. Origin metadata is not daemon-incarnation proof and never enables attachment.

Task 2.2 remains pending: discovery argv and quoting are implemented, but the bridge/terminal command builders and executable account-shell fixtures are not yet complete. Task 2.1 remains pending: this slice has an explicit transport target, not persisted remote binding/recovery support.

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

## Continuation

Implement the native origin bridge and connection-preserving existing-FD client capability, including macOS identity observations and replacement-race tests. Prepare a reviewable isolated deployment/rollback before provisioning the host. Use Rust binaries; do not introduce Python or another installed runtime. The user's host/address and agent-socket information authorize the read-only checks above, not changing their live-mode settings, restarting agterm or installing components.

An isolated live origin session and compatible bridge/client are required before attach acceptance. Do not enable stock create-on-miss `zmx attach` as a shortcut. Keep tasks 1.1–1.4 and terminal import/recovery/acceptance pending until their full behavior is verified. Run future UI acceptance only through private-desktop WSL/background-Sandbox routing. Stages 3 and 4 remain required follow-up scope.
