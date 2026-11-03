# Tasks

Apply started on 2026-11-02; 26/27 tasks complete. Implementation resumed after user approval of the job-loss policy below and explicit session targeting for questions. See [development notes](../../../docs/agterm-presentation-development.md) for implementation and scoped acceptance evidence. Follow [design.md](design.md) and the two delta specs. Source research pin: `c63e5158e637662ded7ba3a12eac4881b24caef1`; prior real terminal acceptance pin: `9133258994a6cafb330ee354df949b76b1b2834b`.

### Apply finding: distinguish presentation loss from job-channel loss

The pinned [AppStore+RemotePresentation.swift](https://github.com/umputun/agterm/blob/c63e5158e637662ded7ba3a12eac4881b24caef1/agtermCore/Sources/agtermCore/AppStore%2BRemotePresentation.swift) calls `orphanReplicaOverlays` when presentation leaves connected. That function closes an already-ended held surface, but marks a running job orphaned and keeps its program until its separate SSH ends. `replicaOverlayHeld` then closes the orphaned surface. No later presentation stream adopts it.

User approved the upstream behavior on 2026-11-02. Presentation loss revokes the grant, retires unclaimed reservations, closes ended held surfaces, and leaves an already-claimed job on its existing scoped PTY channel until that channel ends or explicit close; no adoption or replay on reconnect. Job-channel loss and explicit close retain the origin job cancellation/result policy and never affect the base terminal. Design decision 4 and tasks 5.3/5.4 now distinguish these cases.

Read-only environment verification: the supplied macOS arm64 peer is reachable through the existing Ubuntu SSH agent; the prior isolated stage-2 build remains available. No installed application, live session, or old acceptance build was changed. Task 1.1 captured real new-pin envelopes; see tests/Agwinterm.Pty.Tests/Fixtures/Presentation/README.md. This is wire evidence, not bidirectional UI acceptance.

Accessibility follow-up (2026-11-03): after the user granted macOS permissions, routed run `artifacts/wsl-ui/20261103T183505-a1068d93` passed all seven actual Mac question UI cases plus session/right-pane program input and held-surface UI close, with authoritative Windows/WSL outcomes. Existing loss/restart/workload regressions also passed. This strengthens completed tasks 4.4 and 5.4; progress remains 17/27. See the development notes for hashes, AX evidence, unlocked-session prerequisite and the pinned choice-row AXPress limitation.

## 1. Pinned contracts and identity

- [x] 1.1 Capture actual hello/snapshot/delta, ask, job and page envelopes from an isolated peer at the research pin, including PageServe enum encoding and caps; add provenance fixtures and verify encode/decode against the real CLI rather than inferred JSON.
- [x] 1.2 Extend discovery/binding with presentation version and authoritative stable pane IDs; test absent/invalid fields, swapped roles, duplicate IDs, reused daemon names and endpoint collisions without changing terminal-only import.
- [x] 1.3 Add bounded v1 frame models and negotiated capability intersection; test duplicate fields, oversized/partial lines, unknown additive kinds, unsupported versions and unadvertised extensions, and document the supported/refused option matrix.

## 2. Independent transport and origin broker

- [x] 2.1 Add outbound non-PTY WSL SSH presentation transport with bounded handshake/write queues, ping/ack, silence detection and cancellation; test hostile selectors, stderr saturation, stalled readers and preservation of an active terminal stream.
- [x] 2.2 Add an application origin broker and scoped bundled-CLI stream relay; test coherent snapshot-before-delta ordering, subscriber teardown, wrong window/session/account and no generic command forwarding, and document the internal endpoint contract.
- [x] 2.3 Extend the provisioned Rust WSL adapter with exact `zmx present` dispatch and honest discovery readiness; test unprovisioned/revoked/foreign endpoints, unavailable Windows UI and immutable terminal attachment regression without installing services.
- [x] 2.4 Add pure reconnect/generation state handling and diagnostics; test old transport callbacks, stale revisions, no hello, failed snapshot, bounded backoff, closed-pane races and origin replacement, and document terminal-only degradation.

## 3. Passive presentation in both directions

- [x] 3.1 Connect validated status/context/HUD effects and origin publication to local state; test snapshot clears, pane swaps/removal, remaining HUD lifetime, stale expiry and unchanged terminal geometry, documenting supported rendering options.
- [x] 3.2 Add live notification and negotiated sound routing with mirrored provenance; test duplicate revisions, reconnect snapshots, multiple viewers and relay-loop suppression, and document loss semantics during outages.
- [x] 3.3 Run a routed real-peer passive matrix in both directions with single/split panes, both application restarts and presentation-only disconnect; record peer hashes, unchanged terminal workload identities and absence of duplicate alerts.

## 4. Presenter grants and questions

Approved delegated single-choice policy: initial first item and exclusive nonblank Other, matching agterm; local questions retain their existing semantics. No new public form flags are introduced.

- [x] 4.1 Implement origin grant arbitration separately from terminal leadership and revoke it on stream loss; test two viewers, repeated acquire, old-owner answers and takeover after disconnect while terminal sizes remain unchanged.
- [x] 4.2 Add optional exact-session `ask open --target` with immutable window/session ownership and unchanged untargeted behavior; map native buttons and representable single-field input/choice/other/cancel forms in both directions; test legacy calls, wrong-window/session targets, owner removal, exact IDs/types, limits, malformed answers, busy modal slots and explicit multi-field refusal, and document the compatibility subset.
- [x] 4.3 Integrate question resolve/reject/dismiss with origin first-completion outcomes; test answer/cancel races, lost acknowledgement, owner close and handback without replay, including exact result retrieval or explicit uncertainty.
- [x] 4.4 Run real-peer question success, cancellation, form refusal and disconnect/late-answer cases in both directions; record authoritative origin results and two-viewer grant behavior.

## 5. Origin-run program overlays

- [x] 5.1 Add origin slot reservations, expiring single-claim job records and scoped `session overlay run-job` support in the bundled WSL adapter; test wrong owner/session/account, duplicate claim, timeout and refused-before-spawn with no base workload creation.
- [x] 5.2 Add Rust-hosted viewer job surfaces and origin execution/result plumbing; test command/cwd/environment staying on the origin, PTY input/resize, pane mapping, wait/exit/close and supported sizing, and document refused operations.
- [x] 5.3 Fence job callbacks and cleanup across presenter loss, failed claim and owner removal; test unclaimed reservation retirement, ended held-surface cleanup, running-job survival on presentation-only loss, orphan cleanup on job-channel exit, no adoption/replay, one settled result, released slots and surviving base ZMX terminals.
- [x] 5.4 Run real-peer job creation/input/resize/exit/wait/close and separate presentation-only loss, job-channel loss and explicit-close cases in both directions; prove the program ran only on the origin using markers not satisfiable by echoed input.

## 6. Remote web pages

In progress (2026-11-03): origin grants/slots, scoped resources, WebView2 adaptation and the approved page/session-only bridge are integrated; six page kinds are negotiated. Model checks and actual file/CSS/Unicode, URL, submission and channel-loss checks in both directions support completed tasks 6.1/6.2; see the development evidence for exact runs and incomplete regression cells. Remaining page tasks retain their full checks. The requested [manual IME/clean/offline scenario](../../../docs/agterm-stage4-manual-acceptance.md) is prepared for 6.6; preparing it does not complete acceptance.

- [x] 6.1 Add origin page grants and scoped `session overlay page-serve` handling with pinned resource/bridge framing; test single-use expiry, wrong presenter, queue limits, cancellation terminal frames and channel-loss waiter cleanup, documenting caps.
- [x] 6.2 Adapt local WebView2 resources to remote streams and origin-side containment checks; test sibling assets, Unicode paths, parent/symlink/reparse escapes, cross-page grants and origin paths never being read from viewer disk.
- [x] 6.3 Integrate URL/file page options, bridge allowlists and isolated storage; test review/general grant boundaries, URL/subframe denial, target overrides, cross-endpoint storage isolation and missing-runtime refusal, and document supported options.
- [x] 6.4 Add negotiated page.bridge-revision envelopes, document activation acknowledgements, legacy file-bridge refusal, ordered page commands/state and origin outcomes; test hide/show/resize/navigation/reload, stale document callbacks, submit/cancel/close races, token secrecy and no implicit page recreation after reconnect.
- [x] 6.5 Update the isolated test agterm with a reproducible revision-aware protocol patch and run real-peer file-assets and URL page matrices in both directions with allowed/refused bridge calls, submission/cancellation, channel loss and reload races; record origin outcomes and viewer cleanup.
- [x] 6.6 Reconcile relevant local WebView2 evidence with tasks 1.4, 1.5, 3.3 and 6.3 of add-webview2-cookbook-overlays; attach real IME and clean/offline installer/portable results and keep this acceptance item pending while required evidence is missing. The user authorized automated Hyper-V acceptance and Chinese IME installation in disposable test guests on 2026-11-03/04; synthetic Unicode input is not IME evidence.

## 7. Integrated acceptance and review

- [x] 7.1 Run the full five-family matrix against both actual directions with two viewers, old/partial peers, UI/transport failure, account/endpoint isolation and no stale responses; deliver a sanitized evidence report with exact builds and a supported/refused matrix, keeping any unavailable real-peer cell pending.
- [x] 7.2 Run stage-1 WSL recovery and stage-2/stage-3 terminal regressions through wsl-ui.ps1, plus ordinary Windows restore/control and local question/HUD/program/web regressions in background Windows Sandbox; verify no ownership, replay, input-routing or desktop-isolation regressions.
- [ ] 7.3 Complete Release/native/model checks, strict OpenSpec validation and whitespace checks; verify CI and inspect Greptile feedback for the final PR commit, resolve valid findings and record a clean final review before declaring readiness.

## Workflow follow-up

- Implementation is authorized through openspec-apply-change; remaining checkboxes retain their full acceptance scope.
- Target a PR in narma/agwinterm against narma; use Squash if merge is later requested.
- Update the roadmap with accepted scope, limitations and evidence; sync specs and archive only after all required tasks pass. Do not report a passive-only milestone as completed stage 4.
