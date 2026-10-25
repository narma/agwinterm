# Control capabilities: staged adoption from agterm 0.38.1

Date: 2026-10-25. Status: **C0–C1 implemented and locally verified; C2–C8 not started**.

The objective is useful new capabilities for agwinterm, adapted to Windows, ConPTY,
native rendering and WSL. Full command, spelling, wire-format or behavior parity with
agterm is **not** the objective. This plan records the scope requested in the October
control-API comparison. The subsequent user instruction authorizes implementation of
every stage through OpenSpec, with verification, archiving and a commit after each stage.
Release and live installation remain outside that authorization.

Baseline: agterm **v0.38.1**, released 2026-10-23, compared by source inspection with
agwinterm working tree at `6089f0c`. This was not a runtime conformance test on macOS.
Recheck each capability against current code before implementing it. Existing local
changes and unrelated OpenSpec work are not part of this plan.

## Sources and decision precedence

- [agterm v0.38.1 CLI sources](https://github.com/umputun/agterm/tree/v0.38.1/agtermCore/Sources/agtermctlKit)
  and releases [0.38.1](https://github.com/umputun/agterm/releases/tag/v0.38.1),
  [0.38.0](https://github.com/umputun/agterm/releases/tag/v0.38.0),
  [0.37.0](https://github.com/umputun/agterm/releases/tag/v0.37.0).
- [Product boundaries](../../CONTRIBUTING.md), [original native design](../superpowers/specs/2026-06-30-agwinterm-design.md),
  [recorded agterm differences](../agterm-parity.md), [P5 overlay contract](completed/2026-09-07-p5-pane-overlays.md).
- Current contracts: [control API](../control-api.md), [session commands](../session-commands.md),
  [WSL](../wsl.md), [HUD](../session-hud.md), [picker](../native-picker.md),
  [navigation](../navigation.md), [quick terminal](../quick-terminal.md).
- [Menu omissions](2026-09-15-menu-bar.md) document the current lack of terminal zoom
  and a hooks.conf executor. These are not evidence that either is impossible forever.

The September parity tracker and batch plan remain historical delivery evidence, not
an exhaustive backlog against 0.38.1. Their statement that the control API has no open
items applies to their older reference version. This plan defines the October adoption
scope; it does not overwrite shipped contracts or reopen completed P1–P17 work.

## Compatibility rules

1. Preserve stable pane IDs, caller/session/window routing and explicit refusals for
   unsupported auxiliary targets. Do not silently widen a pane operation to its session.
2. Keep `split off` destructive and swaps identity-preserving. Overlay hiding is a
   different operation and must not change split lifetime.
3. Keep the existing window-wide session-overlay `result` behavior. Pane overlays stay
   full-pane; new floating dimensions apply to the session-wide program overlay only.
4. Preserve PowerShell/direct launch contracts, existing `--wait`, restore commands,
   and integer row/column `--grow-*` units. Do not reinterpret existing scripts as Unix.
5. Resolve command execution and cwd in the target profile: Windows or the correct WSL
   distribution/user. Do not treat Linux paths as Windows paths or add runtime dependencies
   that the documented native WSL helper intentionally avoids.
6. Native UI follows Windows activation policy. API operations do not steal focus by
   default; explicit follow requests cannot promise OS foreground activation.
7. Existing plain HUD calls remain passive and retain their defaults. Formatting and
   placement are opt-in additions, not an implicit change to old messages.
8. New read-back fields are additive. Persistence extensions validate loaded values and
   retain existing restoration behavior. Result retention and event buffers are bounded.
9. Scope is agwinterm. Decide Lite applicability per stage; an agliteterm mirror is
   separate work. Do not expand the shared conformance promise before a coordinated mirror.

## Stages and dependencies

**C0 is implemented and locally verified** through OpenSpec `control-cli-reliability`.
[Acceptance evidence](../../openspec/changes/archive/2026-10-25-control-cli-reliability/verification.md) records
the Release build, tests and isolated Windows/WSL checks. This is implementation readiness,
not a release or live installation. C0 is archived, synced and committed as `5970e69`.
**C1 is implemented and locally verified** under `control-events-and-wait`.
[C1 acceptance evidence](../../openspec/changes/archive/2026-10-25-control-events-and-wait/verification.md)
records managed, native, Windows and WSL checks. **C2–C8 are not started**.
Every stage goes through its own OpenSpec proposal, design, specs and tasks, followed
by implementation, verification, sync/archive and a separate implementation commit.
Each stage may need several focused changes; it
is not a requirement to put a whole row into one PR. Dependencies are readiness gates,
not authorization to start work or a requirement to use parallel agents.

| Stage | Scope | Prerequisites |
| --- | --- | --- |
| C0 | Existing CLI correctness and explicit targeting | None |
| C1 | Event observation and status waiting | C0 |
| C2 | Program-overlay controls | C0, C1 |
| C3 | Native picker extensions and ask/forms | C0; C2 before recommended delivery |
| C4 | HUD lifetime, placement and native formatting | C1, C2 |
| C5 | Recently closed sessions and workspaces | C0 |
| C6 | Session placement, batching and sidebar organization | C0; C1 for event coverage |
| C7 | Window controls and Reveal | C0; C1 for event coverage |
| C8 | Pane appearance and diagnostics | C0; C4 for shared text-layout work where applicable |

Recommended delivery order: **C0 → C1 → C2 → C3 → C4 → C5 → C6 → C7 → C8**.
The C2-before-C3 order is prioritization, not a technical dependency of native dialogs.

### C0 — make existing automation outcomes reliable

Scope:
- Return failure exit codes for `ok:false` under `--json` in the ordinary CLI path.
  Preserve documented exceptions such as offline `version` diagnostics and picker outcomes.
- Reject unknown/unsupported options and accidental duplicate selectors rather than
  silently dropping them. Do not treat repeated targets as batches before C6 exists.
- Make `session focus`, `session resize` and named custom-command execution honor explicit
  session/window targets, including background sessions, with no implicit selection change.

Ready when: regression tests cover JSON refusal, malformed options, ambiguous/missing
targets and two distinct sessions/windows; a command cannot report success after acting
on the wrong target. Existing documented command modes and timeout uncertainty remain
accurately described. The current selection is unchanged unless the operation requires it.

### C1 — observe changes and wait for an agent

Scope:
- Add an opt-in continuous watch mode and kind filtering to `events`; preserve one-shot
  `--since` polling. Define run identity, reconnect cursor, buffer-overrun and app-restart
  behavior explicitly so a reconnect cannot silently claim a complete event history.
- Add events for session selection, pane focus, split/scratch changes, overlay and HUD
  lifecycle, including hiding/showing when C2 lands. Include enough IDs to resolve owners.
  Expose app launch/run identity to observers without promising replay across app restarts.
- Add `session wait` for one or more statuses, timeout and cancellation. Use atomic
  snapshot/subscription semantics or equivalent journal coverage so short-lived completed
  statuses are not lost between reading the tree and beginning the wait.

Ready when: tests cover already-matching status, rapid completed/auto-reset, timeout,
session removal, cancellation, reconnect, cursor gaps and restart. Waiting never blocks
the UI thread. Session-level status follows our documented pane aggregation; event payloads
identify a pane where relevant. Buffer bounds and exit codes are documented.

### C2 — program overlays without changing their existing contracts

Scope:
- `hide/show/toggle`, session-wide and pane-slot selection: keep the program and ID alive,
  stop intercepting user input while hidden, and expose effective visibility in `tree`.
- Independent floating `--width/--height` in Windows DIP, effective geometry read-back,
  and explicit return to full size. Keep percent sizing and full-pane pane overlays.
- `open --cwd/--follow/--background-color`, previously deferred from P5 rather than rejected.

Ready when: hidden running and exited overlays retain correct outcomes; show restores
the same overlay; explicit overlay-ID operations have documented behavior while hidden.
Close, swap, pane teardown, replacement and window teardown leave no orphan process or
input capture. Mixed percent/DIP axes, DPI changes, small windows and invalid dimensions
are covered. Windows and WSL cwd/launch paths are verified. Existing result/--block contracts
and the prohibition on floating pane overlays remain intact.

### C3 — native questions and structured answers

Scope:
- Extend `pick` with initial selected ID and item status/color/shape fields.
- Add native `ask open/result/cancel`, buttons and hotkeys, single/multiline text input,
  single/multiple choices, optional other text and blocking/nonblocking forms.
- Reuse picker ownership, bounded input, exact result IDs, cancellation and accessibility
  principles. A second terminal-rendered dialog style is not required for this stage.

Ready when: answer/cancel/escape/owner-close outcomes are distinct and stable; waiting
uses exact IDs after creation. Invalid forms do not open UI. Keyboard navigation works
on Cyrillic as well as Latin layouts; native controls expose accessible names and state.
Limits, retention, conflicting modal UI and transport-loss behavior are documented and
tested. Form labels and values are data, never executable commands.

### C4 — a more useful passive HUD

Scope:
- First increment: `--hide-after`, sticky/frameless placement, explicit font size and
  pane anchoring. Preserve old defaults, replacement and shared-slot behavior.
- Second increment: bounded multiline Markdown subset (headings, emphasis, lists, code,
  quotes/rules and bounded tables where supported) rendered with DirectWrite/Direct2D;
  file input for messages. Document the supported subset rather than claiming HTML parity.
- File arguments consumed by the Windows CLI keep the documented WSL conversion rule.
  Merely accepting a file does not introduce generated helper files or shell renderers.

Ready when: expiration cannot close a replacement HUD, timers end on teardown, pane anchors
follow the intended pane through swaps, and read-back reports effective placement/style.
Formatting has bounded layout work and handles Unicode, wrapping and small windows.
Keyboard, mouse, terminal dimensions/history and notifications remain unchanged. No HTML,
JavaScript or clickable HUD links. Plain-text calls retain their documented validation.

### C5 — reopen closed work through the CLI

Scope:
- First increment: `recent list/open/clear` over the existing closed-session restoration
  path, with stable entry IDs and explicit destination-window rules.
- Second increment: closed workspaces and bounded persisted history. This is additional
  implementation, not something implied by the current Reopen Closed UI.

Ready when: reopening preserves the supported split, profile/cwd, names, flags, context
and restore pins; Windows and WSL sessions are covered. Reopening cannot duplicate a live
identity. Partial workspace restoration does not lose un-restored entries. Closed/deleted
window behavior, history eviction, corruption and restart behavior are tested. Clearing
recent history never clears live sessions or changes `restore clear` semantics.

### C6 — organize working sets

Scope:
- `workspace flag`, including sessions hidden by filters, and `workspace new --collapsed`.
- `session new/move --before/--after` with unambiguous anchor/workspace rules.
- Explicit batch close/move with per-target outcomes. Decide and document partial-failure
  behavior before mutation; report which targets actually changed.
- Flagged layouts flat/plain/tree. Follow with multiple focused workspaces and a separate
  filter switch, migrating the existing single-focused-workspace state.

Ready when: hidden members are handled, ordering is deterministic, ambiguous anchors
refuse, repeated targets cannot produce accidental duplicate mutations, and partial errors
are truthful. Navigation still obeys visible order and empty-workspace semantics. State
restores with validated additive fields; disabling a filter does not discard its focus set.

### C7 — complete useful Windows control operations

Scope:
- Window next/previous, minimize/restore, creation minimized and CLI access to existing
  fullscreen. Explicit monitor selection for movement and actual geometry in replies.
- Reveal the session directory in Explorer using the existing Windows/WSL conversion path.

Ready when: background/minimized windows do not unexpectedly take focus; last-window and
quick-host rules hold. Geometry is checked across monitors, DPI and work-area bounds, with
effective values read back. Unknown monitor/window or unresolvable cwd fails clearly;
WSL Reveal resolves the correct distribution/user path. These commands reuse UI operations.

### C8 — presentation and diagnostics

Scope:
- Per-pane background overrides, solid colors and native text watermarks; status color/shape.
- Separate optional light/dark theme choices integrated with Windows appearance, preserving
  current single-theme behavior when not configured.
- Effective keymap read-back, config reload and installed-versus-running version diagnostics.
  Preserve the distinction between custom-command listing and the complete effective keymap.

Ready when: pane overrides follow IDs through swap/restore and have explicit inheritance
and clearing rules. Presentation does not change status priority or notification categories.
Theme changes update terminal and chrome consistently. Reload shares live config validation;
invalid entries have visible diagnostics. Installed-version reporting distinguishes missing
or unavailable metadata from a match and handles portable installations honestly.

## Explicit exclusions and deferrals

| Capability | Decision and reason |
| --- | --- |
| HTML/URL overlays, browser storage/navigation, JS bridge, page forms | Excluded under the existing native/no-WebView architecture. Reconsider only as an explicit architecture change. An external browser is not claimed as an equivalent embedded overlay. |
| zmx list/screen/prune/kill/reset/tree/attach/present, remote presentation and HTML, session lead, remote clipboard | Excluded: the maintained tracker explicitly says not chasing zmx live/remote; this is not a remote multiplexer project. Existing local pty-host work is not replaced. |
| Automatic foreground-program replay via session restart | Deferred outside this plan. Windows foreground-shell hints are not Unix foreground-process-group proof. A future explicit-command restart needs its own Windows/WSL lifecycle design; no guessing from hints. |
| In-app hooks.conf executor and hooks list/reload | Not in this adoption scope. Current hooks are installed agent integrations, and the menu deliberately omits hooks.conf. C1 supplies observation for external automation. This is a plan choice, not a permanent prohibition. |
| surface zoom | Not included just to match a verb; current product documentation records no terminal zoom. Existing window fullscreen/zoom is distinct and remains supported. |
| Reinterpreting split off, swap IDs, overlay result, restore clear, session restore none, launch --wait or grow units | Excluded: shipped contracts differ deliberately or are documented locally. Any future extension must be additive and separately specified. |
| Floating/resizable pane overlays | Excluded from C2; keep the documented full-pane constraint. New DIP sizing is for the session-wide floating slot. |
| Clickable links, HTML or JavaScript in HUD | Excluded: breaks passive native HUD input behavior. C3 provides an explicit interactive dialog instead. |
| Separate quick type/text, mandatory --pane-id alias, select-all/keymap-run spelling parity | Not required: use existing quick/pane IDs, selection all and the corrected custom-command path. Do not count aliases as new capabilities. |
| General clipboard set | Deferred: existing selection-copy and Windows/WSL clipboard workflows cover current local use; the upstream remote-presenter feature is out of scope. No need to widen the clipboard API solely for parity. |
| Ghostty terminfo installation, macOS entitlements/dictation/notarization, Metal-specific memory changes | Excluded: different platform/backend. Windows or WSL problems need evidence and a native solution. |
| Cookbook deployment, scheduled-job manager, automatic installation of third-party tools | Not part of terminal command adoption. No scheduler, plugin marketplace or agent-manager subsystem is introduced. |
| Mandatory simultaneous agliteterm parity | Excluded from this plan: shared subset changes and optional Lite mirrors require their own scoped work. Product-specific capabilities may remain agwinterm-only. |

## Definition of done and tracking

A stage is done only when its included increments are delivered, not merely when a CLI
spelling is accepted. If an increment is deferred, retain it explicitly as pending and
do not mark the whole stage complete.

- Record concrete source/contract changes, validation evidence and remaining limitations
  in the implementation change. Validate both CLI and raw-pipe paths where applicable.
- Add focused behavioral tests for target routing, refusals and lifecycle boundaries;
  use UI acceptance for rendering/focus/accessibility and actual WSL tests for Linux paths.
- Run required repository checks and review for the implementation scope. Desktop tests
  use isolated owned instances and the repository's current safety/cleanup procedure.
- Update `docs/control-api.md`, relevant feature docs, CLI help and `AgentSkill.cs` together.
  Update shared conformance only for a deliberately coordinated shared capability.
- Recheck old callers: defaults, IDs, focus, results, exit codes and persistence. Intended
  correctness fixes such as JSON failure exit codes need a clear compatibility note.
- No release, install, migration of the user's live configuration or implementation is
  performed by recording this plan. Such work begins only after a subsequent request.
