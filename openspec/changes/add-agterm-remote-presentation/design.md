# Design

## Context

See [proposal.md](proposal.md) for motivation. Implementation on top of stages 1–3 is in progress following user approval. Current implementation and scoped evidence are recorded in [development notes](../../../docs/agterm-presentation-development.md); the full stage-4 acceptance remains pending.

Observed local seams:

- `AgtermRemoteDiscovery.cs` parses the tree and left/right daemon topology at terminal pin `9133258`, but does not retain a presentation capability or stable origin pane mapping. `Program.Remote.cs` reports `presentation = false` and already fences borrowed bindings/reconnect.
- `native/agwinterm-hook/src/zmx/export.rs` publishes `presentation: false`, relays bounded tree/screen requests and refuses other commands. Its private endpoint scope and immutable attachment manifests must remain authoritative; do not broaden the existing relay into a generic control proxy.
- `IPresentationHost` currently handles appearance/configuration, not a streaming presentation protocol. Add a dedicated broker boundary instead of treating that interface as an existing remote hub.
- `SessionHuds`, `NativeQuestion`, `Program.OverlayControls.cs`, `Program.WebOverlays.cs` and `WebOverlayCommand` provide local surfaces and validation. Native asks allow multiple fields; upstream asks encode one input or choice form, so their schemas are not interchangeable.
- `add-webview2-cookbook-overlays` has 32/36 tasks complete. Tasks 1.4, 1.5, 3.3 and 6.3 retain manual IME and clean/offline release evidence gaps. Earlier user decisions allow other work to proceed; this proposal does not mark those tasks complete or turn them into a prerequisite for starting passive presentation.

### Refreshed upstream source baseline

