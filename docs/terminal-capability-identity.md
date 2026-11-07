# Terminal identity and capability reports

Agwinterm embeds libghostty-vt 1.0.0, not the Ghostty application. The renderer,
input handling, clipboard policy and transport determine which engine features
are available end to end.

| Route | Default TERM for newly started applications | Overrides and ownership |
| --- | --- | --- |
| Windows, in-process or hosted | `xterm-256color` | Explicit profile/custom environment wins; a parent's unrelated TERM is replaced. |
| WSL native PTY, in-process or hosted | `xterm-256color` through WSLENV | Linux profile environment is applied at exec, including explicit TERM. The helper also defaults a missing/empty TERM. |
| Owned WSL ZMX workload | `xterm-256color` | Explicit profile TERM wins. ZMX's temporary attachment uses its own compatible TERM, and workload exec reapplies the profile environment. |
| SSH attachment | `xterm-256color` on the SSH client | OpenSSH sends this in the PTY request. Read-only discovery allocates no PTY and sets no TERM. |
| Existing remote/ZMX workload | Original workload environment | Attaching cannot rewrite an existing process environment. Multiplexers may advertise their own TERM. |
| New origin overlay job | `xterm-256color` unless configured | Environment travels in the origin-job request. |

New locally owned applications also receive `TERM_PROGRAM=agwinterm`, the
application's `TERM_PROGRAM_VERSION`, and `COLORTERM=truecolor`. OpenSSH does not
automatically forward those variables: unlike TERM, they require environment
forwarding or an explicit remote launch environment. No SSH server configuration
or remote user's terminfo database is modified.

`xterm-256color` is a compatibility baseline, not a claim of complete xterm
emulation. It uses an existing ncurses entry; no `xterm-ghostty` entry is installed
or required. Minimal distributions still need their normal terminfo package.
Applications can detect extended features through runtime queries.

## Runtime replies

- XTVERSION reports `Agwinterm (libghostty-vt 1.0.0)`.
- DA1 is `CSI ? 62 ; 22 c`: VT220-level color support. DA2 is `CSI > 1 ; 0 ; 0 c`;
  DA3 has zero unit ID. These conservative library defaults do not claim Sixel,
  printer support, or clipboard access.
- XTGETTCAP `TN` remains unanswered. A terminal-wide name cannot accurately
  describe explicit profile overrides, nested multiplexers and pre-existing
  remote workloads. In particular the adapter must not return `xterm-ghostty`.
- Native XTGETTCAP replies for colors/truecolor, bracketed paste, OSC 52,
  synchronized output, mouse/focus and keyboard protocols remain available.
  Clipboard capability does not grant permission to read the clipboard.
- The adapter advertises `Su`, `Smulx`, `Setulc`, `Smol` and `Rmol`. Cells retain
  single/double/curly/dotted/dashed underline styles, resolved underline colors
  and their default/indexed/RGB sources, and an independent overline flag.
  Local and hosted renderers draw these decorations even on blank cells.
  SGR 59 restores foreground-colored underlining; SGR 24 and 55 independently
  remove underline and overline. OSC 8 UI underlining uses the text foreground.
- `blink` is advertised. SGR 5 and 6 use the same native blink attribute and
  toggle the text and its decorations every 500 ms; SGR 25 and SGR 0 clear it.
  Backgrounds and nonblinking text remain visible. The window's text timer is
  independent of cursor settings and only runs while rendered cells need it;
  it stops when the window is minimized. Animation needs neither PTY output nor
  repeated hosted frames and never alters selected/copied text or stored cells.

The native adapter ABI is 10. Hosted presentation revision 4 carries decoration and blink
data in full and delta frames; peers require an exact revision match and reject
older frames rather than silently losing formatting. GUI and host must be
updated together. Existing disk buffers remain readable: the added protobuf
fields default to legacy plain underlining. New history buffers retain color
sources and shapes when imported at a different width.

XTGETTCAP replies now pass directly through from the native engine; no renderer
capability filter remains. Library upgrades must rerun the multi-key query contracts.

## Verification

The native harness pins DA replies, absent TN, blink/decoration reports,
palette changes, immutable frames and decorated history import. Windows Ghostty UI acceptance verifies child
environment, reply delivery, decoration pixels and idle blink/reset phases in both owner modes. `wsl-capabilities.ps1` runs
through the private-desktop WSL worker, verifies `infocmp`/`tput`, default and
overridden TERM, and repeats queries through an isolated loopback OpenSSH PTY
with temporary keys and strict host verification. It requires already installed
Bash, ncurses tools and OpenSSH server; it installs no runtime dependencies.
The loopback test validates SSH transport, not an arbitrary external server's
configuration or a pre-existing remote application's environment.
