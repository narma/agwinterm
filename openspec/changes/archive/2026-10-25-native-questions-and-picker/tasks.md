# Tasks

Prerequisites: C0 and C2 archived and committed. Groups depend on preceding groups.

## 1. Picker presentation

- [x] 1.1 Implement selected ID and validated status/color/shape through Core, CLI and native rows; document the additive contract and verify filtering, identity, initial selection and invalid-input tests.

## 2. Form contract and transport

- [x] 2.1 Implement bounded form parsing, typed values and first-completion result retention; document schema and verify malformed forms, answer shapes, cancellation races and retention tests.
- [x] 2.2 Implement ask host/wire routing and bounded CLI stdin/exact-ID polling; update help and agent skill, and test usage-before-connect, refusal, known/unknown-ID transport loss and cancellation.

## 3. Native form ownership and interaction

- [x] 3.1 Implement paged native fields, buttons, logical-character shortcuts and answer collection; document keyboard behavior and verify text/multiline/choice/other values and Latin/Cyrillic navigation in an isolated instance.
- [x] 3.2 Integrate shared ask/pick/modal guards, queued-open withdrawal, focus policy and teardown; verify mutual refusal, completed-result stability, owner-close and input isolation with native acceptance.

## 4. Cross-platform acceptance

- [x] 4.1 Extend the native WSL cancellation bridge for ask; verify actual terminal Ctrl+C, exact-ID cancellation and no-block form use from WSL, and document the result.
- [x] 4.2 Run Release build, managed regression, native picker/form acceptance including DPI/accessibility and existing control regression; audit every normative scenario, record evidence/limitations and update roadmap readiness.

## Workflow follow-up

- Sync the two main specs, archive this change and commit C3 separately after all readiness checks pass.
- Continue C4 through its own OpenSpec proposal, specs, design and tasks.
