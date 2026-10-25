# Proposal

## Why

C5 makes closed work recoverable from scripts and across application restarts. The current per-window menu history is memory-only, captures just the active cwd rather than split state, and removes entries before restore succeeds.

## What Changes

- Add `recent list/open/clear` for closed sessions and workspaces with stable history-entry IDs, newest-first summaries and explicit open-window destination routing.
- Share the improved reopen path with existing menu actions; preserve split identity/layout, profile/cwd, names, flags, context and explicit restore pins.
- Persist bounded app-instance history independently of live restore checkpoints; retain failed workspace members for retry and expose truthful per-member results.
- Preserve existing restore-clear semantics and product exclusions. No foreground-process guessing, remote sessions or automatic program replay is added.

## Capabilities

### New Capabilities

- `recent-closed-work`: Bounded durable history and safe recovery of closed sessions and workspaces.

### Modified Capabilities

None. Existing CLI exit codes and committed lifecycle event contracts apply unchanged to the new operations.

## Impact

`Program.Sessions`, `Program.Services`, native menus, control host/server, CLI arguments/help, agent instructions and session/API documentation. Reuse the existing typed restore model and native session/pane creation. Add pure history tests and isolated Windows/WSL acceptance. C0 is the prerequisite; C1 supplies ordinary create events. C4 is already archived and committed as `0212fac`.
