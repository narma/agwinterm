# agterm and cookbook parity research

Initial survey: 2026-10-31. Updated: 2026-11-01.

Status: the initial cookbook inventory is complete. Embedded web viewing and local
web-overlay parity are implemented in [PR #19](https://github.com/narma/agwinterm/pull/19),
with cookbook fixtures and validation evidence. Manual IME and clean/offline Windows
acceptance remain pending. Remote display/replication is deferred to a separate
change. The broader recipe audit and OSC 8 assessment remain open.

The goal is to identify which useful agterm workflows Agwinterm supports, which
need recipe adaptations, and which require new terminal capabilities. The intended
output is a prioritized backlog with evidence and acceptance criteria. Matching
command names alone does not establish workflow parity.

## Related plans and contracts

- The [October C0–C8 plan](2026-10-25-control-capabilities.md) documents the preceding
  implementation wave. All stages are marked implemented; its verification reports
  retain the limitations of the shared C6 UI checks. Cookbook recipes and scheduled
  job management were outside that wave.
- The [parity tracker](../agterm-parity.md) connects the October plan to earlier work.
  The [July roadmap](../agterm-roadmap.md) and gap analysis are historical references.
- Completed changes and verification reports are in the
  [OpenSpec archive](../../openspec/changes/archive/).
- The [web-overlay change](../../openspec/changes/add-webview2-cookbook-overlays/proposal.md),
  [cookbook coverage matrix](../cookbook-parity.md) and
  [release validation](../webview2-release-validation.md) describe the implemented
  web scope and remaining acceptance checks.

Historical statements must be checked against current contracts. For example, the
October plan and older tracker describe `split off` as destroying a pane, whereas
[current control API documentation](../control-api.md#session-split-and-session-swap)
and [installed agent guidance](../../src/Agwinterm.Pty/AgentSkill.cs) describe hiding
it while retaining its process and ID for split restoration. This is documentation
drift, not an open feature gap. This research preserves the historical documents.

## Pinned comparison baseline

| Source | Baseline |
| --- | --- |
| Agwinterm initial survey | Commit `02b843cb875b818156e5230af5e3b72775aeece8` |
| Previous comparison wave | agterm v0.38.1 against Agwinterm `6089f0c`, followed by C0–C8 implementation |
| Upstream reference | agterm master `714ec616c81add35627a1f8c3d5cb50ab9e030a1`, captured on 2026-10-31 |
| Cookbook inventory | [46 recipe directories at the pinned commit](https://github.com/umputun/agterm/tree/714ec616c81add35627a1f8c3d5cb50ab9e030a1/cookbook), [index](https://github.com/umputun/agterm/blob/714ec616c81add35627a1f8c3d5cb50ab9e030a1/cookbook/README.md) |

The initial survey read all 46 recipe READMEs and separately inspected scripts for
`native-dir-picker`, `project-launcher` and `flag-recent-agents`. It compared local
API, picker, working-set, recent-work, WSL, command-launch and foreground-hint
documentation, plus relevant control-server and generic status-installer code.

Subsequent web work inspected and adapted the four web-related recipe surfaces:
`html-doc-overlay`, `annotate-document`, `scheduled-jobs` and `session-snapshot`.
Their tested coverage is recorded separately from full recipe portability in the
[cookbook matrix](../cookbook-parity.md). The broader inventory is still a source
and contract survey, not runtime validation of every recipe. No overall parity
percentage is claimed.

## Findings

1. **Many workflows should first be evaluated as external recipes.** Native picker,
   ask/forms, program overlays, HUD, events/wait, working sets, recent history and
   window management already provide a substantial foundation. Verify arguments,
   responses and lifecycle behavior before adding commands.
2. **Claude/Codex resume already has a native integration path.**
   [SessionStart and session.bind](../control-api.md#sessionbind-from-a-sessionstart-hook)
   retain conversation and launch information, including [WSL](../wsl.md). Compare
   restoration of a specific pane with upstream recipes before adding overlapping
   shell wrappers.
3. **An available API does not make a shell script portable.** Check JSON envelopes,
   exit codes, `AGT_*`/`AGTERM_*` versus `AGWINTERM_*`, window and application-instance
   routing, exact pane IDs, Windows/WSL working directories and quoting.
   `native-dir-picker` splits roots on `:`, which cannot be used unchanged for
   Windows drive paths. `project-launcher` uses zsh and a temporary prompt file;
   Agwinterm's [session new](../session-commands.md) has its own launch modes.
4. **Recent history is not conversation replay.**
   [recent open](../recent-work.md) restores shells and settings, but does not replay
   agent bindings or captured programs. `park-and-resume` and `session-snapshot`
   still need a separate audit of snapshot durability and restoration.
5. **Embedded web viewing now has an implementation; remote attachment remains
   separate.** WebView2 supports local HTML, URL pages and explicit local bridges.
   An external browser is a fallback, not equivalent to embedded interaction.
   Likewise, ordinary SSH is not equivalent to agterm/zmx live remote attachment.
6. **Windows foreground hints do not establish prompt readiness.** This matters
   for `same-dir`, reset, agent-to-agent automation and busy/idle classification;
   see the [hint contract](../navigation.md#foreground-shell-hints).
7. **The pinned upstream is broader than v0.38.1.** For example,
   [named-companion](https://github.com/umputun/agterm/blob/714ec616c81add35627a1f8c3d5cb50ab9e030a1/cookbook/named-companion/README.md)
   depends on companions and row tints introduced in 0.39.0. Implemented shell-less
   web companions do not establish parity with that recipe's program duplication,
   replay and appearance requirements.

## Roadmap beyond C0–C8

### Embedded web viewing: implemented, with acceptance limits

The implementation uses a lazy, optional WebView2 host for local HTML documents,
URL pages, local dashboards and supported image formats. The terminal grid keeps
its native renderer. The implemented scope includes:

- Session-wide and independent split-pane pages, including hide/show, swaps,
  hidden splits, owner teardown and coexistence with program overlays.
- Origin-pinned URL navigation by default and explicit `--browse` for cross-origin
  browsing. JavaScript, persistent URL storage and local bridge grants are separate
  options.
- Explicit local resource-directory grants, isolated child frames, bounded review
  submit/result handling, trusted-local general/declarative commands and blocking
  page outcomes.
- Native identity/title and icon controls, keyboard traversal, DPI sizing, shared
  web zoom, theme updates that retain document state, and browser/explorer actions.
- Shell-less web companions, image pages and live-document retention during the
  three-second close/undo interval.
- Fixtures for the four web-related cookbook surfaces, with source provenance,
  documented platform differences and integration evidence.

The [change tasks](../../openspec/changes/add-webview2-cookbook-overlays/tasks.md)
and [release validation](../webview2-release-validation.md) are the detailed status
references. Synthetic DPI checks and UI Automation coverage are available; manual
IME verification and clean/offline Windows validation are still outstanding.
Simulated missing-runtime checks do not replace clean-machine acceptance. Build
and package evidence identifies the tested version and must not be treated as
proof that every installed copy contains subsequent review fixes.

The web surface does not imply full browser tabs/extensions or complete ports of
the four recipes. Scheduler installation, HTML publishing, and snapshot collection
and restoration remain outside this implementation. Remote display/replication
requires its own OpenSpec change; agterm/zmx interoperability is a candidate, and
the transport has not yet been selected.

### OSC 8 and terminal hyperlinks: assessment pending

The intended behavior is for terminal applications to associate an explicit URI
with visible link text, retain that association, and allow the address to be opened.
This is a terminal capability independent of an embedded browser.

- [ ] Audit the managed and Rust engines, cell attributes, scrollback/reflow,
  renderer, mouse hit testing and URI-opening path. The
  [July gap analysis](../agterm-gap-analysis-2026-07.md) recorded missing OSC 8 and
  URL detection, but is not evidence of the current build's behavior.
- [ ] Specify OSC 8 open/close handling, ID parameters and URIs, metadata size
  limits and lifetime across main and alternate screens.
- [ ] Define link retention during scrolling, wrapping, resize, overwrite,
  selection/copy and buffer persistence; compare managed and Rust behavior.
- [ ] Define hover/address feedback, activation gestures and allowed URI schemes,
  including external-browser and embedded-page destinations.
- [ ] Test Windows/ConPTY and WSL delivery separately. Automatic plain-URL detection
  and the implemented browser-link opening policy are adjacent features, not proof
  of OSC 8 support.

Acceptance: a precise gap statement for each backend/transport, a hyperlink
behavior contract and an end-to-end validation plan.

## Complete scenario inventory

This grouping sets audit priorities; it is not a claim that every recipe has passed
runtime tests. Each recipe needs its own row in the final evidence matrix. Names
refer to the pinned cookbook above. The web-related rows reflect subsequent work.

| Recipes | Questions to resolve | Audit priority / status |
| --- | --- | --- |
| `native-dir-picker`, `fzf-path-picker` | Return a path without Enter; correct pane, quoting and cancellation; native and WSL/TUI variants | First |
| `project-launcher`, `new-session-in-workspace` | Workspace selection/creation, cwd/profile, arbitrary prompts and ambiguous names | First |
| `project-switcher`, `workspace-sets` | Compare retained focus sets with a separate filter; preserve hidden sessions | First |
| `window-per-project`, `window-switcher` | Exact window selection, waiting sessions, minimize/restore without unexpected focus changes | First |
| `overlay-and-split` | TUI launch/cwd/PATH; process and pane-ID retention through hide/show, swap and restart | First |
| `claude-session-resume`, `codex-session-resume` | Existing bind/restore with two conversations, split and WSL; avoid duplicate integration | First |
| `session-context-nudge` | Existing context instructions versus task changes and companion renaming | First |
| `long-commands-status`, `flag-recent-agents` | Command lifecycle, status timestamps and multi-choice; avoid changing unrelated flags/windows | First |
| `backlog-picker`, `sqlite-browser` | Picker, overlay and exact pane; dependencies, cancellation, launch errors and data formats | Second |
| `claude-recap`, `claude-conversation-picker` | Transcript formats, conversation selection, summary cost/latency and correct relaunch | Second |
| `annotate-claude-replies`, `annotate-pane-output` | revdiff/TUI, capture, clipboard/paste and returning notes to the originating pane | Second |
| `park-and-resume`, `session-snapshot` | Durable snapshots versus bounded recent history; split, conversations, partial failures and replay | Second; snapshot viewer tested, collection/restoration not ported |
| `kimi-session-resume`, `opencode-session-resume`, `antigravity-session-resume` | Agent-specific hooks/formats and restoration; generic launch status is not conversation binding | Second |
| `copilot-agent-status`, `kimi-agent-status`, `kiro-agent-status`, `antigravity-agent-status` | Native lifecycle hooks versus generic process wrappers; blocked/permission/turn-completion states | Second |
| `container-agent-status` | Container-to-host routing and pane identity; explicit transport configuration | Second |
| `agent-reset`, `close-tab-when-done`, `two-agent-chat`, `claude-account-swap` | Input state, process identity, deletion/restart boundaries and races | Dedicated audit |
| `truthful-agent-lights` | Live child processes, workers, sweeper and competing status writers; Windows and WSL separately | Dedicated audit |
| `flagged-dashboard` | Exact pane membership, busy filter, hidden split, readback and grid size; dashboard already exists | Dedicated audit |
| `same-dir` | Reliable prompt readiness and shell-specific cd; foreground hints are insufficient | Dedicated audit |
| `named-companion` | Parent/child model, duplicate/replay, tint and refusal to copy another pane's arguments | Separate product assessment; web companions alone do not cover it |
| `claude-pasted-images` | Kitty graphics/XTVERSION and Claude modification; separate engine capability from CLI recipe | Dedicated audit |
| `remote-claude-session` | External SSH/tmux wrapper, reconnect and status bridge; distinct from zmx | Dedicated audit |
| `status-announcer` | Existing event stream; Windows/WSL speech as an external example | Low |
| `scheduled-jobs` | External scheduler and session launch versus launchd/Homebrew deployment | Representative embedded dashboard tested; scheduler port remains open |
| `html-doc-overlay`, `annotate-document` | HTML loading, page bridge and annotation/result lifecycle | Adapted fixtures tested; see cookbook coverage and pending release checks |
| `remote-session-picker`, `remote-image-paste` | zmx/live remote and another machine's clipboard; SSH is not equivalent | Deferred remote scope |

## Remaining research stages

### R0 Verify the baseline and clarify status

- [x] Identify the previous C0–C8 plan and implementation records.
- [x] Pin the upstream commit and all 46 directories; read their READMEs.
- [x] Record split-off documentation drift and the recent-history/replay distinction.
- [x] Record implemented web scope, validation limits and deferred remote work.
- [ ] Check other tracker/plan claims against current specs, code and verification.
- [ ] Maintain explicit source, package and runtime version evidence for future
  recipe validation runs.

Acceptance: every claim of implemented behavior has a current contract and an
implementation/verification reference. Stale documentation is distinguished from
feature backlog.

### R1 Update the agterm capability comparison

- [ ] Compare v0.38.1 with the pinned upstream: CLI, response schemas, release notes,
  bundled skill and new lifecycle/UI capabilities.
- [ ] Verify that C0–C8 still cover their selected workflows after later changes.
- [ ] Assess newer cookbook dependencies, including program companions, tint,
  custom-command error reporting and pane/window routing changes.
- [x] Audit and implement the selected local web-overlay contracts; record
  deliberate differences and deferred remote display separately.
- [ ] Complete the OSC 8 assessment without conflating it with URL detection.

Acceptance: a list of new capabilities and changed contracts with upstream/local
source references, excluding work already implemented.

### R2 Build an evidence matrix for all recipes

- [ ] Read scripts, skills/assets and available tests for every recipe. The four
  web-related fixtures provide partial coverage, not completion of this inventory.
- [ ] Record the user outcome, API/JSON/exit codes, environment variables,
  interpreter/dependencies, on-disk data and process requirements.
- [ ] Evaluate Windows PowerShell and WSL separately with concrete shell and
  distribution configurations. Running under WSL alone does not establish portability.
- [ ] Assign a status: built-in coverage; recipe sufficient; adaptation required;
  API/engine/UI change required; alternative workflow; out of scope; unverified.

Each row needs a pinned upstream path, local source/spec, concrete gap, justified
S/M/L adaptation estimate, limitations and a testable acceptance criterion. Until
runtime verification, describe a workflow as a candidate rather than working.

### R3 Validate useful workflows in isolation

The four web-related surfaces have dedicated fixtures and evidence. The initial
candidates for the broader audit remain native-dir-picker, project-launcher,
workspace-sets/window-switcher, overlay-and-split, Claude/Codex resume and
long-commands-status/flag-recent-agents.

Exercise two windows and two application instances; originating split/hidden panes
after swaps; foreground-window changes during a picker; cancellation without side
effects; Unicode; paths containing spaces/quotes; and prompts with special
characters. For resume, verify distinct conversations, working directories,
profiles and launch arguments after restart. For events, cover timeout,
cancellation, disconnect and restart identity.

Acceptance: reproducible steps and observed Windows/WSL outcomes, with explicit
statuses for missing dependencies, unsupported modes and failed checks. Use private
app IDs/pipes and temporary data according to [CONTRIBUTING](../../CONTRIBUTING.md).

### R4 Select the next implementation wave

- [ ] Separate documentation/examples and small adapters from new terminal capabilities.
- [ ] Prioritize useful workflows on the existing API; extend it for confirmed gaps.
  Refine estimates and ordering from R1–R3.
- [ ] Document alternatives, maintenance costs and alignment with
  [product boundaries](../../CONTRIBUTING.md#is-it-worth-it) for OSC 8, program
  companions, scheduling and remote display. Reuse the established web surface
  where appropriate rather than repeating renderer selection.
- [ ] Define each selected OpenSpec change with dependencies, tasks and acceptance
  criteria. Keep remote display/replication separate from the local web change.

The remaining deliverables are the complete 46-recipe matrix, an updated agterm
capability delta, a documentation-drift list and a prioritized backlog. Embedded
web implementation and its acceptance evidence now form an input to that work;
they do not establish complete cookbook parity.
