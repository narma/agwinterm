# Tasks

Prerequisites: C1/C2 and preceding C3 are archived and committed. Groups depend on preceding groups.

## 1. Validated presentation input

- [x] 1.1 Extend typed HUD options and bounded CLI file input, document units/defaults/WSL conversion and verify invalid raw/CLI inputs, duplicate options, plain compatibility and file limits before mutation.
- [x] 1.2 Add a bounded host-free Markdown parser and document its exact supported subset; verify headings, emphasis, code, lists, quotes, rules, tables, malformed syntax and Unicode/work bounds.

## 2. Native lifetime and placement

- [x] 2.1 Implement monotonic expiry and stable pane anchoring with committed lifecycle events; document update/replacement/removal behavior and verify background expiry, stale deadlines, swaps, pane removal and no false events.
- [x] 2.2 Share native layout between rendering and readback for sticky/frame/font/anchor geometry; document effective fields and verify matching client-pixel bounds, DPI changes, occlusion and plain defaults.

## 3. Native formatted presentation

- [x] 3.1 Render the bounded Markdown styles with DirectWrite while preserving passive input; update feature/API/help/agent docs and verify native Unicode screenshots, small bounds and terminal input/history/grid invariants.

## 4. Integrated acceptance

- [x] 4.1 Run Release build, managed regression, existing HUD/control suites and actual WSL message files with spaces; audit normative scenarios, record evidence/limits and update roadmap readiness.

## Workflow follow-up

- Sync the new HUD spec and the C1 event delta, archive C4 and commit it separately after all readiness checks.
- Continue C5 through its own OpenSpec change.
