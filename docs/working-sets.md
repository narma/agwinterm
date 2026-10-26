# Working sets

`workspace flag on|off|toggle --target WORKSPACE` affects all members, including hidden
or collapsed sessions; toggle inverts each member. `workspace new --collapsed` creates
an empty collapsed workspace. These operations never flag just the visible subset.

`session new --before SESSION` / `--after SESSION` creates beside an exact session ID,
unique ID prefix or unique name in the selected window. The anchor supplies its workspace;
an explicit `--workspace` or `--workspace-name` must agree, and `--create-workspace` is
incompatible. Existing cwd/profile/command/no-select behavior applies. Main session IDs
are accepted; a split-only ID or auxiliary surface is not an organization target.
`session move --target SESSION --before ANCHOR` / `--after ANCHOR` uses the same rule.

Batch operations are explicit:

```text
agwintermctl session close --targets ID1,ID2,ID3
agwintermctl session move WORKSPACE --targets ID1,ID2
agwintermctl session move --targets ID1,ID2 --after ANCHOR
```

Raw requests use `targets: ["ID1", "ID2"]` in `session.close`/`session.move` args.
There must be 1..128 nonempty selectors. Repeating `--target` remains an error; it does
not form a batch. Duplicate selectors or aliases resolving to one session, an anchor
inside the batch, and invalid destinations refuse before any mutation. Missing/ambiguous
or unsupported individual targets produce errors while valid members proceed. The reply
has `outcome: completed|partial` and ordered `items` with target, sessionId, changed and
error. Exit 0 means the batch was processed: inspect every item. Global refusal exits 1,
local CLI misuse 2. No rollback is promised. Batch close retains normal last-session
fallback-shell behavior and records each closed session in recent history.

Moves preserve session/pane IDs and insert successes in request order. The anchor never
moves. Ordinary single-target move directions remain available.

`sidebar flagged-layout plain|flat|tree|toggle` is per window. Plain retains the current
names-only default; flat adds ` : workspace`; tree shows only groups with flagged members,
using the existing collapse state. Toggle switches tree to flat and either flat form to
tree. Flagged views ignore workspace focus filters. Layout is remembered independently
of ordinary sidebar mode (`sidebar mode tree|flagged`).

`workspace focus on|off|toggle --target WORKSPACE` edits a retained set; adding a member
enables filtering. `workspace filter on|off|toggle` changes visibility without clearing
that set. `workspace focus clear` empties the set and disables filtering. An enabled empty
set shows all workspaces. Legacy no-target focus on selects only the active workspace;
off/toggle-off disables filtering while retaining membership. Context-menu focus edits
that workspace's membership. Keyless actions `toggle_workspace_filter` and
`clear_workspace_focus` are available in menus/keymaps.

Explicitly selecting an out-of-set workspace/session disables ordinary-tree filtering
without deleting membership. Workspace deletion removes only that ID. Ordinary session
navigation follows visible sidebar order, excluding collapsed/filtered sessions. Workspace
navigation still includes collapsed/empty groups and refuses in flagged mode.

Tree and window-state replies add `workingSet` with `focusedWorkspaceIds`,
`workspaceFilterEnabled`, `sidebarFlaggedLayout` and `visibleSessionIds`. Restore adds
optional fields and migrates an old single focused workspace to one enabled member;
unknown IDs/duplicates are dropped and invalid layouts become plain. Older builds may
drop these additive fields when writing state, as with other restore extensions.
