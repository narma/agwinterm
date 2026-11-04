# WSL ZMX and bidirectional agterm interoperability

Updated: 2026-11-04. Status: stages 1–3 merged and archived, with accepted real-peer terminal evidence. Stage 2 main specs reconciled with stage 3. Stage 4 implementation and acceptance are complete (27/27 tasks); transport, scoped broker/adapter, passive events/sound, question conversion/outcomes and origin-run program overlays are implemented, with actual-peer evidence in both directions. Remote page grants, scoped resources, WebView2 adaptation and restricted bridge dispatch are implemented, with real file/URL acceptance in both directions; revision-aware page bridges, cached/same-document browser history and the integrated bidirectional matrix have passed. Real Chinese IME acceptance and all four clean/offline cells passed on the focus-fixed packages. Implementation commit 1ec505e passed all 12 CI jobs and clean Greptile review (5/5); documentation follow-up heads are checked before PR readiness.

## Committed outcome

The user explicitly requested completing **all four stages** below. Stage 1 is not the end goal. Stages 2–4 are committed follow-up scope, deferred from the first OpenSpec change rather than dropped or optional. This roadmap is the durable cross-change record; continuing work does not require the original chat.

ZMX on the Agwinterm side runs only inside WSL. Preserve the Rust PTY host as the Windows transport. The complete outcome is local live-session recovery, terminal attachment in both directions between Agwinterm and agterm, and supported remote presentation in both directions. A resumed agent conversation after reboot is not a surviving process.

Implementation of stage 1 was explicitly requested on 2026-11-02, including the isolated dependency download and design corrections. Stages 2–4 retain their separate change boundaries. No services or SSH configuration were installed, and the user's live sessions were not manipulated.

## Stage tracker

