# Ghostty VT Windows adapter

The [single-owner contract](../../docs/single-owner-contracts.md) explains which state belongs to the terminal host and which operations the GUI delegates to it. See [installer layout and migration](../../installer/README.md) when changing the native payload.

Windows sessions use Ghostty for terminal state. Installed configurations that previously selected Managed or Rust migrate to Ghostty. Hosted sessions keep their terminal in the application host and send viewport frames to the UI.

The Windows adapter pins libghostty-vt **1.0.0** (`vt-1.0.0`, commit `0fa3d577a5614150ff8e2c152de5fb9d5c6d2dec`) with Zig 0.17.0. This is the first stable C ABI release; upstream promises compatibility across 1.x. Snapshot bytes are versioned separately and are not covered by that promise. Our history and Unicode search extensions still build against this release. See the [official documentation](https://lib.ghostty.org/vt/) and [upstream changes](https://github.com/ghostty-org/ghostty/compare/2c76f29972dad21d3f2cc0efde2517a6fa8fed56...vt-1.0.0).

The build applies the history import and Unicode search extensions to the pinned checkout and records their hashes. Zig builds the Windows GNU target. The shipped DLLs load with the Windows inbox CRT.

From the repository root:

```powershell
./native/ghostty-vt/build.ps1
dotnet run --project tests/Agwinterm.GhosttyHarness -c Release -- artifacts/ghostty-build/bin
dotnet build src/Agwinterm.Win32/Agwinterm.Win32.csproj -c Release
./tests/integration/hyperv-ui.ps1 -Suite Ghostty -Exe ./src/Agwinterm.Win32/bin/x64/Release/net10.0-windows/win-x64/Agwinterm.Win32.exe
```

Use `-SkipDownload` once the pinned sources/toolchain are available. Local
upstream modifications are rejected. The build records source/toolchain IDs,
targets, and both DLL hashes in `artifacts/ghostty-build/provenance.json`.
Build the application to stage the verified DLLs beside its executable.

`agwvt.c` owns terminals, render states, tracked command marks, and bounded
allocations. Feed never exports a grid. Capture begins under the terminal's
external lock and finishes using snapshot-owned data. Linked cells retain
their own URI bytes, so concurrent writes cannot change an older frame.
Only one capture consumer is allowed. Managed disposal rejects new work and
defers native destruction until an in-flight capture finishes; callers serialize
disposal with terminal mutations using the session lock. In-process session
teardown disposes its core explicitly, including failed/late process starts.
Callbacks copy bounded host effects into a managed queue; they never reenter
the terminal or throw across the C boundary.

Clipboard requests are the synchronous exception: OSC 52/5522 use a bounded
MIME exchange and reply before the native callback returns. Hosted sessions use
correlated duplex effects, independent of frame credit and terminal query locks.
Reads and MIME listings require `clipboard-read=true`; writes honor
`clipboard-write`. See [terminal clipboard protocols](../../docs/terminal-clipboard.md)
for Windows format mappings, limits, cancellation and timeout behavior.

Kitty direct payload storage is capped at 64 MiB per screen (within the shared
256 MiB terminal allocation budget). PNG decoding uses Windows WIC, with at most
16 million decoded pixels, and returns RGBA using Ghostty's supplied allocator.
File, temporary-file and shared-memory media remain disabled. Graphics captures
retain generation-keyed pixels independently of live storage, including held
and in-flight frames. Unchanged pixels are copied only once into each native
and managed cache. Placements retain native layers, z-order, crops and offsets.
Direct2D draws all three native layers with viewport clipping; premultiplication
runs on bounded background tasks. Animations advance once per capture and use
Ghostty's next-frame deadline, with no polling for static/stopped images.

Native snapshots retain parser continuation and tracked command boundaries.
The managed envelope includes the upstream revision, bounded Windows input
sidecar state, lengths, and a SHA-256 integrity check. Decode builds a separate
terminal and commits only after validation. Hosted panes reconnect to the surviving owner and receive its current viewport. Input, queries and frame acknowledgements use the presentation protocol.
Application history persistence uses the existing attributed protobuf
format (now including graphemes). Import parses inert attributed text into a
separate Ghostty terminal, lets Ghostty reflow it, then prepends native pages.
The live screen, modes, parser continuation and tracked positions stay intact.
History has a 64 MiB byte budget in addition to its configured line limit.

Mouse and text paste use upstream encoders, including native mode selection,
bracketed paste and control-character sanitization. Windows keyboard events use
the upstream key encoder; DEC 9001 remains the Windows-specific path.
The host theme answers CSI ? 996 n and scheme changes notify mode 2031 subscribers,
including idle local and hosted sessions. Application OSC background overrides do
not change the reported host scheme. Size queries use the pane and cell geometry;
XTVERSION identifies Agwinterm and its embedded library release.
Cell, word, logical-line and rectangular drags use native gesture bounds with a
tracked press origin. Windows hit testing, click counts and autoscroll remain in
the UI; hosted gestures execute in the terminal owner. Ordinary copy uses the
native plain formatter with Windows line endings and physical rows for rectangles.
Immutable text, formatted snapshots and saved-frame fallback remain independent
of live terminal state. The `copy_selection_html` and `copy_selection_vt` actions
use snapshots captured by the native formatter at selection time (100,000 bytes
per formatted representation); HTML includes a plain-text clipboard
fallback. For example, `map ctrl+alt+c = copy_selection_html` enables formatted
copy without changing ordinary copy shortcuts. Hosted selection replies carry
formatted snapshots to the GUI; `export_selection_text/html/vt` saves them locally
through the Windows save dialog, even after the owner evicts the selected rows.
History captures include native graphics and hyperlinks. Capture temporarily
selects the requested viewport under the session lock and restores the live
viewport before returning; conversion and drawing run outside the lock.
Search uses native screen coordinates (including wide cells and soft wraps).
The pinned search patch adds full Unicode folding using Ghostty's existing uucode tables, preserves original cell coordinates, and retains feed/tick progress on a worker. No managed text matcher or separately installed runtime is used. Run build.ps1 -TestSearch to include upstream search tests in Debug mode.
UI Automation maps UTF-16 ranges to complete graphemes and terminal columns.

The adapter renders Kitty graphics. Sixel remains outside the supported graphics formats.

The Windows CI contract job builds the pinned source and runs the host-free
harness. Local desktop checks must use the existing Hyper-V worker. License
copies emitted by `build.ps1` cover Ghostty and Zig. Release entry points use
`stage.ps1`, which collects the actual linked-package licenses, pinned uucode
notices excluded by Zig package filtering, and the toolchain runtime notices.
Installer and portable outputs include `ghostty-NOTICES.txt`. The portable
toolset also embeds the DLL pair, provenance and bundled ConPTY; its content-
addressed extraction is shared by the GUI and the .NET session host.

CI caches the pinned Zig toolchain, Ghostty's local compiler cache and downloaded
packages, and an explicit global Zig cache under artifacts/ghostty-zig-global.
The compiler cache key includes the runner platform, pin, and native build inputs;
changes within the same pin can reuse dependency outputs through a restore prefix.
Source preparation runs before compiler-cache restoration so a restored cache
directory does not prevent a fresh git clone. Compiler caches are best-effort:
Windows runner changes can still make Zig recompile restored outputs.

An exact-input native bundle cache therefore also stores the DLLs, provenance,
licenses, and Debug search test executable. Its key includes platform, pin, native
sources/headers, extensions/patches, build scripts and workflow configuration,
with no fallback prefix. An exact hit skips only native compilation. Debug search
tests and the current C# native contract harness always execute; test success is
never cached. On a miss, CI uses -BuildSearchTests to produce the executable and
then runs it in the same unconditional test step as a cache hit.

With -TestSearch, both builds use library-only mode. The Debug C API test binary
includes the Zig module tests (as documented in upstream build.zig), so it is
compiled once via test-lib-vt-build and executed directly on every invocation.
This avoids application-only dependencies, duplicate module test builds, and
reuse of cached test-run results. Build summaries and phase timings distinguish
compilation from actual test execution in CI logs.
