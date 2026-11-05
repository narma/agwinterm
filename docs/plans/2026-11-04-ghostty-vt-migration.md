# Ghostty VT migration: initial audit and implementation decisions

Status: reproducible Windows build, native adapter, opt-in renderer integration and executable contract tests. **Not a complete replacement backend or rollout acceptance.** No production default changes. Tracking: [#34](https://github.com/narma/agwinterm/issues/34).

## Destination and scope

The destination is a single libghostty-vt engine. Managed and Rust are temporary rollout/oracle backends, not a permanent compatibility architecture. Prefer Ghostty's state, grapheme, input, selection and snapshot semantics over reproducing limitations of the existing interface. Keep Win32/Direct2D/DirectWrite, ConPTY, PTY host, WSL/ZMX, control API and agent lifecycle. Sixel is excluded. Upstream VT maintenance is the purpose of this migration: do not add a second general parser in the adapter.

Parity with agterm means relevant terminal behavior and feature coverage, not embedding its macOS UI or rendering stack. It needs an explicit executable workload comparison; sharing Ghostty alone is not evidence of parity. Preserve the application-specific presentation and remote contracts already shipped here.

## Source evidence

Local audit starts at `b48dbb9` (2026-11-04). Current Ghostty build revision: `f5a1a0fedd8762d490bc2951dc4b63a0f1b030e0`, updated at user request after `40066da` and the initial `ee02d019` audit. Its `build.zig.zon` requires Zig **0.17.0**. The source builds with `-Demit-lib-vt=true -Doptimize=ReleaseFast -Dtarget=x86_64-windows-gnu -j4`, plus the narrow history export described below. The official Zig archive is SHA-256 pinned in `native/ghostty-vt/pin.json`; the build records both output DLL hashes. MSVC-target output initially required a VC runtime absent from the clean test image; GNU-target output passes clean-Windows loading. The managed loader verifies provenance and both hashes before loading, then checks exports, ABI layouts and revision.

- [Pinned terminal API](https://github.com/ghostty-org/ghostty/blob/f5a1a0fedd8762d490bc2951dc4b63a0f1b030e0/include/ghostty/vt/terminal.h)
- [Pinned render API](https://github.com/ghostty-org/ghostty/blob/f5a1a0fedd8762d490bc2951dc4b63a0f1b030e0/include/ghostty/vt/render.h)
- [Pinned snapshots](https://github.com/ghostty-org/ghostty/blob/f5a1a0fedd8762d490bc2951dc4b63a0f1b030e0/include/ghostty/vt/snapshot.h)
- [Pinned graphics API](https://github.com/ghostty-org/ghostty/blob/f5a1a0fedd8762d490bc2951dc4b63a0f1b030e0/include/ghostty/vt/kitty_graphics.h)
- [Pinned modes](https://github.com/ghostty-org/ghostty/blob/f5a1a0fedd8762d490bc2951dc4b63a0f1b030e0/include/ghostty/vt/modes.h)
- [Microsoft native interop guidance](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/best-practices)

## Existing coupling that must change

`ITerminalCore.Screen` exposes a mutable concrete `ScreenBuffer`. `RustTerminalCore.Sync` copies and converts the full native grid after every Feed/Resize, caches history and resolves links. `Program.Render.RenderTerminal` then copies the viewport again under the session lock. Moving that design to Ghostty would retain unnecessary work on every PTY chunk, including hidden panes.

`Cell.Rune` holds one scalar, not a grapheme. A native parser alone cannot fix combined emoji or combining marks throughout DirectWrite, selection, dumps, search and accessibility. The frame/text representation must carry complete clusters with an ASCII fast path and explicit column widths.

Persistence bypasses the interface: `BufferPersist.Serialize/Restore` special-case Rust and cast everything else to `TerminalEmulator`. Additional type gates exist in `ServerSession`, `PtyHostServer` and `Program.Services`. `PtyHostServer` has a managed-specific bundled-ConPTY repaint path. These are migration work, not safe places to silently treat Ghostty as managed. `ShellMark` is also nested inside `TerminalEmulator`.

The interface comments saying Rust lacks images and callbacks are stale; the actual implementation supports both. Use implementation and executable checks as evidence.

## Initial contract map

Classification is about API availability, not tested parity. **Direct** still needs a binding and lifetime validation. **Adapter** means representation/ownership translation. **Sidecar** means application-owned state. **Unverified** is a blocking investigation, not an assertion that upstream cannot do it.

| Existing contract | Classification and direction |
| --- | --- |
| Feed, Resize | Direct: terminal VT write and resize; inspect processing errors. Configure ConPTY resize-pull behavior explicitly. |
| Screen | Adapter: render-state capture and changed-row bulk export; retire mutable ScreenBuffer as the universal read contract. |
| CursorRow/Col/Visible/Shape, IsAltScreen | Direct: terminal cursor and active-screen data; capture consistently with the frame. |
| ScrollTop/Bottom | Unverified: no matching scalar terminal-data getter found in inspected header; inspect screen API before deciding whether consumers need these values. |
| MouseReporting/ReportsMotion/Click/Drag/Motion/Sgr/SgrPixels | Adapter: tracking data plus modes; validate native mouse encoder, including pixel coordinates. |
| BracketedPaste, FocusReporting, KeyboardFlags | Direct mode/keyboard data; input encoder integration still needs application tests. |
| SynchronizedOutput | Adapter: render-hold callback, captured pre-hold frame, bounded timeout. Updating render state unconditionally defeats the hold. |
| Win32InputMode | Unverified: no 9001/win32 entry found in the inspected modes header, `src/terminal/modes.zig` or C terminal implementation. Check stream handling and exercise the mode before concluding unsupported. If necessary add only a bounded Windows-specific recognizer with split-input tests. |
| DynamicBg and theme colors | Adapter: upstream colors/palette to application default/indexed/RGB semantics; avoid using RGB zero as absence. |
| ScrollbackMax, HistoryCount | Direct line-limit and scrollback-row data; verify trimming/reflow semantics. |
| ScrollGeneration | Adapter: application invalidation epoch; do not infer all changes from history count. |
| GetHistoryCell, GetRowWrapLength | Adapter: `grid_ref.h` exposes cell/row/grapheme/style/link access; `screen.h` exposes WRAP and WRAP_CONTINUATION. Validate translation to the current wrap-content-length convention, especially wide-character edge padding. Never reconstruct wraps from trailing spaces. |
| SeedScrollback | Adapter: legacy import; do not replay untrusted restored text as executable escape sequences. |
| Marks | Adapter: semantic-prompt callback to application command boundaries; tracked grid references are a candidate for surviving reflow, with explicit invalidation on trim. Validate exact prompt/output/exit boundaries. |
| Title, Cwd | Direct data and effects; preserve host handling. |
| Host.Respond/Bell/Notify/Progress/ClipboardWrite/DefaultColors | Direct effects plus adapter dispatch. Never enable clipboard-read callbacks. |
| Host.CommandFinished | Sidecar: authenticated application shell-completion protocol; retain token/sequence validation. |
| Host.Unhandled | Adapter: upstream unknown-sequence callback; header documents OSC/APC, not a universal CSI debug hook. |
| Images, Placements, HasImage | Adapter: native Kitty store to existing Direct2D cache, with identity/generation tracking and bounded resources. |
| SetImageData, PlaceImage, ClearPlacements | Sidecar or verified direct native import: preserve host shared-memory frame path without base64 recoding. Investigate native injection API before choosing. |
| PlaceSixel | Unsupported by migration policy; no emulation/fallback implementation. |
| CellPixelWidth/Height | Adapter: update terminal pixel geometry used by graphics and mouse input. |
| DumpRow/HistoryRow/Buffer | Adapter: cluster-aware text extraction; distinguish logical text from physical rows. |
| DumpModes | Adapter: existing reattach contract must be audited against native state/snapshot semantics, not guessed escape sequences. |
| Dispose | Native ownership: release render state, terminal, callback roots and image references in a defined order. |
| BufferPersist / PTY reattach | Adapter: versioned Ghostty snapshot envelope and one-way legacy import; full snapshot is not equivalent to history-only ConPTY repaint. |

## Interop decision

Use source-generated `LibraryImport` bindings for small ordinary C API calls. Prefer `SafeHandle` ownership, exact C-width fields (`nuint` for size_t, explicit byte for C bool where appropriate), sequential blittable structures and explicit calling conventions. Pin input spans only for the duration of synchronous calls; avoid string/StringBuilder marshalling in the hot path. Returned borrowed memory must not outlive its documented owner/update boundary. Callback exceptions must never cross into native code.

Use a small C/Zig shim only where a bulk operation is needed: export changed rows, style identifiers, grapheme bytes and link identifiers in bounded reusable buffers. Keep loops over cells on the native side instead of thousands of managed/native transitions per frame. No VT parsing, Unicode width logic or reflow algorithm belongs in that shim. Confirm this tradeoff with measured capture cost before freezing the ABI.

Expose a shim handshake containing ABI version, struct sizes, required capability bits and pinned upstream revision. Validate before creating sessions. Library failure may select managed for **new** sessions during transition; never switch a live native handle to another engine. Do not apply SuppressGCTransition to writes/capture/resize or any callback-capable operation.

Callbacks run synchronously on the feeding thread. Queue bounded host effects and dispatch in order through the existing host seam; never reenter Feed or synchronously wait on UI. Register callbacks and userdata with lifetimes covering the terminal. The render-hold callback needs the special upstream-prescribed pre-hold capture path, not delayed capture after the entire write.

## Frame ownership

Feed updates authoritative native state and cheap host/mode metadata, not a managed full-grid mirror. On a visible capture, hold `session.SyncRoot` for `begin_update` and matching terminal metadata, then release it before `end_update`, conversion and drawing. Only one consumer may mutate a particular render state. A frame keeps its own row/cluster storage alive through painting; no borrowed terminal pointers escape the lock. Clean dirty state only after successful consumption; force invalidation after resize, viewport/theme changes and device loss. Unchanged rows can reuse cached data.

Text/control/UIA reads require a separate explicit read/capture path under the session lock. They must stay correct for hidden panes without forcing paint or persistent full-screen duplication. A temporary adapter for old cores is acceptable; do not build a permanent pluggable-engine framework.

## Regression gates and measurements

[#28](https://github.com/narma/agwinterm/pull/28) fixed attributed restore/reflow and PTY-host reattach. [#29](https://github.com/narma/agwinterm/pull/29) fixed soft-wrap selection copying. Port their behavioral fixtures: widen/narrow/restart, preserved colors/links/spaces/wide cells, surviving host PID, partial/reverse selections, hard breaks, and rectangular selection. Do not compare to old cores where their behavior is itself incorrect.

Use `tools/vt-bench` for repeatable headless adapter measurements. It reports raw samples and honestly separates its scope from UI/GPU/PTY measurements. A complete baseline still needs worker-routed Windows/WSL visible/hidden runs, latency, idle and churn. No performance claim or default-switch decision is justified yet.

### Initial headless run, 2026-11-04

Both backends completed 54 configurations × five samples (540 samples total). Build: Release, .NET 10.0.11, Windows 10.0.26300 X64, 32 logical processors; source `b48dbb9` plus this harness. Native core was built from source with `cargo build --release --manifest-path native/Cargo.toml -p agwinterm-core --offline`, ABI 23, DLL SHA-256 `2EE716EB4834B0CAA8E5D7902D8FCE6916401C373927CDA87C3A5DA3A8D460CC`. No UI or PTY was started. Managed ran first, Rust second; these are exploratory single-process-launch results, not acceptance measurements.

For the Rust adapter's one-session sparse-update workload (four payload repetitions), median elapsed milliseconds were:

| Grid | 64-byte chunks | 4096-byte chunks |
| --- | ---: | ---: |
| 120×40 | 10.4990 | 0.3558 |
| 240×100 | 55.9424 | 1.3950 |
| 400×200 | 187.2340 | 4.4989 |

This is consistent with the source-level full-grid-copy cost scaling with both grid size and Feed frequency. It is not a Ghostty speed prediction. Managed short samples show warm-up/tiering sensitivity and sometimes zero process-CPU timer ticks; do not present precise cross-backend speedup ratios from this run. Calibrated longer measurements are still required.

Local raw reports: `artifacts/vt-managed.json`, `artifacts/vt-rust.json`; invariant-culture CSV medians: `artifacts/vt-summary.csv`. These generated files are ignored artifacts; rerun the documented commands to reproduce the experiment. Release harness build passed without warnings; native Release build passed; missing-native-library validation refused the Rust run without fallback; summary validation accepted all 108 configurations.

## Next reviewable increments

1. Finish the API gaps above, verify Windows x64 build/toolchain/license provenance, run the headless baseline and extend isolated UI measurements. Record results and limitations.
2. Add native binding/shim lifetime and ABI tests, chunked write/resize smoke tests, and opt-in new-session selection. Include enough frame integration to avoid a per-Feed full mirror from the start.
3. Migrate frame/text/grapheme consumers and input/host contracts, then graphics and versioned restore. Keep every regression gate executable.
4. Measure everyday agent/TUI workloads and agterm parity, then switch default and remove old cores when the regression gates pass. The user explicitly authorizes removal earlier when old interfaces obstruct the destination architecture; no separate permission checkpoint is required. Preserve legacy saved-data import where useful; no requirement to preserve old internal abstractions.

## Implemented Windows/native increment

- Build script, source/toolchain pin, SHA-256 provenance and source-generated `LibraryImport` bindings with `SafeHandle` ownership. The build temporarily adds a narrow native history-page prepend export, then restores the pinned checkout byte-for-byte; VT parsing and reflow remain upstream. No extra runtime is installed or shipped.
- Bounded native allocations and host-effect queues; dirty-row bulk export, UTF-8 clusters and snapshot-owned hyperlink metadata. Feed does not copy the grid. The live renderer begins capture under the session lock and converts/draws outside it. Control/text reads have separate reusable buffers and do not mutate in-flight paint frames. History painting now uses retained native viewport captures (see the continuation below).
- Pre-hold render snapshots, independent cursor coordinates and hyperlink data, a one-second hold deadline, and UI deadline redraw scheduling independent of cursor blinking. Capture abort preserves dirty state after a failed transfer.
- Native snapshots with parser continuation and tracked OSC 133 command boundaries, revision/integrity envelope, bounded decoding and atomic replacement. Native restore failure leaves the current terminal intact. Windows server replicas now use these snapshots; fresh-shell history restoration uses the separate history-only import described below.
- Selection endpoints use upstream tracked references across reflow and eviction, with immutable logical copy text checked after mutations. This matters because a partial-region deletion can clamp a tracked coordinate to different content. Common copy extraction now preserves complete clusters, soft-wrap lengths/wide padding, hard breaks, reverse endpoints and rectangles. A tracked top-row probe supplies a scroll-change epoch without assuming history count grows. Anchor release retains the allocator owner and queues pin destruction for a serialized native operation; finalizers do not wait on managed UI/session locks.
- Experimental `emulator-core = ghostty`, glyph-cluster drawing/copying, exact cluster-aware content selection, host callbacks and a narrowly scoped bounded Windows-mode/application-OSC observer. Sixel is deliberately unsupported.
- Cursor shape/blink comes from the captured native state, with live user defaults delegated to Ghostty. Host foreground/background defaults are synchronized before parsing color queries; explicit OSC overrides survive theme changes. The legacy DynamicBg presence bit preserves explicit black. The public upstream API currently cannot distinguish an override equal to the configured default; that transparency edge case remains an API gap, not a reason to add an OSC color parser.
- Windows CI job for pinned compilation and the host-free native contract harness. The workflow is added locally; a hosted CI run has not yet been observed.

Validation: Release application build without warnings; 640 existing core tests pass; 82 native contract checks pass, including chunk boundaries, snapshots, invalid package rejection, held-frame ownership, resize during hold, grapheme selection, 100 concurrent paint/write/control-read iterations, and 100 terminal lifetimes. Hyper-V fixture passes 8/8 checks with clean DLL loading, ConPTY input, alternate-screen text, clusters, hyperlinks, frame capture and return to the primary shell. Screenshot inspection confirms one composed emoji in DEC mode 2027; without that mode upstream intentionally uses legacy scalar-width behavior. The final smoke run, including native selection integration, is under `artifacts/ghostty-ui-anchors/results`. It is a launch/render/control smoke suite; detailed selection/reflow/eviction cases run in the host-free native harness, not through mouse automation.

The Ghostty ingestion benchmark completed 54 configurations × five samples. Latest raw report, including tracked scroll probes: `artifacts/vt-ghostty-current.json`; combined medians: `artifacts/vt-current-summary.csv`. The one-session sparse-update samples allocate zero managed bytes during Feed. Timings are below a millisecond and retain the warm-up/timer limitations above; do not turn them into precise speedup claims or claim renderer/latency parity.

Native graphics now use bounded Kitty storage, Windows WIC PNG decoding, generation-keyed immutable pixel captures, and Direct2D layers/crops/pixel offsets. Held and in-flight captures retain their own image generations; unchanged captures reuse pixel buffers. Animation ticks return deadlines for visible-surface redraws, with no idle polling. Pixel conversion runs in bounded background tasks. Contract tests cover exact PNG pixels, deletion/replacement during capture, held images, alternate-screen isolation, snapshot restoration and animation. Hyper-V evidence `artifacts/ghostty-ui-images/results` confirms native PNG and RGBA rendered on clean Windows (10/10 checks). Graphics history painting is covered by the continuation below; expanded placeholder/relative-placement visual acceptance remains pending.

In-process session teardown now explicitly disposes native cores. Closing during an in-flight capture rejects new work and defers native destruction until the reader finishes. Slow spawns completing after close are killed/disposed instead of publishing into a closed session. Window teardown disposes native animation timers and textures. Latest checks: 645 Core tests, 102 native contracts, 16 focused lifecycle/cleanup tests, and 10/10 Hyper-V checks in `artifacts/ghostty-ui-lifetime/results`.

Remaining replacement blockers: remaining margin metadata and expanded selection UI acceptance; complete palette/theme integration and cursor metadata for non-paint consumers; host-image lifetimes and expanded graphics placement acceptance; expanded keyboard-layout/IME acceptance; Unicode search case folding and large-history search scheduling; complete third-party notice packaging; full Windows/WSL/agterm parity and calibrated end-to-end performance checks. Old engines remain only because these consumers have not yet migrated, not to preserve a permanent multi-backend architecture.

## Keyboard, history and Windows server continuation

Windows key press/repeat/release events now use a cached upstream key encoder, synchronized from each native terminal's modes. Layout text and unshifted characters are supplied separately; dead-key composition remains Windows-owned. DEC 9001 uses the Windows input protocol. Key releases retain their original session target. Native contracts cover normal/application cursor keys, modifiers, Ctrl, Backspace modes, Kitty press/release and layout text; the owned VM drives real keydown/character/keyup messages and verifies one command without duplicate characters. Mixed old/native broadcast and expanded IME/layout acceptance remain transitional gaps.

History saves now support native cores and protobuf graphemes. Restoration builds an isolated attributed terminal and uses upstream reflow before prepending its pages to the primary history. Live modes, cursor, parser continuation, alternate screen and tracked selections stay intact. History is capped by configured rows and a 64 MiB native page budget. No shell replay or live VT-state replacement is involved in fresh-process history restoration.

The Windows headless host loads the configured Ghostty core; replicas use the same factory. Native attachment negotiates an optional protocol field, then reads one bounded, integrity-checked snapshot from the data pipe. Capturing/transferring state and registering raw output share the host's ingestion lock, including split escape sequences. Native clients retain legacy-host fallback; native hosts explicitly reject clients without snapshot support. Replica detach/disposal releases native ownership while preserving the host process on detach. Snapshot envelope construction uses one exact managed allocation instead of repeated full-payload copies.

A separate self-contained `tests/Agwinterm.GhosttyServerHarness` exercises real named pipes, bundled ConPTY, detached output, split CSI, both screens, input modes, history, resize, PID continuity and exit. Publish it into a test-only `ghostty-server-tests` subdirectory beside the test application's executable; the `Ghostty` Hyper-V suite discovers and runs it inside the owned VM. This is test tooling, not a runtime dependency or release payload.

Validation on `40066da`: Windows native and application Release builds pass; 125 native contracts pass; Hyper-V passes 13/13 UI checks including the separately launched application host, and the nested server harness passes 18/18 checks. Evidence: `artifacts/ghostty-ui-server-40066/results`. The managed suite passed 645 Core tests and 32 focused server/lifetime checks during this increment. Server acceptance exposed an adapter color-value normalization bug; indexed/default/RGB specs now use their canonical constructors, with an equality regression check. The new upstream key/action setters' results are checked. The upstream dependency/license manifest now exists; our release notice packaging still needs completing.

The upstream `test-lib-vt -Dtest-filter="C header" -Doptimize=ReleaseFast -Dtarget=x86_64-windows-gnu -j1` check also passes. The first Debug/j4 attempt failed while Zig read a standard-library file (`Unexpected`), before executing tests; the production-configuration retry succeeded. Mouse reporting now reads native dimensions directly under the session lock instead of materializing the entire managed query grid after output. Application Release build and the 10 existing mouse-coordinate/encoding tests pass after that optimization. Mouse protocol encoding subsequently migrated to the upstream encoder, as described below.

## Mouse, paste, history graphics and grapheme parity (2026-11-05)

Mouse events now use a cached upstream encoder/event pair and native terminal modes, including X10, button motion, SGR and pixel reporting. Pointer coordinates are mapped into the pane's terminal geometry before encoding. Text paste uses the upstream sanitizer and bracketed-paste encoder while retaining the application's confirmation policy. Neither path materializes a managed screen grid. Kitty clipboard read grants and multi-MIME exchange remain outside this text-paste integration.

History painting now captures the native viewport, including image generations and hyperlinks, then restores the live viewport before releasing the session lock. Conversion/drawing remain outside the lock. In-flight captures survive image deletion. Browsing history during synchronized output uses current history; the live view still honors its retained pre-hold frame. A public explicit-viewport capture API could avoid the temporary viewport mutation and redundant dirtying; measure that cost before proposing an ABI extension.

Find uses native search/grid references and expands wide endpoints for rendering. Matches can cross soft wraps and include complete combining/emoji clusters. Search currently uses upstream ASCII-only case folding and synchronous execution under the session lock: Unicode folding and incremental scheduling are still gaps. UI Automation maps UTF-16 offsets to cell columns and navigates complete graphemes. Cluster layouts share the bounded glyph-layout cache rather than being rebuilt every paint.

Host-free validation: 152 native contract checks, 647 Core tests, and GlyphBench's 9 lifetime/key, 3 cluster-cache and 48 ink-containment checks pass. The isolated Clipboard suite passes 10/10 checks, including Kitty Ctrl+V/Ctrl+Shift+V and a single bracketed-paste packet. The broader isolated run passed Accessibility 16/16 and native server 18/18. Evidence is in ignored artifacts directories `ghostty-ui-clipboard`, `ghostty-ui-parity` and `ghostty-glyph-bench.json`. Detailed final rendering results are recorded below. These are regression checks and exploratory timings, not complete agterm/WSL acceptance or a default-switch gate.

Final isolated run `artifacts/ghostty-ui-final-parity/results`: TerminalRendering 20/20, Ghostty UI 19/19, nested server 18/18, and all six render timing scenarios completed. The common renderer needed additional ink-fit padding for color-font raster edges; actual screenshots now preserve the lower contours with ligatures enabled and disabled. Repeated draws, cache eviction and font reset retain identical pixels. Private test configuration selects an installed font before launch so transient missing-font notifications cannot contaminate pixel comparisons.

Full-window render timings include EndDraw on a 104x38 grid in the prepared two-vCPU VM. With ligatures enabled/disabled, medians were ASCII 2.35/1.99 ms, sparse icons 5.46/3.83 ms and dense color emoji/CJK 461.82/458.27 ms. The dense workload is a significant unresolved rendering cost, not a passing performance acceptance gate. Raw samples and p95 values are retained in the run's `terminal-performance.json`. These VM timings do not measure physical-GPU performance, input latency, WSL traffic or native parser throughput.

Same-build managed-core control (`artifacts/ghostty-perf-managed-control/results`) also completed all six scenarios. With ligatures enabled/disabled its medians were ASCII 4.58/2.01 ms, sparse icons 5.13/4.26 ms and dense emoji/CJK 605.86/613.02 ms. This reproduces the dense-screen cost without Ghostty and does not indicate a Ghostty regression in these runs. Separate VM launches and legacy Unicode width behavior prevent treating the difference as a calibrated speedup. Next performance work should profile the common draw/EndDraw path and evaluate bounded raster reuse, with the existing pixel and cache-lifetime checks retained.

The follow-up [emoji performance investigation](../performance-emoji-2026-11-05.md) compares Ghostty with the actual Rust core on one mixed Latin/Russian screen containing 0/4/16/64 emoji. It isolates the expensive color-font draw calls and adds bounded, device-bound raster reuse. Pixel checks and both engines' isolated rendering suites pass. Warm dense-frame cost drops from roughly 450–461 ms to a few milliseconds in this VM; the linked report records repeated measurements, transient-toast control and limitations. This resolves that specific warm-render hotspot, not the remaining end-to-end/WSL performance gates.

## Stage 1: theme and live metadata integration (2026-11-05)

Ghostty paint now uses native resolved colors instead of resolving indices a
second time against the UI theme. The host provides palette/cursor defaults;
OSC 4/10/11/12 overrides remain native-owned and resets use the latest defaults.
Theme changes refresh explicit screen/history reads even without new output.
In-flight paints retain their captured palette and cursor colors. New .NET
hosted sessions receive the initial palette and cursor in their environment,
including before a UI attaches. Live theme changes and reattachment now also update the surviving .NET host over a
capability-negotiated control request with incarnation checks. Updates are coalesced
off the UI thread and preserve parser continuation and OSC overrides; older hosts
receive no unknown request.

The narrow pinned-source extension exposes live cursor shape/blinking, vertical
scroll margins and explicit background-override presence. No parser, reflow or
color policy was added to the extension. Non-paint consumers no longer receive
cursor/margin placeholders; explicit black matching the default is retained.
Adapter ABI is now 2, with matching provenance/layout checks rejecting old pairs.

177 native contracts pass, including repeated selection and command-boundary
reflow at widths 20/8/40/5/80, metadata after snapshot restore, OSC reset and
in-flight color ownership. Core tests pass 648/648. Hyper-V Ghostty passes 24/24 with 32 nested server checks,
including actual OSC palette pixels, live theme switching and reset to the new
palette. Evidence: artifacts/hyperv-ghostty-stage1/results and
artifacts/ghostty-stage1-contract.log. This does not claim stages 2/3 or full
Windows/WSL/agterm parity, and does not remove legacy engines.

Final server evidence: artifacts/hyperv-ghostty-stage1-server-final/results.
The first server run passed new theme checks but timed out awaiting the legacy
Rust marker; the diagnostic rerun passed unchanged Rust assertions. Cause is not
confirmed. Native theme/parser/snapshot checks passed in both runs.

Review follow-up: reattachment sends the theme after PaneHost assignment, and
live theme updates enumerate hidden splits, scratch, quick and overlay terminals
without duplicate updates. A host-free UI ownership regression passes; Core is
649/649. The renderer suite passes 20/20 and performance fixture 96/96. Across
three rounds, mixed Latin/Russian + four emoji medians are 2.48–2.84 ms and dense
medians 3.09–3.25 ms in the prepared VM. These are VM render timings, not an input
latency or physical GPU claim. First render runner launch rejected an unready
VM desktop before application work; normal worker retry passed.