| Stage | Deliverable | Status | OpenSpec |
| --- | --- | --- | --- |
| 1 | WSL ZMX lifecycle and recovery with Rust PTY host | 29/29 tasks complete; local acceptance and CI passed; disabled by default; [PR #23](https://github.com/narma/agwinterm/pull/23) merged; [evidence](../wsl-zmx-development.md) | [add-wsl-zmx-session-recovery](../../openspec/changes/archive/2026-11-02-add-wsl-zmx-session-recovery/proposal.md) |
| 2 | Agwinterm viewer attaches to agterm origin | 22/22 tasks complete; pinned real macOS origin single/split, recovery, faults and terminal continuity accepted; [PR #25](https://github.com/narma/agwinterm/pull/25) squash-merged as `41cc0d9`; final-head checks passed and Greptile 5/5; synced and archived; [evidence](../agterm-remote-development.md) | [add-agterm-remote-attach](../../openspec/changes/archive/2026-11-02-add-agterm-remote-attach/proposal.md) |
| 3 | agterm viewer attaches to Agwinterm's WSL origin | 9/9 tasks complete; native adapter/provisioning, pinned actual Mac single/split/reconnect/restarts and stage-2/local WSL/ordinary Windows regressions passed; [PR #26](https://github.com/narma/agwinterm/pull/26) squash-merged as `35e9024`; CI passed and Greptile 5/5; archived; [evidence](../wsl-origin-export-development.md) | [export-wsl-sessions-to-agterm](../../openspec/changes/archive/2026-11-02-export-wsl-sessions-to-agterm/proposal.md) |
| 4 | Remote presentation in both directions | Implementation and acceptance complete; 27/27 tasks; notification/sound routing and question conversion/outcome checks passed; question acceptance passed in both directions; origin-run job acceptance passed in both directions; automated Mac Accessibility question/program UI checks passed; page integration and scoped actual-peer acceptance passed; revision-aware bridge and integrated matrices passed; real IME acceptance passed; corrected package repeat and implementation CI/Greptile passed; all five families are required | [add-agterm-remote-presentation](../../openspec/changes/archive/2026-11-04-add-agterm-remote-presentation/proposal.md) · [design](../../openspec/changes/archive/2026-11-04-add-agterm-remote-presentation/design.md) · [tasks](../../openspec/changes/archive/2026-11-04-add-agterm-remote-presentation/tasks.md) · [evidence](../agterm-presentation-development.md) |

Stage 4 has a CLI-created OpenSpec change with proposal, design, remote-presentation and wsl-origin-export delta specs, and 27 tracked tasks. The approved job-loss policy follows agterm: a claimed job survives presentation-only loss on its existing PTY channel, with no adoption/replay. Explicit `ask open --target` session ownership is approved and implemented; untargeted questions retain their previous window ownership. A stage may need several focused changes; track all of them here rather than shrinking the stage to match one implementation.

## 1. Local WSL live sessions and recovery

Scope: profile opt-in, dependency/capability checks, separate UI/Windows/Linux identities, owned versus attached lifecycle, detach versus terminate, managed leadership, authoritative text/input and recovery without duplicate command replay. Adopt a surviving Rust transport first; otherwise attach a verified live daemon. Explicitly relaunch after confirmed workload loss.

Completion: the current change's acceptance passes, including routed Rust/WSL survival tests, partial split recovery, uncertain-operation handling and ordinary-session regressions. Save evidence and an archive link in this tracker. Basic local ZMX operation does not complete stages 2–4.

Next after acceptance: propose stage 2 using the shipped endpoint and recovery contracts.

## 2. Agwinterm viewer -> agterm origin

Scope: SSH execution from WSL, discovery through the origin's `agtermctl zmx tree --json`, origin executable/socket endpoints, existing-only daemon attachment, split import, leadership, ownership and reconnect. Preserve origin identities separately from local pane IDs. Never create a replacement remote shell when the selected session disappears.

Completion: actual agterm-to-Agwinterm terminal attachment is demonstrated with compatible pinned builds; input, authoritative reads, split panes, different client sizes, disconnect/reconnect and local viewer close behave correctly. Closing the viewer does not kill the origin workload. Record unavailable real-peer verification as pending rather than replacing it with fixture-only claims.

Next after acceptance: propose stage 3, reusing the verified remote contract and fixtures.

## 3. agterm viewer -> Agwinterm/WSL origin

Scope: an SSH-accessible WSL endpoint and an agterm-compatible discovery/control adapter. Publish Linux executable/socket paths, eligible per-pane daemon identities and supported capabilities. Bridge origin metadata/control to Agwinterm without exposing the Rust host pipe as a remote API. Specify behavior when the Windows UI is unavailable and how endpoints are provisioned; do not assume a running SSH server.

Completion: an actual agterm client discovers and attaches to an Agwinterm-owned WSL workload, including splits and leadership. Viewer disconnect/close preserves the workload; origin-owned termination remains explicit. Validate endpoint/user isolation and both applications' restart behavior. Retain stage 2 regression evidence.

Next after acceptance: propose stage 4 against both verified terminal directions.

## 4. Bidirectional remote presentation

Scope: a separately negotiated `zmx present`-compatible channel with version/capability checks, generation/revision handling and reconnect behavior. Cover status, context, HUD and notifications first, then questions and program/web overlays with their additional job/page channels. Preserve origin routing, presenter ownership, answers, cancellation and outcomes. Reuse the local web surface; terminal output alone is not remote presentation.

Completion: every selected presentation kind has an explicit supported/unsupported contract and real-peer evidence in both directions. The stage is not complete merely because status colors work: questions and the planned program/web overlays need acceptance too. Older peers fail or degrade explicitly without losing terminal access, duplicating notifications or executing stale responses. Any narrower scope requires an explicit user decision recorded here.

Remote clipboard, general SSH administration, full agterm CLI parity and unrelated cookbook features are not implicitly included in these four stages. Add them only through an explicit scope decision.

## Continuation procedure

For a new chat or after finishing any change:

1. Read this roadmap and the linked current OpenSpec; inspect actual implementation/verification status rather than trusting planning-file completion.
2. Before archiving a stage, update its row with accepted scope, evidence, remaining limitations and the resulting archive path. Keep incomplete stages pending. Fix links when a change moves into the archive.
3. Name the next unfinished stage in the completion report and link this roadmap. Completing stage 1 must not be reported as completing the whole interoperability effort.
4. Draft the next stage's proposal/design/specs/tasks using current upstream and shipped local contracts. Replace its suggested name with an actual link. Do not reopen the already confirmed question of whether stages 2–4 are wanted.
5. Begin implementation only when requested; keep future planning separate from automatic deployment or service configuration.

Stage 4 is merged and archived; its requirements are synchronized to main specs. Real Chinese IME and all four clean/offline cells passed through the user-authorized Hyper-V worker. Stage 4 has 27/27 tasks complete. The approved page.bridge-revision extension is implemented in Agwinterm and a reproducible isolated agterm patch; unsafe legacy file bridges refuse explicitly. Stage 4 merged in PR #31 and archived on 2026-11-04.

Stage-4 source research was refreshed at agterm `c63e5158e637662ded7ba3a12eac4881b24caef1` (v0.40.0); this is not new real-peer acceptance. Preserve terminal regression evidence at `9133258994a6cafb330ee354df949b76b1b2834b`. The plan separates presentation streams, origin program-job PTYs and web resource/bridge channels, with sole-presenter grants and generation/revision fencing. Status/context/HUD, notifications, questions, program overlays and web overlays each require real-peer evidence in both directions; a passive-only milestone does not finish the stage.

Local WebView2 tasks 1.4, 1.5, 3.3 and 6.3 are complete in [add-webview2-cookbook-overlays](../../openspec/changes/add-webview2-cookbook-overlays/tasks.md). Real Chinese Pinyin and actual clean/offline package results are recorded in [the Hyper-V report](../agterm-stage4-hyperv-acceptance.md); stage-4 task 6.6 is awaiting the final corrected package repeat. No automatic SSH setup or new installed runtime is introduced. Stage-4 implementation was subsequently authorized; installed user applications remain unchanged.

## Relationship to older plans

This roadmap supersedes the ZMX exclusion in the October C0–C8 plan and the historical "Not chasing" ZMX entry **for the scope above only**. Full agterm parity is still not the goal. The [cookbook research plan](2026-10-31-agterm-cookbook-parity-research.md) retains its independent recipe audit; its remote-display follow-up is now tracked here. Stage 4 consumes the existing local web-overlay capability and its acceptance evidence, not an assumption that all browser release checks are complete.

Upstream research baseline and compatibility caveats are recorded in the [stage 1 design](../../openspec/changes/archive/2026-11-02-add-wsl-zmx-session-recovery/design.md). Refresh that evidence before proposing each remote stage; a pinned historical fixture is not a claim about the latest upstream contract.
