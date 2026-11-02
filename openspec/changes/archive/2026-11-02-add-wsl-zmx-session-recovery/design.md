# Design

## Context

See [proposal.md](proposal.md) for motivation. This document is a proposed design, not evidence of implemented or runtime-tested compatibility.

Observed local boundaries:

- `src/Agwinterm.Pty/ServerSession.cs`: UI detach preserves a hosted process; dispose kills it. Startup reconciliation reaps unclaimed Windows transports using creation tickets.
- `src/Agwinterm.Win32/Program.Sessions.cs`: `CreatePane` attempts `TryAdopt` during restore, otherwise launches; host failure currently falls back to in-process. ZMX requests need a fail-closed branch without changing the ordinary path.
- `src/Agwinterm.Win32/Program.Services.cs`: `IsAdopted` suppresses buffer, agent-resume, pinned and captured-command replay. A new Windows transport attached to an existing Linux daemon would not satisfy this check today.
- `native/agwinterm-ptyhost/src/main.rs`: one data attachment per Windows session; attach supersedes the old client and restores terminal state. It is not the place to add multi-client sharing.
- `WslIntegration`, `ShellProfiles`, `RestoreState` and `RecentHistory` provide the existing launch, environment, checkpoint and closed-history boundaries. Recent deliberately restores settings, not live process state.

