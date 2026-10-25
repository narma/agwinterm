# Design

## Context

See proposal.md and the existing native sidebar/session code. `CycleSession` currently uses every session; `DrawTreeList` filters one nullable workspace ID while `DrawFlaggedList` draws names only. `NewSession` posts creation asynchronously; anchored creation must resolve and insert in one UI action. Existing C0 explicitly prohibited accidental batching via repeated selectors.

## Goals / Non-Goals

Goals: deterministic local organization and truthful outcomes, preserving pane IDs and normal command defaults. Non-goals: cross-window session transfer, remote sessions, automatic grouping, full upstream UI parity or changing the local default flagged presentation.

## Decisions

- Add a narrow working-set host seam rather than expanding every Lite/shared method. New operations and enhanced new/move/close paths validate on the pipe/CLI side, then resolve and mutate within one queued UI action. Ordinary calls keep their existing path. Anchored new uses existing launch validation and CreateSession; it forbids create-workspace because an anchor already supplies its workspace.
- Use `--targets A,B` for batches and a typed wire array. Validate count, syntax, duplicates, destination and anchor before any mutation. Resolve all members first; duplicate resolved sessions refuse globally. Missing/ineligible members remain individual errors. Successful moves are inserted together in input order; close reuses normal close/history behavior. Return structured outcomes even for a partial accepted batch; document exit 0 means the batch was processed, not every member succeeded.
- Add a shared pure working-set projection for membership migration and visible order, consumed by native drawing/navigation/readback. Plain remains the default; flat appends ` : workspace`; tree groups flagged sessions. Per-window persistence follows existing local sidebar ownership, unlike upstream's app-wide layout setting. Flagged tree uses the existing workspace collapse state, with no new secondary expansion model.
- Replace single-focus storage with a retained set plus enabled flag. `workspace focus on/off/toggle [--target W]` controls membership, `clear` empties it; `workspace filter on/off/toggle` controls visibility independently. Legacy no-target on/off/toggle retains effective single-focus behavior by selecting active membership on on and disabling on off/toggle-off, without erasing the retained set. Native context-menu focus acts on that workspace's membership. Add explicit filter/clear actions without new default shortcuts.
- Add optional state fields with WhenWritingNull so default files retain the old shape. Preserve legacy FocusedWorkspaceId when exactly one enabled member exists; new fields become authoritative when present. Validate IDs against restored workspaces, deduplicate in workspace order and fall back to plain layout. Deletion drops only the removed membership; explicit out-of-set selection disables filtering and preserves membership.
- Expose effective focus IDs/filter/layout and visible session IDs in tree/window state. Draw and navigation use the same projection. Committed mutation paths keep C1 tree/session events; no event claims a change for a refused request.

## Risks / Trade-offs

- Batch close can create a fallback shell when closing the last active session → keep existing close semantics and report only explicitly requested closures.
- A child launch can fail later after creation → retain existing accepted-launch meaning; do not claim process completion or transaction rollback.
- Collapsed groups hide sessions from ordinary navigation → intentional visible-order behavior, covered by native acceptance; workspace navigation still includes collapsed/empty groups.
- Older builds drop additive fields on write-back → documented existing restore-format limitation; no schema version bump.

## Migration Plan

Migrate legacy single focus on read; old default files remain valid. Keep local sidebar presentation default. Validate UI, raw/CLI refusals, batches, persistence and WSL placement before sync/archive and the C6 commit. Then proceed to separate C7 and C8 changes.
