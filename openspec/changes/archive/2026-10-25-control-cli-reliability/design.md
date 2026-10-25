# Design

## Context

See proposal.md for motivation and the two delta specs for observable requirements. This is C0 only.

Observed implementation:
- `Agwinterm.Ctl/Program.cs` splits options into a dictionary, overwrites duplicates and lets most options consume the next token. A few verbs have strict allowlists, but many ignore unknown fields. Its JSON reply branch prints the response and returns zero without testing `ok`.
- `CtlUsageTests`, `CtlRequestTests`, `PickCli` and `FrameShmCli` already establish help, encoding, timeout and specialized-parser contracts. Keep those seams rather than replacing every parser indiscriminately.
- `ControlServer` receives a target but `ISessionHost.FocusPaneDir`, `ResizeSplit` and `CommandRun` have no target parameter. Win32 implementations use `_active`.
- `Program.Input.cs` also derives custom-command tokens, environment and input via `ActiveSurface()` and `ActiveWorkspace()`. Changing only the dispatcher would leave wrong-context execution.
- Host mutations run on the UI queue. Split focus currently calls `SyncSession`, and resize uses an active-only helper. These paths must distinguish model mutation from binding the visible surface.
- Current docs explicitly describe focus/resize as active-session operations. Other verbs generally default to caller pane IDs. Existing untargeted behavior is therefore preserved rather than generalized.

## Goals / Non-Goals

**Goals:** make failures machine-readable; validate before dispatch; carry explicit target context through all affected layers; verify no collateral selection or input.

**Non-Goals:** a general command framework, acknowledgements from child programs, transport exactly-once delivery, wire-wide unknown-field rejection, C1–C8 features, new persistence, automatic Lite changes, or release/install work.

## Decisions

### 1. Separate reply interpretation from presentation

Extract a small testable ordinary-reply path that validates the envelope, computes exit status and formats output independently. JSON keeps the raw server reply, including refusals; diagnostics for invalid replies go to stderr. Preserve specialized version/picker paths. Do not repair this by suppressing JSON errors or requiring scripts to parse `ok` themselves.

### 2. Validate the existing CLI vocabulary before side effects

Introduce testable per-operation option metadata: accepted names/aliases, flag versus valued option, numeric/value validation and selector multiplicity. Preserve existing verb builders and specialized validation initially. Audit accepted forms against `Program.cs`, `CtlUsage`, `AgentSkill.cs` and current docs, including less-visible operations, before enabling each descriptor.

Reject duplicates for single-value selectors, including pipe/socket aliases; this is not the future batch-target API. Preserve documented optional sound forms, stdin conflicts, help handling and command-specific option rules. Unknown wire fields outside the three changed handlers are not part of this CLI hardening project. A third-party parser dependency is unnecessary and would enlarge migration risk.

### 3. Explicit targets are additive; legacy defaults remain

For these three CLI operations, omission sends no target and retains active scope; explicit `active` resolves within the routed window. Do not accidentally activate the generic `DefaultTarget()` caller fallback when adding support. Other verbs retain their existing caller defaults. Document this exception clearly in help/API/skill.

Pass target through the dispatcher and host interface. Resolve and validate on the UI thread using existing session/pane resolvers, within the already-routed host. Revalidate liveness at execution. Never temporarily assign `_active` to reuse helpers: that would affect recency, notifications and visible input.

### 4. Model operations accept a session explicitly

Refactor split focus/divider calculations to accept the resolved session. Update its remembered state regardless of visibility; synchronize visible bindings only if that session is actually selected. Preserve axis refusals and row/column units. UI/keymap callers continue to supply the active session. Auxiliary split targets refuse.

### 5. Custom-command context is explicit and complete

Pass a resolved context containing owning session, addressed pane/surface, workspace, profile and cwd into expansion and execution. Derive AGW values from that context rather than `ActiveSurface()`. Session versus exact pane IDs retain existing resolver semantics; explicitly targeted send does not broadcast. Untargeted human-command paths retain existing broadcast behavior.

Reuse existing new/overlay/detached/send execution policies and Windows/WSL launch helpers. New mode still selects its newly created session, but uses the target workspace/profile; the source session need not be selected first. Overlay mode uses the target's existing slot rules. Quick retains send/detached-only support. Reject unknown modes instead of falling through to send. Input success continues to mean accepted transport input, not child completion.

### 6. Validate through externally observable behavior

Use process-level CLI tests with a disposable fake named-pipe endpoint for actual stdout/stderr/exit codes and assertions that invalid inputs never connect. Unit tests cover option rules and dispatcher target propagation; extend existing fake hosts as needed. Isolated Win32 integration exercises two sessions in two windows, swapped panes, hidden sessions, readonly/send/broadcast, token expansion and active selection. WSL integration proves target distribution/user/cwd, using the current repository fixture and native helper.

## Risks / Trade-offs

- Previously ignored flags start failing → document the compatibility change and audit documented aliases; do not preserve accidental last-value-wins targeting.
- Active and caller defaults differ → test with `AGWINTERM_SESSION_ID` deliberately different from the selected session and document these three exceptions.
- Custom commands use several active globals → refactor expansion and execution together; verify AGW values and actual destination, not only dispatcher arguments.
- UI state can change while requests queue → resolve on the UI thread and refuse removed targets; no active fallback for explicit targets.
- Existing transport cannot establish child execution → retain uncertainty wording; do not add rollback or retry guarantees.
- Local modifications already exist in integration/test project files → preserve them; apply focused changes without resetting the working tree.
- Lite may not support new explicit routing → do not silently widen shared conformance or promise cross-product parity; document the serving-version requirement.

## Migration Plan

Implement and test CLI outcomes/validation first, then explicit host routing and custom-command context. Update docs and perform focused regression plus required repository checks before marking C0 complete. No persisted data migration is required. Rollback restores previous binaries but also restores their known CLI defects; no automatic live installation or release belongs to this change.
