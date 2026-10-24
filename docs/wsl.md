# WSL

Open a `WSL: <distribution>` shell profile and run the integration installers from the menu or command palette. They use the active session's distribution and `--user`, including custom WSL profiles. From a terminal, `agwintermctl install cli|hooks|skill|shell --target <session>` selects the same environment; inside an agwinterm session the target defaults to the caller. An unknown target fails instead of installing into Windows. With no running session or target, the standalone CLI's `install cli` and `install skill` retain their Windows installation behavior.

Run `agwintermctl profiles reload` to discover distributions installed since the first launch. Existing profiles and their settings are retained. Profile `cwd` may be a Linux path; restored and duplicated sessions, splits and reopened sessions keep their Linux working directory. Without a configured directory, the WSL home mode opens `~` for the selected Linux user.

## Requirements and installation

The WSL integration helpers require `python3` in the distribution and Windows executable interop enabled. They do not install packages or change system settings. The Windows CLI must be shipped next to the app, as it is in the packaged build.

- **CLI:** writes `~/.local/bin/agwintermctl`, a quoted wrapper around the Windows CLI resolved with `wslpath`. Adds that directory to bash/zsh startup files and `.profile`. `install cli --remove` removes the wrapper and these PATH blocks.
- **Shell:** adds an idempotent source block to `.bashrc` and `.zshrc` (honoring `ZDOTDIR`). The script preserves existing prompt commands and reports the working directory with percent-encoded OSC 7. Restart the shell after installation. Other Linux shells can emit OSC 7 themselves; their startup files are not modified.
- **Hooks:** installs Python handlers for Claude Code, Codex and Devin, plus the CLI wrapper they use. Existing hooks are preserved, malformed configurations are refused, and changed configurations are backed up before rewriting. Devin JSONC is accepted and rewritten as JSON with the original retained. Approve new Codex hooks in `/hooks`.
- **Skill:** installs the agent skill into the Linux Claude and Codex configuration directories. `CLAUDE_CONFIG_DIR`, `CODEX_HOME` and, for Devin, `XDG_CONFIG_HOME` are honored.

Profile environment overrides are applied inside Linux, including `HOME`, which WSL does not override through `WSLENV` alone. Agwinterm identity and control-pipe variables cross both directions through `WSLENV`, so Linux hooks and the Windows CLI address the correct pane and app instance.

## Commands, paths and restore

Scratch panes, overlays and custom commands started from a WSL session use its distribution. Overlay and detached commands use `/bin/sh -lc`; interactive custom-command sessions run the command in the user's shell and leave a login shell open afterwards.

File drops convert Windows paths with the selected distribution's `wslpath` and quote them for a POSIX shell before pasting. Reveal in Explorer and Linux `file://` links convert in the opposite direction. File arguments passed directly to the Windows CLI (for example `image show`) still need `wslpath -w`; the wrapper does not guess which arguments are paths.

SessionStart hooks report Linux PIDs. The host reads `/proc` in the selected distribution, verifies the pane identity and rejects nested agents before saving the conversation ID, directory and permission flags. `restore capture` and quit-time capture read the Linux terminal's foreground process group, excluding shells and the restore denylist. Captured commands are replayed only when `restore-commands` is enabled, as on Windows.

## Validation

Build the solution, then run `pwsh -NoProfile -File tests/integration/wsl-control.ps1 -Distro Ubuntu`. It starts a private app instance, verifies its disposable Linux HOME before installing anything, and checks CLI routing, installers, OSC 7, duplicate/persisted directories, agent status/resume, overlays and foreground capture. `python3 tests/integration/wsl-scripts.py` inside WSL checks idempotence, config preservation, JSONC, CLI quoting and bash/zsh prompt integration without changing the user's HOME.
