# Tasks

Prerequisite C0 is complete; C1 lifecycle observation remains in force. Each group depends on the preceding group.

## 1. Durable history model

- [x] 1.1 Add validated bounded session/workspace history with stable entry IDs and atomic persistence; document limits and verify round-trip, corruption, eviction, invalid payloads and disk-error behavior with focused tests.

## 2. Native capture and safe recovery

- [x] 2.1 Capture both panes and restore supported metadata through native creation, sharing menus and CLI; document pin/no-replay semantics and verify stable IDs, split/swap, profiles, empty workspaces and cross-window collisions.
- [x] 2.2 Retain failed members, roll back partial session creation and bind partial workspace retries; document error outcomes and verify retry does not duplicate successes or lose failures.

## 3. Control and CLI contract

- [x] 3.1 Add recent list/open/clear with strict raw/CLI validation and destination routing; update help/API/agent docs and verify local exit codes, exact IDs, no side effects on refusal, clear isolation and unknown/closed/quick destinations.

## 4. Integrated acceptance

- [x] 4.1 Run Release build and managed regression plus owned native and actual WSL recovery/restart checks; audit normative scenarios, record evidence/limits and update roadmap readiness.

## Workflow follow-up

- Sync the new capability, archive C5 and commit it separately after readiness checks.
- Continue C6 through its own OpenSpec change, then C7 and C8 likewise.
