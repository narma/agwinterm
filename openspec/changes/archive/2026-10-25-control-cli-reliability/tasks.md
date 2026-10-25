# Tasks

Implementation authorized by the user's apply request. Groups follow dependency order; preserve unrelated working-tree changes. Acceptance evidence and coverage limits are recorded in verification.md. Release, live installation and archive/sync remain separate actions.

## 1. Truthful ordinary CLI outcomes

- [x] 1.1 Extract and wire ordinary reply validation/output handling in Agwinterm.Ctl; verify actual process exit codes and stdout for successful/refused JSON and text replies using a disposable pipe endpoint.
- [x] 1.2 Cover malformed envelopes, transport failure, UTF-8 output and timeout uncertainty; verify protocol diagnostics use stderr and specialized version/picker outcomes and local help remain unchanged.
- [x] 1.3 Update control-api.md and CLI/agent guidance for JSON failure codes; verify examples agree with the process-level tests and note the intentional compatibility change.

## 2. Argument validation before side effects

- [x] 2.1 Encode the accepted operation/option vocabulary from current builders, help and feature docs, including flag/value/optional-sound rules; verify representative valid invocations from every command family with parameterized parser tests.
- [x] 2.2 Reject unknown options, missing values, invalid numeric values and duplicate target/window/transport selectors before request construction or local installation; verify invalid invocations exit 2 and the fake endpoint receives no connection.
- [x] 2.3 Integrate existing strict HUD/text/split/frame/picker validation without weakening it; verify stdin ambiguity, literal positional text, case-insensitive options, boolean flags before text and PowerShell/direct launch cases.
- [x] 2.4 Update CtlUsage, AgentSkill.cs and control-api.md to state accepted aliases, non-batch selectors and failure behavior; verify documented examples against parser tests and existing help tests.

## 3. Explicit split target routing

- [x] 3.1 Carry explicit target through CLI, ControlServer and ISessionHost for focus/resize, retaining omitted-target active scope; verify dispatcher tests and environment-versus-active regression cases for CLI and raw requests.
- [x] 3.2 Refactor Win32 split focus/resize helpers to mutate a resolved session on the UI queue without rebinding unrelated visible input; verify two-session background focus/ratio tests and unchanged recency/unseen state.
- [x] 3.3 Cover window mismatch, unknown/ambiguous/empty/deleted targets, swapped IDs, single-pane and auxiliary refusals, axis errors and integer growth units; verify failures leave both target and visible sessions unchanged in isolated fixtures.
- [x] 3.4 Update split API/help/agent documentation and relevant QA cases with explicit-target behavior and legacy defaults; verify the examples identify the owning session and preserve existing split semantics.

## 4. Explicit custom-command context

- [x] 4.1 Extend command.run dispatch and host context to carry session/pane, workspace, profile and cwd; verify named and raw command tests reject invalid targets/modes without active fallback.
- [x] 4.2 Pass resolved context through AGW token/environment expansion and new/overlay/detached/send execution; verify generated context and actual destinations from two distinct sessions, including a swapped pane.
- [x] 4.3 Separate explicitly targeted send from the human broadcast path; verify readonly/input-closed refusal, no broadcast leakage, unchanged untargeted behavior and honest partial-input failure reporting.
- [x] 4.4 Verify new-mode target workspace/profile and normal new-session selection, overlay target slot, detached cwd and quick restrictions using isolated Win32 integration cases; assert the source is never temporarily selected.
- [x] 4.5 Extend the existing WSL fixture for targeted custom commands across Windows/WSL contexts; verify distribution/user, Linux cwd and AGW identity with native helpers, recording unavailable environment coverage as incomplete rather than passed.
- [x] 4.6 Update custom-command help, AgentSkill.cs, control-api.md and WSL guidance for target defaults and mode side effects; verify documented examples match the integration scenarios.

## 5. Integrated readiness

- [x] 5.1 Run Release build and relevant Core/Pty test suites, then required repository checks; verify no new failures and record exact commands/results without altering unrelated pending work.
- [x] 5.2 Run isolated UI/WSL acceptance using the repository's current safety and cleanup procedures; verify each scenario in both delta specs has test or observed acceptance evidence and retain explicit limitations for any unrun checks.
- [x] 5.3 Review the complete C0 change for compatibility, target safety and accidental scope expansion; verify documented exceptions remain, C1–C8 are not implemented and shared Lite conformance is not silently expanded.
- [x] 5.4 Update the adoption plan's C0 status only after its acceptance evidence is complete; verify the status distinguishes implementation readiness from release/installation and leaves C1–C8 pending.

## Workflow follow-up

- Archive/sync specs only when implementation and review are complete and the user requests that workflow.
- Prepare C1 as a separate OpenSpec change after C0; keep the roadmap's exclusions and dependencies.
- Release and live installation remain outside this change.
