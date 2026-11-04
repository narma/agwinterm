# Ghostty VT Windows adapter

This is an experimental opt-in integration, not the default terminal core.
The upstream source is pinned by `pin.json`. The build adds the narrow
`history.zig` embedding export to prepend existing native pages; it does not
replace upstream parsing, reflow or page ownership. The source checkout is
restored byte-for-byte after building, and provenance records the extension hash.
The build uses
Zig's Windows GNU target so the DLLs do not require a separately installed
Visual C++ redistributable. Zig is build tooling, not a runtime dependency.

From the repository root:

```powershell
./native/ghostty-vt/build.ps1
dotnet run --project tests/Agwinterm.GhosttyHarness -c Release -- artifacts/ghostty-build/bin
dotnet build src/Agwinterm.Win32/Agwinterm.Win32.csproj -c Release
./tests/integration/hyperv-ui.ps1 -Suite Ghostty -Exe ./src/Agwinterm.Win32/bin/Release/net10.0-windows/win-x64/Agwinterm.Win32.exe
```

Use `-SkipDownload` once the pinned sources/toolchain are available. Local
upstream modifications are rejected. The build records source/toolchain IDs,
targets, and both DLL hashes in `artifacts/ghostty-build/provenance.json`.
Build after generating the DLLs to copy them alongside the application;
`emulator-core = ghostty` selects the experimental core for new sessions.

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
terminal and commits only after validation. Windows server sessions transfer this
snapshot on their data pipe before registering the live output stream, under the
same lock as VT ingestion. New replicas preserve both screens, parser continuation,
images and input modes without a resize repaint. Native hosts require snapshot-aware
clients; native clients can still attach to older hosts using the legacy path.
Application history persistence uses the existing attributed protobuf
format (now including graphemes). Import parses inert attributed text into a
separate Ghostty terminal, lets Ghostty reflow it, then prepends native pages.
The live screen, modes, parser continuation and tracked positions stay intact.
History has a 64 MiB byte budget in addition to its configured line limit.

The application adapter still has migration gaps: remaining margin metadata,
complete palette/theme integration, graphics scrollback painting and host-image lifetimes,
mouse/paste encoders and expanded keyboard-layout/IME acceptance. Windows keyboard
events already use the upstream key encoder; DEC 9001 remains the Windows-specific path.
Live renderer conversion runs outside the session lock; history painting still
uses the locked explicit read path. Do not enable this core by default or remove the old cores
until their required consumers have migrated (obsolete interfaces can be removed sooner). Sixel will not be implemented in this
backend. See the migration audit for the complete contract map.

The Windows CI contract job builds the pinned source and runs the host-free
harness. Local desktop checks must use the existing Hyper-V worker. License
copies emitted here cover Ghostty and Zig; release packaging still needs the
complete third-party notice inventory before distributing this integration.
