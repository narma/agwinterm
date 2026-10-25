# Tasks

Prerequisites: C0 and C1 verified, synced, archived and committed. Groups depend on preceding groups; readiness includes tests and documentation, not parsing alone.

## 1. Overlay options and geometry contract

- [x] 1.1 Add and document typed action-specific wire/CLI option validation and an additive host seam; test invalid values, unknown actions, incompatible options and legacy reply compatibility before mutation.
- [x] 1.2 Add independent DIP/percent geometry and update rules in host-free code; test mixed axes, partial resize, full reset, tiny bounds and multiple DPIs.

## 2. Native visibility and lifetime

- [x] 2.1 Implement and document slot hide/show/toggle through queued target resolution, shared surface/input routing and selection restoration; test hidden explicit-ID access, no-op events and background selection preservation.
- [x] 2.2 Preserve hidden running/exited lifetimes, block results, replacement, swap and owner teardown; exercise actual processes and ensure hidden slots cannot retain ordinary key/mouse capture.

## 3. Native geometry and launch options

- [x] 3.1 Use one geometry calculation for cover rendering, regridding, hit-testing and additive tree details; document readback and verify effective visibility, occlusion, dimensions and resize events.
- [x] 3.2 Implement cwd/follow/background-color using target profile and overlay-local rendering; verify invalid options cannot replace a live overlay and successful follow alone changes selection.

## 4. Acceptance and documentation

- [x] 4.1 Update CLI usage, agent skill, control API and overlay documentation with hidden-ID behavior, geometry units/readback, full reset and compatibility boundaries; validate examples.
- [x] 4.2 Run Release build, relevant Core/Pty and existing overlay regressions plus isolated Windows acceptance for lifecycle/input/geometry; record reproducible evidence and limitations.
- [x] 4.3 Run real WSL native-bridge launch/cwd checks including spaces, background targeting and follow; record outcomes and fixture cleanup.
- [x] 4.4 Audit all normative scenarios, validate OpenSpec strictly and update C2 roadmap readiness with verification evidence.

After readiness: sync main specs, archive this change and commit C2 separately. Continue C3 through a new OpenSpec change.