Inspected on 2026-11-02 at agterm [`c63e5158e637662ded7ba3a12eac4881b24caef1`](https://github.com/umputun/agterm/tree/c63e5158e637662ded7ba3a12eac4881b24caef1), the v0.40.0 release preparation commit. The accepted real-peer terminal baseline remains `9133258994a6cafb330ee354df949b76b1b2834b`; neither a source refresh nor the release name proves runtime interoperability.

| Source at the research pin | Observed contract |
| --- | --- |
| [PresentationFrames.swift](https://github.com/umputun/agterm/blob/c63e5158e637662ded7ba3a12eac4881b24caef1/agtermCore/Sources/agtermCore/PresentationFrames.swift) | JSON lines, protocol v1, `kind/gen/rev`, 256 KiB frame cap and 256 queued frames; pane UUID or scratch; snapshots and live events; ask owner, job ID and single-use page token. |
| [PresentationHub.swift](https://github.com/umputun/agterm/blob/c63e5158e637662ded7ba3a12eac4881b24caef1/agtermCore/Sources/agtermCore/PresentationHub.swift) | Intersection of advertised kinds, snapshot-before-held-deltas, `ask.form`, `ask.other`, `overlay.points`, page-kind group and presenter dispatch. Basic ask/overlay messages are role protocol, not all entries of `supportedKinds`; do not invent independent upstream feature flags. |
| [PresenterGrant.swift](https://github.com/umputun/agterm/blob/c63e5158e637662ded7ba3a12eac4881b24caef1/agtermCore/Sources/agtermCore/PresenterGrant.swift) | First acquire wins, no preemption, grant released with connection and owner generation changes. This generation is distinct from frame ordering. |
| [RemotePresentationClient.swift](https://github.com/umputun/agterm/blob/c63e5158e637662ded7ba3a12eac4881b24caef1/agtermCore/Sources/agtermCore/RemotePresentationClient.swift) | No stream for missing version; first hello fixes generation; revisions must increase; ping/ack, 30-second silence detection, reconnect fencing and separate snapshot status effects. |
| [RemoteSession.swift](https://github.com/umputun/agterm/blob/c63e5158e637662ded7ba3a12eac4881b24caef1/agtermCore/Sources/agtermCore/RemoteSession.swift) | `zmx present SESSION` and `session overlay page-serve TOKEN` use non-PTY SSH; `session overlay run-job JOB` uses a PTY. |
| [RemoteOverlays.swift](https://github.com/umputun/agterm/blob/c63e5158e637662ded7ba3a12eac4881b24caef1/agtermCore/Sources/agtermCore/RemoteOverlays.swift) | Origin reserves slots with presenter owner; page grant has a claim deadline; job/page identity and result are not local viewer identities. |
| [RemotePageChannel.swift](https://github.com/umputun/agterm/blob/c63e5158e637662ded7ba3a12eac4881b24caef1/agterm/Control/RemotePageChannel.swift) | Separate bounded resource/bridge slots, streaming chunks, cancel terminal frames, document-revision checks and complete waiter cleanup on channel loss. |

These links define the wire research baseline. Stage-4 fixtures must record complete actual JSON encodings, limits and peer hashes before implementation acceptance, especially Swift enum encoding for page sources. Do not upgrade the existing ZMX dependency merely to match an application release.

## Goals / Non-Goals

**Goals:** a shared protocol/state model with separate viewer and origin adapters, native surface reuse, explicit compatibility by family and outcome authority that survives races without replaying side effects.

**Non-Goals:** changing terminal ownership/leadership, replacing Rust PTY hosting, adding an installed daemon/runtime, reproducing all agterm CLI options, importing arbitrary companion groups, or turning a presentation token into general SSH/control authority. Page overlays on already attached sessions are in scope; unrelated page-only session discovery is not required.

## Decisions

### 1. Three channels with a common scoped binding

Use a long-lived non-PTY presentation stream per imported session, separate PTY streams for origin program jobs, and non-PTY page channels for resources/bridge calls. Separate cancellation and bounded queues prevent an unresponsive browser or presentation parser from disrupting terminal traffic. Reusing terminal escape sequences was rejected because it cannot negotiate grants or carry authoritative outcomes safely.

Outbound: Windows viewer -> existing selected WSL user/distribution -> SSH -> origin agtermctl. Program-job surfaces continue through the Rust PTY host. Inbound: agterm -> SSH -> provisioned Rust WSL adapter -> Windows application presentation broker through an explicitly scoped normal control endpoint. Extend the bundled CLI with a stream relay if necessary; no Python, extra service or exposure of the Rust host pipe. The Linux adapter verifies endpoint account/origin/session scope on subscription and each job/page claim.

Keep transport launch generation, protocol connection generation, presenter owner generation, origin application epoch and page document/command revisions separate. Bind all interaction IDs to endpoint plus session, not a process-wide string map.

### 2. Negotiation and a truthful feature matrix

Discovery retains presentation version only when the endpoint can actually serve it. Hello validates version and intersects known kinds; protocol v1 is the initial implemented version. Preserve upstream role-message behavior and advertise extension kinds only when every corresponding handler is available. Unknown additive kinds advance ordering without producing effects. Do not infer support from version text or terminal attachment.

| Family | Required stage-4 success path in both directions | Explicit refusal/degradation |
| --- | --- | --- |
| Status/context/HUD | Snapshot/clear/update, stable pane mapping, local HUD rendering and remaining lifetime | Unknown pane, unsupported rendering option or malformed payload; never retarget to active pane |
| Notifications | Live delivery once per accepted revision, no relay loop or reconnect replay | No delivery guarantee across outage; snapshot does not recreate alerts |
| Questions | Buttons, one text input or single/multi-choice including supported other/cancel roles, validated origin result | Multi-field Agwinterm forms remain local with a reason; unsupported ask extensions are not advertised |
| Program overlays | Session or mapped-pane job, origin command/cwd/env, PTY input/resize, close and wait/result | Expired/reused job, unavailable presenter, unsupported geometry/visibility operation; no local execution fallback |
| Web overlays | File plus sibling assets under grant, URL page, supported navigation/JS/bridge, hide/show/resize/reload, submission/cancel/result | Missing runtime; options outside negotiated contract; path/bridge scope violations; no generic command relay |

Status sound is implemented only with negotiated `sound`, using viewer policy and no snapshot replay. `layout` is consumed for identity/topology validation on existing attached sessions; it does not authorize arbitrary new terminal creation. Sidebar `info`, respawn extensions, scratch presentation and group/companion import remain explicitly unadvertised until separately supported; their omission must not hide the five required families. Unsupported shapes within a family are documented limitations, never a way to check off an entirely unimplemented family.

### 3. Exact mapping and state recovery

Extend binding/discovery to retain stable origin pane IDs from current upstream identity/layout contracts alongside daemon incarnation and role. Obtain authoritative mapping before applying pane-scoped presentation; a role or array index alone is insufficient. Inbound projection publishes the same stable Agwinterm pane identity consistently across discovery and presentation. A swap only changes role. A missing/mismatched incarnation retires that mapping and its interactions without following a reused name.

Pure model code owns decode/validation, snapshot replacement, revision fencing and effect selection. UI integration applies only validated effects to mapped sessions. Use distinct snapshot and transition paths to prevent duplicate status sounds/notifications. Mirrored effects carry provenance and cannot feed the origin broker's publication path. Bound frames to upstream's 256 KiB, queues to at most 256 frames, handshake/read/write waits and retry backoff; a healthy stream has no overall lifetime timeout. Ping every 10 seconds with a 30-second silence cutoff matches the observed client expectations.

Reconnect uses the saved endpoint and verifies incarnation before new hello/snapshot. Do not persist ephemeral presenter grants or page tokens. On UI restart, restore terminal bindings under their existing rules and negotiate a new presentation subscription; never restore stale dialogs by replaying saved frames.

### 4. Origin broker owns interactions and arbitration

Add a broker for committed local status/context/HUD/events and delegated interactions. It snapshots coherently and holds concurrent deltas until the snapshot is sent. All inbound answers and claims are checked against endpoint, session, current subscriber, owner generation and interaction lifetime. Acquire uses first-holder-wins; reconnect may acquire an available grant only after a new negotiation and never takes terminal leadership.

Reuse native local ask state via an adapter that maps the representable single-field subset and preserves IDs/types. Do not reinterpret a multi-field form as multiple independent questions. Origin validates selection IDs, text limits and button/cancel semantics. First completion wins. A lost answer acknowledgement produces uncertainty, not resend; use exact result retrieval where supported. On presenter loss, dismiss the replica and return pending questions to the origin, preserving any already accepted result.

Approved on 2026-11-02: delegated single-choice forms follow agterm semantics in both directions: select the first item when no initial item is specified, and make nonblank Other text mutually exclusive with an ordinary selection. Apply this policy only to delegated forms. Untargeted and locally presented native forms retain empty-selection and existing Other behavior. This is an explicit semantic conversion, not a new public form parameter.

Approved on 2026-11-02: extend `ask open` with optional `--target` naming an exact session ID in the selected exact window. Existing calls without target retain window-owned local behavior and are never assigned to the currently active session for export. Targeted questions retain their window plus immutable session identity in the origin registry; close/removal and result/cancel validation follow that owner. Only representable questions explicitly targeted at an eligible exported session can be delegated. Do not reinterpret the existing `ask result/cancel` target (the question ID). Add parser, ownership and legacy-call regressions with this API extension.

Program jobs are reserved at the origin and single-claim, with bounded claim deadlines. The viewer receives only job identity and presentation metadata, not executable text. On Agwinterm origin, the bundled Rust helper runs the authorized WSL job in its original context after a claim; it never starts a replacement base shell. Refusal before claim releases the reservation without execution. After claim, presentation-only loss revokes the presenter grant but leaves the running job on its existing scoped PTY channel. Close an already-ended held surface; mark a running replica orphaned and close its surface when the job channel ends. A new presentation connection cannot adopt or replay that job. Job-channel loss and explicit close follow the original job cancellation/result contract; neither can kill the underlying ZMX workload. Unclaimed reservations are retired on presenter loss. No automatic job retry. This distinction was approved by the user on 2026-11-02 and matches the pinned agterm behavior.

### 5. Reuse WebView2 with remote resources, not local-path substitution

Adapt the existing local resource-provider seam to read from the page channel. Keep per-page bounded resource streams, separate bridge concurrency, cancellation and backpressure; a cancelled request retains its slot until a terminal response or connection teardown. Exact wire/frame/resource caps come from the pinned PageServe codec and are recorded in fixtures before enabling pages.

Origin grants are single-use, expiring and tied to the current presenter/page. Validate decoded paths and canonical containment, including symlink/reparse escapes, at the origin. File source identity is display data; the viewer must not read that path from its own disk. Scope temporary viewer storage by endpoint/session/page; persistent URL storage requires the existing explicit option and a documented origin partition, never default sharing between unrelated remote endpoints.

Relay bridge requests only to the originating page's original grant and command allowlist. Remote delegation does not upgrade URL pages to trusted local pages, expand review grants, or accept caller target overrides. Document revision and ordered page command sequence fence callbacks and state acknowledgements. Origin retains first submission/cancellation; close after submission keeps the result. Presentation or page-channel loss closes the replica, invalidates token and pending calls, and settles the original page without implicit reopening. Missing WebView2 is a clean page refusal.

### 6. Acceptance is incremental but completion is not passive-only

User clarification (2026-11-03): remote general file-page bridges are restricted to their originating page/session. Permit own-page close, submit, hide/show/toggle, resize, reload and navigation, plus originating-session text/output reads, type/write input and status. Review grants retain only submit/close. Reject caller-provided target/window/pane/targets routing, batch, application settings, creation/destruction of sessions and every command outside this explicit allowlist. Resolve the original session and stable pane at dispatch time; stale document/page/presenter callbacks cannot mutate a replacement slot. Local trusted-page behavior remains unchanged. URL pages and subframes receive no bridge authority. Apply this boundary both before viewer forwarding and at an Agwinterm origin, so a remote viewer cannot bypass it by constructing raw PageServe bridge frames.

Implement passive streams first, then presenter/questions, program jobs and pages. Each milestone remains inside this change unless later split into explicit linked changes; it does not shrink the roadmap. For every family run success/refusal/loss/reconnect on Agwinterm-viewer/agterm-origin and agterm-viewer/Agwinterm-WSL-origin, recording actual binaries and origin outcomes. Two-viewer grant races and wrong endpoint/user/window/session/pane tests are mandatory.

Keep the old accepted terminal peer build as a regression baseline and build an isolated stage-4 peer at the new research pin. Existing SSH accounts/services may be used only within authorized setup; do not upgrade installed apps or change live sessions. Host-free model tests run directly. WSL UI acceptance goes through `wsl-ui.ps1` on the private non-input desktop; ordinary Windows UI regressions use `sandbox-ui.ps1 ... -Background`. Missing real peer or manual browser evidence stays pending, never replaced with fixture claims.

After user-granted macOS Accessibility permission, the optional acceptance driver may address only the isolated viewer's verified PID and app path. It uses AX controls/native input and validates retained results at the origin, refusing locked sessions and ambiguous targets. This is test infrastructure built with the existing Swift SDK, not a shipped dependency or a protocol extension. The pinned peer's custom choice rows require native keyboard navigation; exposed AXPress alone does not toggle them. Record this limitation with the evidence.

## Risks / Trade-offs

- Upstream version/kind negotiation does not independently flag every basic interaction -> preserve observed role protocol, use explicit per-request refusals and prove behavior with real peers before advertising presenter readiness.
- Local forms and geometry differ -> define representable subsets before publishing, retain origin fallback and test boundaries rather than silently truncate fields/options.
- Page channels carry files and commands -> preserve page grants, canonical roots, token secrecy, bounded buffering and stale-document rejection at both boundaries.
- Origin UI is the interaction authority -> UI loss ends presentation while terminal streams survive; no durable queue or replay of interactive side effects.
- Local WebView2 checks remain unfinished -> carry named pending evidence into the stage-4 report; proceed with authorized independent work, but do not claim full browser release acceptance without that evidence or a recorded user scope decision.
- Source pin advanced beyond accepted binaries -> new isolated real-peer evidence is a gate; no automatic ZMX update and no implied compatibility with all future peers.

## Migration Plan

1. Preserve existing terminal contracts and explicit endpoint provisioning. Add presentation support opt-in to capable endpoints; older endpoints continue terminal-only.
2. Implement/model-test negotiation and passive behavior before advertising interactive support. Enable each interaction family only after its local handlers and scoped channels pass acceptance.
3. Run the bidirectional real-peer matrix, local regressions, strict OpenSpec and CI; inspect Greptile on the final PR commit before declaring readiness.
4. Disabling presentation revokes grants and settles its child interactions while preserving base terminals and saved bindings. Rollback does not change SSH configuration, terminate ZMX workloads or replay jobs/pages. Old viewers see unsupported capability.
5. Update the roadmap/evidence and archive only after all required families and acceptance tasks pass.
