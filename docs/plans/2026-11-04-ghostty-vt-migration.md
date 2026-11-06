# Ghostty VT migration — PR #35 acceptance

Updated: 2026-11-05. Tracking: #34. This document describes the current PR,
not the superseded sequence of implementation milestones. Historical audits and
intermediate measurements remain in Git history and the linked performance reports.

## Agreed scope

Ghostty is the default when `emulator-core` is absent. Explicit Managed/Rust
choices remain available. Both legacy engines stay in this PR while Ghostty matures.
The eventual destination remains a single libghostty-vt backend.

This PR covers integration, native search, input/graphics/history acceptance and
selected Windows/WSL/performance checks. It does not claim complete agterm parity
or full replacement readiness. Further mixed-core physical-key broadcast work,
legacy-engine removal and a comprehensive soak/performance matrix are deferred.
Sixel is explicitly excluded.

## Ownership and native contract

- Pinned Ghostty: `f5a1a0fedd8762d490bc2951dc4b63a0f1b030e0`; pinned Zig 0.17.0;
  Windows x64 GNU target, ReleaseFast shipped library, adapter ABI 5.
- Ghostty owns VT parsing, Unicode widths/graphemes, reflow, native tracked positions,
  keyboard/mouse/paste encoding, Kitty images and snapshots. The adapter does not
  implement a second general VT parser. Windows-specific mode observation remains
  narrowly bounded.
- Source-generated bindings, SafeHandle ownership, verified ABI/provenance and
  bulk changed-row export avoid per-cell interop. Feed does not copy the full grid.
  Frames retain owned data across concurrent output; control reads use a separate
  capture path. Host callbacks never let managed exceptions cross the native ABI.
- Palette/default/cursor metadata, OSC overrides/resets, theme changes, hyperlinks,
  accessibility and copied text preserve native semantics and complete graphemes.
- Search uses a pinned native Unicode full-case-fold extension with feed/tick,
  owned worker snapshots, cancellation and bounded results. The C# matcher is gone.
  Normalization and locale-specific casing are not implemented.
- Fresh-process history restore imports attributed history without replaying it as
  shell input or replacing the new live terminal state. Hosted reattachment uses
  authoritative snapshots. Native prepend preserves live modes and tracked anchors.
- Native graphics use generation-keyed pixel caches, bounded conversion workers,
  animation deadlines, history captures and Direct2D layers/crops. Unicode
  placeholders are resolved by Ghostty and are not painted as missing glyphs.

