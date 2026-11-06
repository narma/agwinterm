# Agwinterm — narma's Windows terminal for AI coding agents

[![CI](https://github.com/narma/agwinterm/actions/workflows/ci.yml/badge.svg?branch=narma)](https://github.com/narma/agwinterm/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/narma/agwinterm?sort=semver)](https://github.com/narma/agwinterm/releases)
[![Downloads](https://img.shields.io/github/downloads/narma/agwinterm/total.svg)](https://github.com/narma/agwinterm/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**[Releases](https://github.com/narma/agwinterm/releases)** · [User guide](docs/user-guide.md) · [Control API](docs/control-api.md) · [agliteterm](https://github.com/yeroo/agliteterm)

`agwinterm` is a native Windows terminal with a full control API, in a fast, custom-drawn Win32 + Direct2D shell. Shells are organized into workspaces, each holding the sessions for one project or context, and everything it holds is an object a script can address. The bundled `agwintermctl` creates sessions and types into them, reads a pane's text back, runs a program in an overlay and returns its exit status, sets a session's status, opens a dashboard, moves windows, and reads all of that state back out over a local named pipe.

This is narma's independently maintained fork of [Boris Kudriashov's Windows agwinterm](https://github.com/yeroo/agwinterm). Its workspace and control model grew out of [umputun's agterm for macOS](https://github.com/umputun/agterm).

## What the fork adds

- **Reconnect to running shells.** The Windows host keeps a hosted shell and its Ghostty terminal state alive across GUI restarts. [Session ownership](docs/single-owner-contracts.md)
- **Work across Windows, WSL and macOS.** Run the CLI and agent hooks inside a WSL distribution, opt into recoverable Linux sessions with ZMX, attach to a running agterm session over SSH, or expose an owned WSL terminal to a Mac viewer. [WSL setup](docs/wsl.md)
- **Give scripts a richer interface.** Wait for agent status changes, ask a question with native controls, run a program over one pane, or display a local HTML page using WebView2. [Control API](docs/control-api.md) · [Cookbook compatibility](docs/cookbook-parity.md)
- **Navigate long-running work.** Filter the sidebar, reopen recently closed work across windows and zoom a focused pane while its split peer keeps running. [Working sets](docs/working-sets.md) · [Recently closed work](docs/recent-work.md)

See the [upstream comparison](docs/fork.md) for the baseline, architecture and verification procedures.

## Terminal features

- **Workspaces and sessions.** Sessions are grouped under named workspaces in a sidebar with drag-reorder, flags, unread badges, multi-select, and reopen for closed sessions and workspaces. A live **dashboard** shows a grid of sessions at once.
- **Control API and CLI.** `agwintermctl` drives almost everything over a named pipe, with full read-back: the tree with split ratios and pane ids, window state, session output, a pane's caret column, the age of a status.
- **Splits, scratch, quick terminal and overlays.** Split a session side by side or stacked, open a scratch or a detached quick terminal, run a program in an overlay over a session or over one pane of it, or show a passive HUD.
- **Agent status and skill.** Agents report their state through hooks or the API as a colored dot and a title-bar bell. Opt-in installers add the agent skill, Claude Code / Codex / Devin status hooks, shell integration and the CLI on `PATH`.
- **Claude Code binding and auto-resume.** A `claude` wrapper ties each conversation to its pane, so a restart re-launches every bound pane and the conversation comes back. Updating Claude Code restarts every running Claude session into its own conversation.
- **Restore and session ownership.** Hosted shells run in a separate Ghostty owner process and survive a UI quit or crash. When upgrading from the previous Windows host, setup checkpoints the open windows, saves a backup and closes the old shells. The new version restores the layout, configured commands and history saved with `restore-buffer` enabled.
- **A real Windows terminal.** Default Terminal Application registration, Sixel and Kitty graphics, the Kitty keyboard protocol, ligatures, elevated and de-elevated sessions side by side, and agterm's menus — File, View, Navigate, Help — as a menu bar in the title bar (Alt or F10).
- **Accessible.** The terminal is a UIA text document for Narrator and NVDA, every control is in the UIA tree, and new output is announced.
- **Themes.** ~580 bundled themes retint the whole window, fonts apply live, and an optional mode follows Windows light/dark.

A lot of "does it have X?" questions have the same answer: bind X yourself. A `command` line in `keymap.conf` names any shell line and a `map` line binds it to a key chord or leader chord, and an overlay gives an interactive program a real terminal over the session, so a git UI or a file manager is two lines away. Install the agent skill and ask the agent in your session for what you want; it knows the syntax.

![agwinterm](docs/img/screenshot.png)

<details>
<summary>More screenshots</summary>

The Settings window, on the Agent Status tab, where the status colors and the blocked sound are chosen:

![Settings](docs/img/settings.png)

</details>

## The model

- **Window.** A top-level bundle of workspaces and sessions with its own sidebar; `agwintermctl window` addresses each one.
- **Workspace.** A named group of sessions for one project or context.
- **Session.** One running shell with a name, a working directory, an optional one-line context, and its own scrollback. It is the row you see in the sidebar, and it keeps running while you work in another one.
- **Split, scratch and quick.** A session can split into two shells side by side or stacked, both sharing its one sidebar row. A scratch terminal opens over it for a quick aside; the quick terminal is a detached window of its own.
- **Overlay.** One program running in a temporary terminal over a session, or over one pane of a split. It disappears when the program exits (unless opened with `--wait`, which keeps it until closed) and leaves the shell underneath unchanged.

## Install

Download the self-contained Windows x64 build from [this fork's releases](https://github.com/narma/agwinterm/releases).

- **`agwinterm-setup-<version>.exe`** — per-user installer (Start-menu shortcut and uninstaller).
- **`agwinterm-portable-<version>-win-x64.exe`** — a single self-contained exe, no installation; settings still live under `%LOCALAPPDATA%\agwinterm`. The CLI and WSL helpers for Linux x64/ARM64 are embedded and extracted on first use to `%LOCALAPPDATA%\agwinterm\portable-tools`.

Standard assets use .NET 10. The separate `agwinterm-net11-` assets use .NET 11. The updater checks this fork and selects assets for the running build.

Binaries are currently **unsigned**, so SmartScreen warns on first run → *More info → Run anyway*. Release artifacts carry **Sigstore build-provenance attestations**:

```powershell
gh attestation verify <file> --repo narma/agwinterm
```

The installer is deliberately minimal. The integrations are **opt-in from inside the app**, from the command palette (`Ctrl+Shift+P`) or Settings, and safe to rerun: *Install Command-Line Tool (PATH)*, *Install Agent Status Hooks*, *Install Agent Skill* and *Install Shell Integration* (the same four are `agwintermctl install cli|hooks|skill|shell`), and default-terminal registration in *Settings → General*.

For WSL, select the distribution's session before installing: integrations use its Linux user and home directory. WSL sessions support the control CLI, agent hooks and resume, bash/zsh working-directory tracking, Linux command overlays, and path conversion for Explorer and file drops. See [WSL setup](docs/wsl.md).


## Scripting agwinterm

`agwintermctl` drives a running agwinterm over a local named pipe speaking newline-delimited JSON, one command per invocation. Inside a session `AGWINTERM_PIPE` and `AGWINTERM_SESSION_ID` are already set, so a bare `agwintermctl` targets the instance and pane it runs in. Terminal output is not streamed; `session text` reads a session's buffer when a script needs to see it.

```powershell
$sid = agwintermctl session new --workspace-name demo --create-workspace --cwd $PWD --no-select  # the new session's id
agwintermctl session split on --axis horizontal --target $sid           # add a stacked shell; answers its pane id
agwintermctl session type "git status`n" --target $sid                  # drive a session you are not looking at
agwintermctl session text --target $sid --lines 10                      # read its terminal back
agwintermctl session status blocked --target $sid                       # set the sidebar status dot
agwintermctl session overlay open "lazygit" --block                     # run a program over the session, get its exit
agwintermctl tree --json                                                # dump the whole model as JSON
```

`session type` returns once the keystrokes are queued, so a following `session text` races the shell, and `session write` only paints: the program's next repaint, or any pane resize, paints over it.

The same interface covers windows, splits, overlays, dashboards, HUDs, notifications, events, themes, images and restoration. `agwintermctl --help` lists the session, sidebar, restore, surface and image verbs; the agent skill (`agwintermctl install skill`) carries the full set, and [docs/control-api.md](docs/control-api.md) documents the replies a script can rely on.

## Documentation

- [User guide](docs/user-guide.md): agent features, the terminal, accessibility, themes, keyboard essentials, and where configuration lives.
- [Control API](docs/control-api.md): a tour of `agwintermctl`, and the replies and refusals of the verbs a script leans on.
- [Launching a session command](docs/session-commands.md): `session new --command`, direct mode, `--wait`.
- [Workspace navigation](docs/navigation.md), [quick terminal](docs/quick-terminal.md), [session HUD](docs/session-hud.md), [native picker](docs/native-picker.md).
- [Default terminal](docs/defterm-testing.md): how the console handoff is exercised.
- [Local UI tests](docs/hyperv-ui-tests.md): local Windows UI commands use dedicated Hyper-V VMs; CI keeps its own runner desktop.
- [agterm interoperability](docs/agterm-parity.md) and [historical agliteterm parity](docs/lite-parity.md).
- [CONTRIBUTING.md](CONTRIBUTING.md): building from source, dev builds side by side with the release, the contract with agliteterm.

Report bugs in [Issues](https://github.com/narma/agwinterm/issues).

## Related projects

- **[agterm](https://github.com/umputun/agterm)** by [umputun](https://github.com/umputun) is the macOS original whose design this project follows.
- **agliteterm** is the lightweight sibling, formerly `agwinterm-lite`. Its C++ client uses the Rust emulator core and pty-host. [agliteterm repository](https://github.com/yeroo/agliteterm)

The projects share a [control API contract](tests/conformance/control-api.json). This fork uses Ghostty for Windows terminal state. Consumers of the legacy Windows Rust core and ABI manifest can pin a release before 0.30.1.

## Attribution

agwinterm exists because of **[umputun](https://github.com/umputun)** and **[agterm](https://github.com/umputun/agterm)**. agterm's design — a terminal that treats AI coding agents as first-class citizens, with per-session status, workspace navigation, a detached quick terminal, a native picker, and a language-agnostic control socket — is the blueprint this project follows. agwinterm is a tribute and a port of the ideas and UX, not of the code. Thank you, umputun. 💜

- The [Ghostty VT library](native/ghostty-vt/README.md) maintains Windows terminal state. Bundled color themes come from [Ghostty](https://ghostty.org) and [iTerm2-Color-Schemes](https://github.com/mbadolato/iTerm2-Color-Schemes).
- **[Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows)** (Direct2D/DirectWrite), **[Porta.Pty](https://www.nuget.org/packages/Porta.Pty)** (ConPTY), and **[microsoft/terminal](https://github.com/microsoft/terminal)**'s OpenConsole for the default-terminal handoff and, from [Microsoft.Windows.Console.ConPTY](https://www.nuget.org/packages/Microsoft.Windows.Console.ConPTY) (MIT), as the ConPTY that sessions run on.
- Bundled fonts, with versions and licenses, are listed in [THIRD_PARTY_FONTS.md](THIRD_PARTY_FONTS.md).

## License

[MIT](LICENSE) © 2026 Boris Kudriashov. Bundled theme files retain their upstream (iTerm2-Color-Schemes, MIT) licensing.
