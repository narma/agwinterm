# Tasks

Apply started on 2026-11-02; 13/27 tasks complete. Implementation resumed after user approval of the job-loss policy below and explicit session targeting for questions. See [development notes](../../../docs/agterm-presentation-development.md) for implementation and scoped acceptance evidence. Follow [design.md](design.md) and the two delta specs. Source research pin: `c63e5158e637662ded7ba3a12eac4881b24caef1`; prior real terminal acceptance pin: `9133258994a6cafb330ee354df949b76b1b2834b`.

### Apply finding: distinguish presentation loss from job-channel loss

The pinned [AppStore+RemotePresentation.swift](https://github.com/umputun/agterm/blob/c63e5158e637662ded7ba3a12eac4881b24caef1/agtermCore/Sources/agtermCore/AppStore%2BRemotePresentation.swift) calls `orphanReplicaOverlays` when presentation leaves connected. That function closes an already-ended held surface, but marks a running job orphaned and keeps its program until its separate SSH ends. `replicaOverlayHeld` then closes the orphaned surface. No later presentation stream adopts it.

User approved the upstream behavior on 2026-11-02. Presentation loss revokes the grant, retires unclaimed reservations, closes ended held surfaces, and leaves an already-claimed job on its existing scoped PTY channel until that channel ends or explicit close; no adoption or replay on reconnect. Job-channel loss and explicit close retain the origin job cancellation/result policy and never affect the base terminal. Design decision 4 and tasks 5.3/5.4 now distinguish these cases.

Read-only environment verification: the supplied macOS arm64 peer is reachable through the existing Ubuntu SSH agent; the prior isolated stage-2 build remains available. No installed application, live session, or old acceptance build was changed. Task 1.1 captured real new-pin envelopes; see tests/Agwinterm.Pty.Tests/Fixtures/Presentation/README.md. This is wire evidence, not bidirectional UI acceptance.

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
- [ ] 3.3 Run a routed real-peer passive matrix in both directions with single/split panes, both application restarts and presentation-only disconnect; record peer hashes, unchanged terminal workload identities and absence of duplicate alerts.

## 4. Presenter grants and questions

Approved delegated single-choice policy: initial first item and exclusive nonblank Other, matching agterm; local questions retain their existing semantics. No new public form flags are introduced.

- [x] 4.1 Implement origin grant arbitration separately from terminal leadership and revoke it on stream loss; test two viewers, repeated acquire, old-owner answers and takeover after disconnect while terminal sizes remain unchanged.
- [x] 4.2 Add optional exact-session `ask open --target` with immutable window/session ownership and unchanged untargeted behavior; map native buttons and representable single-field input/choice/other/cancel forms in both directions; test legacy calls, wrong-window/session targets, owner removal, exact IDs/types, limits, malformed answers, busy modal slots and explicit multi-field refusal, and document the compatibility subset.
- [x] 4.3 Integrate question resolve/reject/dismiss with origin first-completion outcomes; test answer/cancel races, lost acknowledgement, owner close and handback without replay, including exact result retrieval or explicit uncertainty.
- [x] 4.4 Run real-peer question success, cancellation, form refusal and disconnect/late-answer cases in both directions; record authoritative origin results and two-viewer grant behavior.

## 5. Origin-run program overlays

- [ ] 5.1 Add origin slot reservations, expiring single-claim job records and scoped `session overlay run-job` support in the bundled WSL adapter; test wrong owner/session/account, duplicate claim, timeout and refused-before-spawn with no base workload creation.
- [ ] 5.2 Add Rust-hosted viewer job surfaces and origin execution/result plumbing; test command/cwd/environment staying on the origin, PTY input/resize, pane mapping, wait/exit/close and supported sizing, and document refused operations.
- [ ] 5.3 Fence job callbacks and cleanup across presenter loss, failed claim and owner removal; test unclaimed reservation retirement, ended held-surface cleanup, running-job survival on presentation-only loss, orphan cleanup on job-channel exit, no adoption/replay, one settled result, released slots and surviving base ZMX terminals.
- [ ] 5.4 Run real-peer job creation/input/resize/exit/wait/close and separate presentation-only loss, job-channel loss and explicit-close cases in both directions; prove the program ran only on the origin using markers not satisfiable by echoed input.

## 6. Remote web pages

- [ ] 6.1 Add origin page grants and scoped `session overlay page-serve` handling with pinned resource/bridge framing; test single-use expiry, wrong presenter, queue limits, cancellation terminal frames and channel-loss waiter cleanup, documenting caps.
- [ ] 6.2 Adapt local WebView2 resources to remote streams and origin-side containment checks; test sibling assets, Unicode paths, parent/symlink/reparse escapes, cross-page grants and origin paths never being read from viewer disk.
- [ ] 6.3 Integrate URL/file page options, bridge allowlists and isolated storage; test review/general grant boundaries, URL/subframe denial, target overrides, cross-endpoint storage isolation and missing-runtime refusal, and document supported options.
- [ ] 6.4 Add ordered page commands/state and origin outcomes; test hide/show/resize/navigation/reload, stale document callbacks, submit/cancel/close races, token secrecy and no implicit page recreation after reconnect.
- [ ] 6.5 Run real-peer file-assets and URL page matrices in both directions with allowed/refused bridge calls, submission/cancellation, channel loss and reload races; record origin outcomes and viewer cleanup.
- [ ] 6.6 Reconcile relevant local WebView2 evidence with tasks 1.4, 1.5, 3.3 and 6.3 of add-webview2-cookbook-overlays; attach manual IME and clean/offline installer/portable results when available and keep this acceptance item pending while required evidence is missing. Existing permission to proceed with other work remains in force.

## 7. Integrated acceptance and review

- [ ] 7.1 Run the full five-family matrix against both actual directions with two viewers, old/partial peers, UI/transport failure, account/endpoint isolation and no stale responses; deliver a sanitized evidence report with exact builds and a supported/refused matrix, keeping any unavailable real-peer cell pending.
- [ ] 7.2 Run stage-1 WSL recovery and stage-2/stage-3 terminal regressions through wsl-ui.ps1, plus ordinary Windows restore/control and local question/HUD/program/web regressions in background Windows Sandbox; verify no ownership, replay, input-routing or desktop-isolation regressions.
- [ ] 7.3 Complete Release/native/model checks, strict OpenSpec validation and whitespace checks; verify CI and inspect Greptile feedback for the final PR commit, resolve valid findings and record a clean final review before declaring readiness.

## Workflow follow-up

- Implementation is authorized through openspec-apply-change; remaining checkboxes retain their full acceptance scope.
- Target a PR in narma/agwinterm against narma; use Squash if merge is later requested.
- Update the roadmap with accepted scope, limitations and evidence; sync specs and archive only after all required tasks pass. Do not report a passive-only milestone as completed stage 4.
