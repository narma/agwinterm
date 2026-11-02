# WSL ZMX and bidirectional agterm interoperability

Updated: 2026-11-02. Status: stage 1 implementation and acceptance complete; reviewed and archived, PR #23 open with passing CI, merge deferred by user. Stage 2 planning complete; implementation not started.

## Committed outcome

The user explicitly requested completing **all four stages** below. Stage 1 is not the end goal. Stages 2–4 are committed follow-up scope, deferred from the first OpenSpec change rather than dropped or optional. This roadmap is the durable cross-change record; continuing work does not require the original chat.

ZMX on the Agwinterm side runs only inside WSL. Preserve the Rust PTY host as the Windows transport. The complete outcome is local live-session recovery, terminal attachment in both directions between Agwinterm and agterm, and supported remote presentation in both directions. A resumed agent conversation after reboot is not a surviving process.

Implementation of stage 1 was explicitly requested on 2026-11-02, including the isolated dependency download and design corrections. Stages 2–4 retain their separate change boundaries. No services or SSH configuration were installed, and the user's live sessions were not manipulated.

## Stage tracker

| Stage | Deliverable | Status | OpenSpec |
| --- | --- | --- | --- |
| 1 | WSL ZMX lifecycle and recovery with Rust PTY host | 29/29 tasks complete; local acceptance and CI passed; disabled by default; [PR #23](https://github.com/narma/agwinterm/pull/23) open, no merge requested; [evidence](../wsl-zmx-development.md) | [add-wsl-zmx-session-recovery](../../openspec/changes/archive/2026-11-02-add-wsl-zmx-session-recovery/proposal.md) |
| 2 | Agwinterm viewer attaches to agterm origin | Required; proposal/design/specs/tasks ready; 0/22 implementation tasks; real-peer verification pending | [add-agterm-remote-attach](../../openspec/changes/add-agterm-remote-attach/proposal.md) |
| 3 | agterm viewer attaches to Agwinterm's WSL origin | Required; proposal pending; follows stage 2 | Not created; suggested name `export-wsl-sessions-to-agterm` |
| 4 | Remote presentation in both directions | Required; proposal pending; follows stages 2 and 3 | Not created; suggested name `add-agterm-remote-presentation` |

Suggested names are reservations in this document only, not existing changes. Create each through the OpenSpec CLI when drafting its actual proposal. A stage may need several focused changes; track all of them here rather than shrinking the stage to match one implementation.

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

Current next action: review the stage-2 proposal on `codex/agterm-remote-attach`, which depends on the unmerged stage-1 branch. Do not merge PR #23 without a new user request. After a separately authorized stage-1 squash merge, rebase the stage-2 branch onto updated `narma`. Stage-2 implementation requires a new apply request; its real-peer capability gate needs an authorized agterm host and cannot be satisfied by fixtures alone.

## Relationship to older plans

This roadmap supersedes the ZMX exclusion in the October C0–C8 plan and the historical "Not chasing" ZMX entry **for the scope above only**. Full agterm parity is still not the goal. The [cookbook research plan](2026-10-31-agterm-cookbook-parity-research.md) retains its independent recipe audit; its remote-display follow-up is now tracked here. Stage 4 consumes the existing local web-overlay capability and its acceptance evidence, not an assumption that all browser release checks are complete.

Upstream research baseline and compatibility caveats are recorded in the [stage 1 design](../../openspec/changes/archive/2026-11-02-add-wsl-zmx-session-recovery/design.md). Refresh that evidence before proposing each remote stage; a pinned historical fixture is not a claim about the latest upstream contract.
