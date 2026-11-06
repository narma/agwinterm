# Color emoji rendering: Ghostty and Rust, 2026-11-05

The regression was in the shared Windows renderer, not the VT engine. Caching
DirectWrite layouts avoided shaping/layout work but still called `DrawTextLayout`
with color-font processing for every fallback glyph on every repaint. Timings
inside those calls account for almost all of the dense-screen frame time.

## Workloads and measurement

The prepared Hyper-V guest uses two virtual CPUs and 4 GiB RAM. The viewport is
104 columns by 38 rows, Consolas 14, cursor blinking disabled. Runs are sequential,
with one application window, through the installed worker. Both ligature modes
are measured. The fixture verifies the actual emulator type in render diagnostics;
a missing Rust/Ghostty library and silent managed fallback fail the benchmark.

Each `mixed-*` workload is **one screen containing Latin and Russian text together**:
`build output Сборка проекта: успешно abcdef Привет мир 0123456789`.
Exactly 0, 4, 16 or 64 emoji (`🔴 🔵 😀 🚀`) replace cells at distributed positions.
Readback verifies the mixed text and exact emoji count. Separate ASCII-only and
dense emoji/CJK screens serve as controls; dense contains 1,824 fitted glyphs.

`Render()` timing includes `EndDraw`, and separately logs the body, `EndDraw`,
fitted-glyph calls, and raster hits/misses. Each scenario warms 20 frames and
measures at least 100 synchronous repaint frames. Final runs wait seven seconds
for the experimental-core startup toast to expire, then repeat three rounds
with alternating scenario/ligature order. Raw samples are retained, including p95.
These measure warm whole-window repaint, not cold first appearance, PTY ingestion,
input latency, scrolling throughput or performance on a physical GPU.

Ghostty is pinned to `40066da055311c991b5c45be67313ef2b3661539`.
The Rust DLL SHA-256 is
`2EE716EB4834B0CAA8E5D7902D8FCE6916401C373927CDA87C3A5DA3A8D460CC`.

## Diagnosis before raster reuse

Single-round baseline medians, milliseconds per frame (ligatures off / on):

| Workload | Ghostty | Rust |
| --- | ---: | ---: |
| ASCII | 2.04 / 2.43 | 2.07 / 2.27 |
| Mixed, 0 emoji | 2.61 / 4.92 | 2.71 / 4.01 |
| Mixed, 4 emoji | 5.17 / 7.37 | 5.57 / 6.37 |
| Mixed, 16 emoji | 11.41 / 12.06 | 11.04 / 11.93 |
| Mixed, 64 emoji | 37.04 / 37.01 | 36.07 / 38.13 |
| Dense emoji/CJK | 459.91 / 460.94 | 453.62 / 450.57 |

Four emoji account for approximately 2 ms in the glyph draw calls; 64 account
for 32–34 ms. In the dense workload, these calls account for about 449–459 ms,
while `EndDraw` is around a millisecond. Both engines reproduce the problem.
The initial baseline did not wait out the six-second experimental-core toast;
early ligatures-on whole-frame values include that transient overlay. Later
ligatures-off scenarios and the separately timed glyph calls establish the cost.
Millisecond-scale whole-frame differences between separate VM launches are noisy;
this experiment does not establish a precise speed ranking of the VT engines.

## Change and correctness

`SoloGlyphBitmapCache` retains device-compatible, premultiplied raster images for
repeated fitted glyphs, including complete grapheme clusters. Keys include layout
identity, foreground color, geometry, DPI and exact subpixel phase. Foreground is
necessary even for color fonts because a glyph may contain foreground-colored
layers. Bitmaps draw at physical pixel boundaries without resampling. The first
occurrence uses the direct path; only repeated keys allocate an offscreen surface.
This avoids allocating a surface for every unique glyph in a stream.

Each window retains at most 256 rasters / 16 MiB of bitmap pixels, with bounded
admission tracking. Font reset, render-target recreation and window destruction
release the cache. Bitmap ownership uses an independent COM reference, not the
scratch target's borrowed wrapper. Layouts remain device-independent.

Host-free WIC tests compare direct drawing, raster creation and reuse at 96, 120,
144 and 192 DPI, including fractional coordinates, emoji, composed graphemes,
CJK and foreground-colored symbols. All 144 cases match exactly in this run
(the assertion allows at most two channel levels for alpha-composition rounding).
Entry-limit eviction, byte-budget eviction, one-off admission and reset are tested.
All 647 Core tests pass. Both actual-window rendering suites and final timing
results are recorded below.

## Reproduction and evidence

Build the Release application with the pinned Ghostty package, and place the
existing Release Rust `agwinterm_core.dll` beside it. Run sequentially for
`EmulatorCore='ghostty'` and `'rust'`:

```powershell
./tests/integration/hyperv-ui.ps1 -Suite TerminalRendering,TerminalPerformance `
  -TestParameters @{EmulatorCore='ghostty'} `
  -Exe ./src/Agwinterm.Win32/bin/Release/net10.0-windows/win-x64/Agwinterm.Win32.exe `
  -OutputDirectory ./artifacts/emoji-check-ghostty
```

The offscreen checks run with `dotnet run --project tests/Agwinterm.GlyphBench -c Release -- --raster-only`.
Ignored evidence directories: `artifacts/emoji-baseline-{ghostty,rust}`,
`artifacts/emoji-cached-{ghostty,rust}`; the repeated final measurements are recorded
below. No user's desktop, clipboard or installed app configuration is used.

## Final repeated results

Pooled warm-frame median / p95 in milliseconds, at least 300 samples per cell across three alternating rounds. Both final runs wait out the startup toast.

| Workload | Ligatures | Ghostty median / p95 | Rust median / p95 |
| --- | --- | ---: | ---: |
| ascii | true | 1.46 / 3.87 | 1.42 / 2.54 |
| ascii | false | 2.28 / 3.75 | 2.16 / 4.17 |
| mixed-0 | true | 2.68 / 5.43 | 2.61 / 11.93 |
| mixed-0 | false | 2.78 / 3.82 | 2.58 / 3.49 |
| mixed-4 | true | 2.68 / 6.17 | 2.72 / 4.9 |
| mixed-4 | false | 2.69 / 4.06 | 2.72 / 3.91 |
| mixed-16 | true | 2.76 / 4.53 | 2.87 / 4.23 |
| mixed-16 | false | 2.96 / 4.11 | 2.8 / 3.91 |
| mixed-64 | true | 3.25 / 8.79 | 3.48 / 7.33 |
| mixed-64 | false | 3.41 / 4.59 | 3.23 / 4.44 |
| dense | true | 3.73 / 7.26 | 3.3 / 4.66 |
| dense | false | 3.27 / 4.26 | 3.06 / 3.99 |

At four emoji the shared renderer now spends about 0.01 ms in the fitted-glyph calls, versus about 2 ms before. Every measured round has a median of zero raster misses for each scenario. Cold first appearances and workloads with continually unique glyphs still use direct rendering; no cold-latency improvement is claimed. VM scheduling/desktop work still produces tail outliers, so the sub-millisecond differences between engines are not a meaningful performance ranking.

Validation: 20/20 actual-window rendering checks for each core; 96/96 final performance-fixture checks for each core (mixed-text/count assertions, sample availability, actual backend); 144 offscreen pixel comparisons plus cache lifetime/budget/admission checks; 647 Core tests. Final evidence: `artifacts/emoji-final-ghostty/results`, `artifacts/emoji-final-rust-quiet/results`, and `artifacts/emoji-final-summary.csv`. The earlier `emoji-final-rust` run is provisional because it predates the startup-toast wait.
