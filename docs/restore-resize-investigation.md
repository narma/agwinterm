# Restored buffer reflow

Branch: codex/resize-reflow (base narma / ceba865).

The managed terminal now records automatic wrapping separately from explicit line
ends. Metadata follows screen row moves and scrollback capture and is cleared by
line/display erasure and line replacement. A deferred wrap is recorded only when
the following glyph actually wraps. The wrap length excludes the padding before
a double-width glyph.

PRow fields 2 (soft_wrapped) and 3 (wrap_length) extend the existing v1 protobuf
compatibly. The saved history preserves cells outside the current viewport.
Restore joins only explicitly continued rows and lays them out at the current
width. Subsequent width changes reflow the restored prefix, preserving attributes,
hyperlinks, spaces and wide-glyph pairs. The fresh shell's live screen and later
scrollback retain the existing ConPTY resize behavior. Physical selections are
cleared on width changes; shell marks after the restored prefix move with it.
History eviction remains bounded by the configured limit and existing trim slack.

Older snapshots contain no wrap information. Their physical rows remain separate
logical lines; the implementation does not guess that a full row was soft-wrapped.
This fixes newly saved managed-core snapshots; the experimental Rust core's
existing plain-text disk fallback cannot provide missing wrap metadata.

Regression coverage includes history/screen boundaries, explicit newlines at the
right edge, colors and hyperlinks, CJK and supplementary Unicode, wide-glyph
padding, repeated narrow-save-restore-grow cycles, erasure, inserted/deleted lines,
legacy snapshots and history eviction. The background Sandbox restore-roundtrip
cell buffer-reflow exercises real PowerShell/ConPTY output and two app restarts.
Validation: all 606 Core tests pass; Release Win32 build has zero warnings/errors.
Background Windows Sandbox RestoreRoundtrip / buffer-reflow passed all four
checks, including the second narrow-save/restart/widen cycle. Evidence:
artifacts/sandbox-reflow/results/stdout.log and result.json. The test Sandbox
6134767a-40d8-49f8-8e80-26c6cebb3084 was stopped after completion.
