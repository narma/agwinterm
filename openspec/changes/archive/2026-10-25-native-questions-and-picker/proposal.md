# Proposal

## Why

Roadmap C3 needs native structured questions and richer picker rows so automation can collect deliberate answers without interpreting terminal output or building HTML surfaces. The existing picker already supplies owned native controls, exact-ID waiting and bounded result retention.

## What Changes

- Extend pick with an exact initial selected item ID and optional status/color/shape metadata, preserving filtering and result identity.
- Add ask open/result/cancel for bounded native forms: buttons and hotkeys, text and multiline fields, single/multiple choices and optional other text.
- Support blocking and no-block CLI workflows, stable cancellation reasons, exact request IDs and bounded in-memory retention.
- Share native modal ownership with picker/settings and preserve Windows focus policy, accessible controls and Cyrillic input.

## Capabilities

### New Capabilities

- `native-picker-presentation`: initial selection and accessible status markers without changing existing picker identity/filtering semantics.
- `native-questions`: validated data-only forms, native interaction, exact-ID outcomes and bounded request lifetime.

### Modified Capabilities

None. Existing main specs cover control reliability, targeting and observation, not picker/form behavior. C0 reliability remains the foundation; the documented picker payload/exit conventions remain unchanged.

## Impact

Depends on C0; delivery follows C2. Core owns validation, selection and outcome retention; Pty owns ask routing; Ctl owns bounded UTF-8 input and exact-ID polling; Win32 owns accessible native controls and shared input guards. Includes managed protocol/state tests and isolated native UI acceptance. No shell evaluation of labels/answers, HTML, terminal-rendered form alternative, dependency installation or release.