Upstream API changes are linked from the adapter-update commit to
[15075](https://github.com/ghostty-org/ghostty/pull/15075) and
[15074](https://github.com/ghostty-org/ghostty/pull/15074).

## Session hosting and rollout

`session-host = server-rust` is retained as a user preference. New ordinary
Ghostty sessions use the native-capable .NET host; explicit legacy cores keep the
selected host. Ghostty uses bundled ConPTY to prevent Inbox resize repaint from
corrupting native reflow. Existing shells keep their authoritative core when adopted.
Ordinary restore also probes a surviving Rust host without spawning another one.

New WSL/ZMX and remote live sessions use Ghostty and the native-capable .NET host
when Ghostty is selected, including when it is the implicit default. Live-profile
enablement still recognizes the configured `server-rust` preference; it does not
force a legacy core. Explicit legacy cores retain the Rust transport.

Saved bindings first adopt their exact surviving transport ticket in either host
namespace, including after switching the configured core in either direction.
A legacy Rust host is probed without spawning one; its existing Managed replica is retained for
that attachment so upgrading does not lose the running process. After host loss,
existing-only daemon recovery uses the currently selected core, hence Ghostty by
default. No claim is made that the Rust host supports native Ghostty snapshots.
Ghostty consumes live-role titles through native callbacks, preserving every title
in a feed batch instead of reading only the last title after the batch completes.

Existing-only recovery, creation tickets, incarnation checks, no command/buffer
replay, and deliberate fresh-start rules remain intact. Remote presentation jobs
can use the ordinary host independently of the imported base terminal's transport.
No extra runtime dependency is introduced on Windows, WSL or remote hosts.

## Completed acceptance

| Area | Evidence |
| --- | --- |
| Configuration and core | 651 Core tests; implicit Ghostty and explicit legacy preservation |
| Native ABI/state | 220 native contracts; malformed package rejection, lifetime/concurrency, input, graphemes, selection, search, images, snapshots, batched role-title filtering |
| Upstream search patch | 132 Debug tests passed, 3 existing skips; shipped DLL remains ReleaseFast |
| Expanded Windows GUI | `artifacts/hyperv-ghostty-reflow-fixed/results`: 78 checks plus 32 server contracts |
| Selection and graphics | Soft-wrap mouse copy through four widths; relative placements, parent deletion, virtual image pixels, snapshot/history graphics |
| Keyboard | F2 routing, AltGr/dead keys, repeat/release ownership, focus, Win32 input and mixed-broadcast duplicate suppression |
| Installed IME | `artifacts/hyperv-ghostty-ime-final/results`: real Pinyin commit/cancel, positioning, resize/refocus, native-core proof; bare portable also passed |
| Restart/upgrade | `artifacts/hyperv-ghostty-restore-final/results`: history reflow through two restarts and adoption of a surviving ordinary Rust shell with its memory intact |
| Live routing contracts | 410 focused Pty tests: core/host selection, WSL/remote binding validation, creation tickets, restore rules and stopped-transport lifetime |
| Native WSL/ZMX lifecycle | `artifacts/wsl-ui/ghostty-live-termination-fenced`: real Ghostty Unicode-search proof, UI quit/crash, host loss plus 60-second idle interval, same Linux incarnation, existing-only retry, role changes, termination/fresh start after delayed exit callbacks, completion tokens, multi-window/layout controls |
| Existing WSL upgrade | `artifacts/wsl-ui/ghostty-live-native-upgrade`: surviving Rust transport adopted after removing explicit core; same workload reconnects through Ghostty after host loss; full lifecycle passes |
| Saved remote transport upgrade | `artifacts/hyperv-ghostty-remote-both-hosts/results`: 20 checks; real hosted child and exact ticket survive a structurally valid saved remote binding, core changes in both directions and ordinary-host setting changes; no saved command/buffer replay; abandoned transport reaped while restored child survives |
| Packaging | Installer/portable include native DLLs, hashes, dependency/runtime notices and bundled ConPTY; standalone extraction hashes and CLI startup passed |
| Prior aggregate gate | Commit `9efb990`: full GitHub CI green; Greptile review 38682286 clean, 5/5 |

These remote transport checks use synthetic origin metadata and do not establish
real SSH/origin recovery. The configured macOS fixture was unreachable (`No route
to host`) during this follow-up; the existing end-to-end remote suite remains the
gate for repeating that environment-dependent scenario. The WSL distribution
shutdown case also remains unverified without a dedicated disposable distribution;
the user's working distribution is not shut down by acceptance tests.

## Performance evidence and limits

[Emoji rendering measurements](../performance-emoji-2026-11-05.md) compare the actual
Ghostty and Rust cores on one mixed Latin/Russian screen with 0/4/16/64 emoji.
Bounded device-dependent glyph raster reuse resolves the measured warm color-emoji
hotspot. Rendering suites and 144 offscreen pixel comparisons passed. These results
do not establish all-workload latency or end-to-end superiority.

[WSL htop and resize measurements](../performance-htop-2026-11-05.md) cover PTY burst
draining and bundled ConPTY. The listing fixture preserved 28 rows and one prompt
through 20 resizes. Thirty-second htop runs showed no blank first row or displaced
selection: 351 Ghostty frames and 348 Rust/bundled comparison frames. Legacy Rust
live-grid resize is not claimed fixed.

`tools/vt-bench` provides reproducible headless samples. Very short samples are
sensitive to warm-up and timer resolution; they are not precise UI speedup ratios.
Full visible/hidden-session, idle, churn, latency and sustained-output measurements
remain part of the later replacement decision.

Native CI caches exact-input binaries, compiler outputs and dependencies; it never
caches test success. Measured successful bundle-cold run: 12m58s; warm run: 6m11s,
versus the earlier 22m44s baseline. Runner speed varies and changed inputs rebuild.

## Before merge / later work

Before merge: require clean checks/review for the final commit. User smoke testing of the
preview is useful but does not substitute for automated regression checks.

After this PR: let Ghostty mature; collect remaining agterm parity and end-to-end
performance evidence; then remove legacy engines/interfaces. Do not expand the
transitional mixed-core architecture in this PR.

Upstream feedback is local-only in `artifacts/ghostty-upstream-feedback.txt`.
It covers search/prepend API experience, Windows builds and dependency notices.
No upstream issue has been opened; publishing requires the user's review.
