# Design

## Context

See proposal.md for motivation. Existing PickSpec/PickSelection and PickRegistry separate host-free validation and retention from NativePicker's owned Win32 controls. PickCli has bounded UTF-8 input, exact-ID polling and a cancellable queued creation seam. C0 routing and C2 are archived and committed. The new contracts are in the two capability specs.

## Goals / Non-Goals

Keep terminal rendering and input independent of form state; validate before claiming an owner. Use existing platform controls and preserve picker response compatibility. No HTML, general form scripting, persistence, required-field expression language or automatic live installation.

## Decisions

- Core owns AskSpec, typed answer collection and AskRegistry. Reuse picker window selection and queued-open withdrawal, but keep ask outcomes separate: extending PickOutcome would confuse its shipped wire shape. Completed asks use the same bounded completion-order policy, with explicit cancellation reasons.
- Forms show one field per page, with Back/Next and always reachable submit/cancel actions. This bounds native control count and makes 16 fields usable at high DPI without custom scrolling containers. Native EDIT supplies single/multiline text; LISTBOX supplies single/multiple selection and accessible selection state. Optional other text has a separate labelled EDIT. Capture a page before moving or answering; recreating controls must not discard saved values.
- Alt character shortcuts are handled through WM_SYSCHAR using the current layout, case-insensitively. Ordinary text does not invoke buttons; multiline Enter is preserved. A default submit button is supplied only when buttons are omitted. Explicit empty buttons are invalid. Labels and IDs are data; button labels escape native mnemonic ampersands where appropriate.
- Ask open uses args.form plus follow and ordinary window selection. Result wraps its payload as result.ask, parallel to result.pick. CLI emits the ask payload, no-block emits the ID, and cancel emits a stable JSON acknowledgement. Failed known-ID waits make one bounded best-effort cancel; unknown-ID open failures preserve uncertainty without retry. Add ask to the Linux bridge's cancellable commands for actual SIGINT handling.
- Share modal guards through a single predicate and route owner-window keyboard/mouse to the currently owned native dialog. Owner shutdown resolves pending ask before native destruction. Construction failure/withdrawal aborts its registry record and disposes only that dialog.
- Picker metadata remains an additive record extension. Initial selection is applied once after initial native filtering. Owner-drawn native list rows retain LBS_HASSTRINGS for accessibility and draw only the requested marker specially; status is included in the native row string, filtering still uses label only. Ordinary rows retain their old appearance.

## Risks / Trade-offs

- Native focus/reentrant destruction can race completion → first-writer registry outcomes, disposal guards and queued-open withdrawal tests.
- Small work areas and long labels → paged fields, bounded controls, wrapped prompt/labels and monitor-clamped DPI layout; verify native screenshots and keyboard traversal.
- Accessibility of owner-drawn rows → retain listbox strings and test accessible row text; do not make color the only status representation.
- Open reply loss can leave an unknown dialog → document uncertainty and do not silently create a replacement.
- WSL Ctrl+C does not reliably reach .NET → extend the existing native bridge interception and verify its real terminal behavior.

## Migration Plan

Additive protocol and optional picker fields require no persisted-data migration. Build and test managed validation/routing first, then isolated native acceptance and WSL cancellation. Update help/API/feature docs together. Sync/archive and commit C3 only after readiness; no installed binaries or user config are changed.