Upstream research baseline: agterm commit [`0716007052a6cedb8c43bb745ce6e5e7d3bc6d2b`](https://github.com/umputun/agterm/tree/0716007052a6cedb8c43bb745ce6e5e7d3bc6d2b), inspected on 2026-11-01. Its `scripts/setup.sh` pins ZMX `8bab1f0173b07e79835ea372d749af3dbf0d0842` (v0.8.1) plus patches. No Linux build or cross-product runtime proof was performed during planning.

- [Patch contract](https://github.com/umputun/agterm/blob/0716007052a6cedb8c43bb745ce6e5e7d3bc6d2b/scripts/zmx-patches/README.md): managed leadership, `screen`, acknowledged `type`, atomic socket publication and snapshot formatting.
- [RemoteSession.swift](https://github.com/umputun/agterm/blob/0716007052a6cedb8c43bb745ce6e5e7d3bc6d2b/agtermCore/Sources/agtermCore/RemoteSession.swift): remote discovery invokes `agtermctl zmx tree --json`; terminal attach uses the origin's executable/socket directory and SSH PTY, with a create-only failure guard.
- [ZmxSupport.swift](https://github.com/umputun/agterm/blob/0716007052a6cedb8c43bb745ce6e5e7d3bc6d2b/agtermCore/Sources/agtermCore/ZmxSupport.swift): expected daemon names are `agterm-` plus 32 lowercase hex digits; the origin publishes its socket directory. Shell integration restrictions there are agterm restrictions, not universal ZMX restrictions.
- [RemotePresentationClient.swift](https://github.com/umputun/agterm/blob/0716007052a6cedb8c43bb745ce6e5e7d3bc6d2b/agtermCore/Sources/agtermCore/RemotePresentationClient.swift): presentation is a separate versioned stream with generation/revision tracking and capability negotiation.

## Goals / Non-Goals

**Goals:** Keep Rust hosting and its protocol boundary; add Linux workload identity and recovery above it; make every launch/recovery decision observable; preserve room for inbound and outbound agterm adapters without coupling local pane IDs to upstream roles.

**Non-Goals:** Native Windows ZMX, migration of running plain shells, process survival across Windows reboot or WSL termination, SSH setup, remote discovery/service exposure, remote presentation/HTML/clipboard, replacing the Rust host, implementing agterm's entire CLI, or changing Recent/native-session contracts. This request writes planning artifacts only.

## Decisions

### 1. Layer ZMX behind the existing host

```text
agwinterm UI + session/recovery coordinator
    |
    v
Rust PTY host -- ConPTY -- wsl.exe -- ZMX client
                                      |
                                      v
                              ZMX daemon -- shell/agent
```

The Windows transport and Linux workload have independent lifetimes. Use the existing protobuf create/attach/detach/kill transport and host tickets; no wire revision is planned. Linux multi-client support belongs to ZMX. A future agterm viewer attaches there, not to the Rust host's single data pipe.

#### WSL distribution lifetime (runtime correction, 2026-11-02)

An isolated systemd-enabled Ubuntu fixture proves that losing the last Windows
WSL invocation can stop the entire distribution, even while a double-forked ZMX
daemon is alive. The kernel boot ID can remain unchanged while PID namespace and
init incarnation change. Existing user WSL sessions can mask this failure.
See `docs/wsl-zmx-development.md` for negative/positive controls. The user
authorized resolving this boundary and updating the design on 2026-11-02.

Add an explicit foreground WSL lifetime lease independent of the Rust PTY host
and UI. It has no terminal transport, does not replace either host, and never
creates, signals or relaunches workloads. Before readiness, pin and revalidate
the workload with Linux pidfds. Keep its Windows `wsl.exe` invocation alive
while those exact workloads live; exit when they all exit. Creation must acquire
the lease before publishing ready. UI quit, attachment detach and Rust orphan
cleanup do not release an owned workload's lease. Explicit workload termination
causes the lease to finish naturally. Reconciliation must validate lease health
and avoid duplicate unbounded leases. Lease failure is visible and never
authorizes workload recreation. Unexpected lease loss, explicit distribution
shutdown, Windows logout/reboot and machine shutdown remain workload-loss cases.

Do not install a service, schedule a task, alter `.wslconfig`, disable WSL idle
shutdown globally or rely on unrelated user terminals as a keeper. The bounded
helper implementation will own lease reconciliation along with its private
ledger. Verify in a disposable distribution with no unrelated foreground
clients, including a no-lease negative control and automatic lease exit.

The initial Linux helper implementation requires explicitly available Python
3.11+ and pidfds, validated by readiness. These resources live under `WslLive`,
separate from ordinary WSL shell/hook integration, which retains its native,
Python-free contract. Missing Python refuses live mode rather than adding a
fallback or changing ordinary profile behavior. No implicit interpreter install.

The first supported live configuration is WSL ordinary panes with `session-host=server-rust`; other host modes refuse an explicit live request until separately verified. Disabled profiles retain all existing host behavior. This avoids an untested matrix and protects the user's current backend. A direct Linux transport backend or native Windows ZMX would require replacing established host behavior and is rejected for this scope.

### 2. Explicit profile opt-in and verified Linux dependency

Add an optional profile `zmx` configuration containing enabled state and an absolute Linux executable path. Resolve and persist actual distribution/user at first creation, including when the profile relies on defaults. Run helpers with explicit selectors, argv-safe shell quoting, bounded output/deadlines and the recorded Linux cwd. Preserve arbitrary supported profile shell/command arguments; bash is a required WSL acceptance case, not a zsh-only port of agterm's shell integration.

Use an isolated socket directory on the Linux filesystem, scoped by app identity and Linux user. Never put Unix sockets on a Windows-mounted directory or derive endpoint selection from the active pane. Clear inherited `ZMX_SESSION`, `ZMX_SESSION_PREFIX` and stale managed-client tokens; set the intended directory and attachment token. Workload-origin AGWINTERM identity remains stable across client attachments; viewer identity must not overwrite it.

Require the pinned agterm-patched capability set: managed leadership, screen/cursor state, acknowledged input, safe publication/replay behavior. Record upstream revision and patch digest, probe actual capability behavior, and preserve build provenance in diagnostics. The dependency is explicitly provisioned by the user/developer for the first release; no package-manager invocation, silent download or replacement of existing ZMX. A reproducible WSL build/probe is the first implementation gate. If the baseline cannot satisfy the specs, leave the feature disabled and revise the design before enabling it.

### 3. Three identities and a small WSL lifecycle helper

Keep separate records:

| Identity | Contents | Authority |
| --- | --- | --- |
| UI binding | pane ID, window/session association, checkpoint generation | Layout and targeting |
| Windows transport | pane transport ID, host PID and creation ticket | Exact host operation |
| Linux workload | endpoint ID, distro/user, executable, socket directory, daemon name, incarnation and ownership | Live workload operations |

Add a versioned optional `LiveBinding` to pane checkpoints. The record includes provider, origin kind (`local-wsl` initially), opaque origin ID, daemon identity, ownership, readiness evidence and last recovery outcome. Persist stable binding data; attachment nonce and pending callbacks are generation-scoped, not authority inherited blindly from disk. Unknown/corrupt bindings retain a visible unavailable placeholder rather than falling through to ordinary command replay.

Use a bounded WSL lifecycle helper with a private per-endpoint operation ledger to serialize create/reconcile/terminate. It is a local control helper, not another terminal emulator or a replacement daemon host. Each creation gets a random operation ID and fresh daemon UUID. The ledger records Linux boot ID and verifiable daemon process/socket incarnation evidence before reporting ready. No mutation is authorized solely by a stored PID, a daemon-name prefix or second-resolution `created` timestamp.

Prefer exact process handles such as Linux pidfds where available for owned termination and verify socket/process association. A check-then-`zmx kill NAME` sequence alone does not provide incarnation-safe termination; never claim it does. If exact ownership/incarnation cannot be proved, refuse termination and retain the record. External attachments are never granted termination authority. A lost creation reply is reconciled through the same operation ID; it is not permission to submit another create. Helper restart and corrupted-ledger paths fail closed.

Choose `agterm-<independent-workload-uuid>` daemon names for the managed compatibility namespace, because the pinned agterm client validates that shape. The private directory and ledger determine ownership, not the prefix. UI pane IDs can change independently. Future agterm export can map stable origin pane roles to these names without renaming live processes.

### 4. A recovery state machine above transport adoption

| Evidence | Action | Command replay |
| --- | --- | --- |
| Matching Rust transport survives | Adopt existing attachment | None |
| Transport lost; exact daemon verified alive | Create only a transport; attach existing daemon | None |
| Daemon confirmed absent | Keep recoverable placeholder | None until explicit fresh start |
| Endpoint unreachable, identity uncertain or dependency incompatible | Preserve binding and show reason | None |
| Explicit fresh start after confirmed loss | Allocate new incarnation using selected source | Once, one source only |

Represent recovery outcome explicitly (`host-adopted`, `zmx-reattached`, `fresh-started`, `missing`, `unavailable`, `unsupported`, `uncertain`). Route buffer seeding and all agent/pin/capture replay decisions through it, instead of expanding the Windows-only `IsAdopted` shortcut. Preserve metadata even when replay is suppressed.

Attach-existing validates identity on a connected Unix socket, then passes that same descriptor through exec to the patched ZMX client's `ZMX_ATTACH_FD` path. This local seam skips `ensureSession` and refuses session switching; it can never create a replacement daemon or reconnect to a reused name. It preserves the upstream terminal client and wire ABI. Readiness requires its separate `existing-fd-attach` capability probe. A daemon identity mismatch becomes a stale binding. Generation checks cancel late output, discovery and readiness callbacks. Reconnect uses bounded attempts/backoff and one in-flight operation per binding; timeout does not imply absence. A close/detach wins over a pending retry.

Explicit fresh start offers a selected saved agent resume, pin, allowed captured command or plain profile shell. It does not execute several sources. Deliver creation commands through the one-time Linux creation path rather than a timer typing into whichever process appears later. Resume after reboot is a new process and is described as such.

### 5. Lifecycle policy preserves existing close expectations

| User/system action | Owned daemon | External daemon |
| --- | --- | --- |
| App quit/UI failure | Preserve; restore attachment later | Preserve; restore attachment later |
| Explicit attachment detach | Preserve; remove binding from automatic restore | Preserve; remove binding from automatic restore |
| Ordinary pane/split/session close | Terminate exact owned workload | Disconnect only |
| Explicit terminate | Revalidate and terminate exact owned workload | Refuse |
| Startup orphan sweep/restore clear/history clear | No daemon kill | No daemon kill |

Owner close retains the application's shipped destructive close semantics. UI labels/readback make the owned/shared consequence clear and provide detach; it does not silently change existing `session close` to persistence. A detached owned daemon remains in the endpoint ledger/inventory and can be reattached explicitly without losing ownership provenance. No automatic daemon prune is introduced.

Do not use upstream `zmx detach` if it detaches all clients. Stop only this attachment/Windows transport. Verify that transport termination and Rust-host failure do not propagate into the daemon; this is an acceptance gate, not an assumed WSL guarantee. ZMX survival requires the distribution to remain running.

Only proven terminated owned work enters Recent as closed local work; an explicit detach is not a closed-work entry. Recent reopening restores supported settings and pins, creates a new daemon UUID, and never restores its old live binding or executes foreground commands. Free UI IDs still follow the existing Recent rules. Failed/uncertain close retains a visible pending/error state rather than falsely reporting a completed kill.

### 6. Local controls and terminal coherence

Plan a dedicated `zmx` CLI group and matching control operations:

| Operation | Scope and result |
| --- | --- |
| `zmx list --profile NAME` | Bounded endpoint readiness/inventory; daemon, incarnation, ownership, attachments and capabilities; no workload launch |
| `zmx attach --profile NAME --daemon NAME --incarnation TOKEN [--window ID]` | Existing-only attachment; exact identity; returns pane and binding IDs |
| `zmx detach --target PANE [--window ID]` | Remove only that attachment and its auto-restore record |
| `zmx terminate --target PANE --force [--window ID]` | Explicit owned-workload termination; no foreign target or active fallback |
| `zmx retry --target PANE [--window ID]` | Retry live recovery only; never fresh-create |
| `zmx restart --target PANE --source shell\|agent\|pin\|captured [--window ID]` | Explicit fresh start of a confirmed missing workload; unavailable source refuses |
| `zmx lead --target PANE [--window ID]` | Explicit leadership claim; report actual role/uncertainty |

Keep these local verbs distinct from future agterm wire/CLI compatibility. Validate duplicate/unknown fields, exact targets and window boundaries using existing CLI conventions. Inventory tokens are revalidated, not irrevocable kill authority. Pane/tree readback adds an optional `liveSession` object with provider, endpoint, daemon, ownership, role and recovery state; ordinary pane fields retain meaning.

Use patched `zmx screen` for workload text/cursor queries and acknowledged `zmx type` for automated keystrokes. Preserve existing control-byte and readonly rules. Raw keyboard/paste follows the attachment's supported semantics; paste must retain bracketed-paste behavior rather than being incorrectly converted to keystrokes. Automated input and follower resizing never claim leadership. Consume role notices only with the current attachment nonce/generation and keep them out of user titles.

Rust host snapshots remain useful for UI adoption; a newly created transport receives only ZMX replay, not saved local buffer seeding as well. Verify history, alternate screen, Unicode/wide cells, colors, cursor, keyboard modes, terminal queries and follower resize with two differently sized clients. An unproven query result is unavailable, not an invented authoritative screen.

Implementation evidence on 2026-11-02 exposed two additional snapshot gaps:
the Rust/managed mode dump omitted the keyboard stack and hidden primary screen;
the pinned ZMX serializer omitted the hidden primary screen and erased the last
viewport of serialized scrollback before it reached history. Preserve the Rust
host and wire protocol: repair its existing VT mode snapshot, and carry the ZMX
repair as a reproducible local patch after the pinned agterm patches. Verify
both recovery paths with styled wide cells, saved cursor and exactly-once history
lines before accepting terminal fidelity. A successful authoritative text query
alone is insufficient evidence that the local display recovered its buffers.

### 7. Future bidirectional agterm compatibility

Reserve adapters around the endpoint and origin model; do not expose a service now.

| Later change | Reuses this change | Additional work deliberately deferred |
| --- | --- | --- |
| agwinterm viewer -> agterm origin | Managed client, authoritative reads/input, origin IDs and ownership | WSL SSH transport, `agtermctl zmx tree` decoding, origin endpoint resolution, remote reconnect and layout import |
| agterm viewer -> WSL origin | Managed daemon namespace, endpoint inventory, stable origin pane mapping | SSH-accessible WSL deployment and a compatible `agtermctl zmx tree --json` adapter; routes Linux CLI requests to the Windows owner without exposing the Rust pipe |
| Presentation in both directions | Capability/readiness and origin model | Versioned `zmx present`, status/context/HUD, questions/overlays, then page channels and clipboard policies |

The source tree endpoint contains Linux paths meaningful on the origin; Windows installation paths must never be exported as Linux executable/socket paths. Local pane IDs are opaque; an adapter handles agterm's primary/split roles and imported origin IDs. Future borrowed bindings remain attachment-only. Preserve the workload's originating control environment when another terminal becomes leader.

Pin representative tree/endpoint, managed-role, screen and unsupported-presentation fixtures to the research commit, with provenance. Test that the local data model can map both directions without dropping origin, endpoint or daemon identities. These are host-free model checks, not proof that two applications interoperate. Do not advertise a presentation protocol version until that protocol is implemented; terminal sharing alone does not replicate statuses or web overlays.

## Risks / Trade-offs

- [WSL may stop or propagate transport teardown unexpectedly] -> Routed runtime gate must demonstrate daemon/PID continuity; document WSL shutdown and reboot as workload loss.
- [Three terminal state layers: ZMX, ConPTY/host and UI] -> Establish authoritative reads and test replay/modes/size rather than assuming byte forwarding is transparent.
- [Pinned patched dependency maintenance] -> Record build/patch provenance, run capability checks, never silently upgrade running daemons; future updates need a separate migration policy.
- [Name/PID reuse and timeout races] -> Operation ledger, random IDs, verified incarnation and exact termination; refuse when evidence is insufficient.
- [Rust startup sweep removes old transports] -> It retains Windows-only authority; never infer Linux kill from unclaimed pane state.
- [No automatic resume after missing daemon] -> Intentional recovery stop prevents duplicate or unintended work; explicit fresh start preserves saved sources.
- [Old binaries ignore additive binding fields] -> Warn against opening live checkpoints with older builds; retain a backup and export ordinary settings when intentionally downgrading. Additive JSON is not a promise of safe downgrade.
- [Remote scope expands into a second product] -> Keep SSH/presentation in separate changes; no dependency on unfinished WebView2 release acceptance for local ZMX work.

## Migration Plan

1. Implement and verify behind disabled-by-default profile opt-in. Existing profiles/checkpoints remain unchanged when no binding is present.
2. Provision a pinned test executable explicitly in an isolated Linux directory; pass capability and Rust-host survival gates before enabling a user profile.
3. Back up checkpoints before the first live binding is saved. Add validated versioned binding fields without changing existing pane IDs or ordinary restore-source precedence.
4. Disabling the profile affects new panes only. Existing bindings remain recognized and recoverable; missing dependencies produce placeholders rather than ordinary replay.
5. Roll back by explicitly detaching or terminating owned work as chosen by the user, retaining the ledger and checkpoint backup. Do not delete daemons or downgrade live checkpoints automatically.

Acceptance requires routed WSL lifecycle tests and Windows host regressions. Use `wsl-ui.ps1` through normal local entry points for WSL UI, `sandbox-ui.ps1 -Suite <suite> -Background` or `-Test <test> -Background` for Windows UI, and direct execution only for host-free tests/fixtures. Never set CI bypass flags or move tests onto the user's desktop. Isolate app IDs, socket directories and test workloads; never stop the user's host or distribution for failure testing.
