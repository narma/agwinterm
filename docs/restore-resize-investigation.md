# Restored buffer reflow

Branch: codex/resize-reflow (base narma / ceba865).

Both managed and Rust terminals now record automatic wrapping separately from explicit line
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
New snapshots from either core share the attributed protobuf format.

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

## Rust follow-up

Rust records wrap lengths through scrolling/erasure and reflows its restored
prefix natively. ABI 21 adds protobuf save/restore calls, preserving colours,
hyperlinks and full Unicode. The Windows UI and PTY reattach paths use attributed
snapshots for either core. The native PTY host uses the same schema and serializer.
The adapter refreshes its history mirror after reflow and detects front eviction
even when the history count remains unchanged.

The user confirmed the right-side gap affects only older output; the input area
and new output already use the full width. No extra right padding was added or
removed. The regression test checks an old restored line joining across its
previous right edge after widening.

Windows native builds now link the CRT statically: the dynamic CRT DLL was absent
in a clean Sandbox and caused native-core loading to fail. No extra runtime
installation is required. Initially local antivirus/file access interference
prevented loading test DLLs; after the user added an exclusion, real native tests
passed (a missing/unloadable DLL is not counted as successful Rust validation).

Validation: 609 .NET Core tests; 54 Rust core tests and 9 Rust PTY-host tests pass.
Background Sandbox RestoreRoundtrip / buffer-reflow-rust passes all five checks,
including explicit native DLL loading (no managed fallback), restore/widen, and
narrow-save/restart/widen. Evidence: artifacts/sandbox-reflow-rust-static/results.
Sandbox 037d1ee6-7019-4769-93e8-30a4b5dc795a was stopped after completion.

Final PTY validation: all 1732 Agwinterm.Pty.Tests pass (two existing xUnit analyzer warnings).
