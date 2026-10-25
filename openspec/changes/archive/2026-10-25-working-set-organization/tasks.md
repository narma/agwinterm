# Tasks

Prerequisites C0/C1 are archived; C5 is committed. Each group depends on previous groups.

## 1. Projection and persisted working sets

- [x] 1.1 Add validated focus-set/filter/layout projection and additive restore fields; document migration/defaults and test legacy single focus, disabled retained sets, invalid IDs/layouts and visible order.

## 2. Workspace and placement operations

- [x] 2.1 Implement workspace flagging/collapsed creation and atomic anchored session new/move; update API/help/agent docs and verify hidden members, anchor ambiguity, workspace conflicts, unchanged IDs and Windows/WSL launch behavior.
- [x] 2.2 Implement explicit bounded batch close/move with preflight duplicate checks and per-target outcomes; document exit semantics and test partial errors, alias duplicates, ordered insertion and recent-history capture.

## 3. Native sidebar and navigation

- [x] 3.1 Implement native plain/flat/tree layouts, multi-workspace membership and independent filter controls using shared projection; update navigation docs and verify screenshots, visible order, empty/collapsed groups, selection and additive readback.

## 4. Integrated acceptance

- [x] 4.1 Run Release build, managed regression, native working-set/navigation/control suites and WSL placement checks; record evidence/limitations, audit normative scenarios and update roadmap readiness.

## Workflow follow-up

- Sync the new capability and CLI delta, archive C6 and commit it separately.
- Continue C7 and C8 as separate OpenSpec changes with verification, archive and commits.
