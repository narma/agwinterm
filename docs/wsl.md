# WSL

Open a `WSL: <distribution>` shell profile and run the integration installers from the menu or command palette. They use the active session's distribution and `--user`, including custom WSL profiles. From a terminal, `agwintermctl install cli|hooks|skill|shell --target <session>` selects the same environment; inside an agwinterm session the target defaults to the caller. An unknown target fails instead of installing into Windows. With no running session or target, the standalone CLI's `install cli` and `install skill` retain their Windows installation behavior.

Run `agwintermctl profiles reload` to discover distributions installed since the first launch. Existing profiles and their settings are retained. Profile `cwd` may be a Linux path; restored and duplicated sessions, splits and reopened sessions keep their Linux working directory. Without a configured directory, the WSL home mode opens `~` for the selected Linux user.

## Requirements and installation

Installation, path helpers and foreground capture currently require `python3` in the distribution. Installed x64 agent hooks use a static Linux Rust helper and a small Windows pipe bridge; they need neither Python nor a Linux .NET runtime. Windows executable interop must be enabled. The helpers do not install packages or change system settings. The Windows CLI and native hook binaries are shipped next to the app in the packaged build.

- **CLI:** writes `~/.local/bin/agwintermctl`, a quoted wrapper around the Windows CLI resolved with `wslpath`. Adds that directory to bash/zsh startup files and `.profile`. `install cli --remove` removes the wrapper and these PATH blocks.
- **Shell:** adds an idempotent source block to `.bashrc` and `.zshrc` (honoring `ZDOTDIR`). The script preserves existing prompt commands and reports the working directory with percent-encoded OSC 7. Restart the shell after installation. Other Linux shells can emit OSC 7 themselves; their startup files are not modified.
- **Hooks:** installs the native Linux handler for Claude Code, Codex and Devin. Existing generated Python commands are migrated in place; unrelated hooks are preserved. If native binaries are missing or the Linux architecture is unsupported, the installer explicitly reports the Python fallback. Malformed configurations are refused, and changed configurations are backed up before rewriting. Devin JSONC is accepted and rewritten as JSON with the original retained. Approve new Codex hooks in `/hooks`.
- **Skill:** installs the agent skill into the Linux Claude and Codex configuration directories. `CLAUDE_CONFIG_DIR`, `CODEX_HOME` and, for Devin, `XDG_CONFIG_HOME` are honored.

Profile environment overrides are applied inside Linux, including `HOME`, which WSL does not override through `WSLENV` alone. Agwinterm identity and control-pipe variables cross both directions through `WSLENV`, so Linux hooks and the Windows CLI address the correct pane and app instance.

## Commands, paths and restore

Scratch panes, overlays and custom commands started from a WSL session use its distribution. Overlay and detached commands use `/bin/sh -lc`; interactive custom-command sessions run the command in the user's shell and leave a login shell open afterwards.

File drops convert Windows paths with the selected distribution's `wslpath` and quote them for a POSIX shell before pasting. Reveal in Explorer and Linux `file://` links convert in the opposite direction. File arguments passed directly to the Windows CLI (for example `image show`) still need `wslpath -w`; the wrapper does not guess which arguments are paths.

SessionStart hooks collect Linux process ancestry. The host checks it against live `/proc` data through the WSL filesystem, including pane identity, process start times and command lines, then rejects nested agents before saving the conversation ID, directory and permission flags. This avoids starting `wsl.exe` for each normal binding. If filesystem access is denied (for example with a different `--user`), verification runs the native helper under the profile's Linux user. The Windows bridge has a two-second deadline; the Linux caller has a three-second deadline. `restore capture` and quit-time capture still use the Python helper to read the Linux terminal's foreground process group. Captured commands are replayed only when `restore-commands` is enabled, as on Windows.

## Validation

Build the solution, then run `pwsh -NoProfile -File tests/integration/wsl-control.ps1 -Distro Ubuntu`. It starts a private app instance, verifies its disposable Linux HOME before installing anything, and checks CLI routing, installers, OSC 7, duplicate/persisted directories, agent status/resume, overlays and foreground capture. `python3 tests/integration/wsl-scripts.py` inside WSL checks idempotence, config preservation, JSONC, CLI quoting and bash/zsh prompt integration without changing the user's HOME.

For native hooks, build `cargo build --release --manifest-path native/Cargo.toml -p agwinterm-hook` on Windows and run `tools/build-wsl-hook.ps1`. The Linux build requires Rust in WSL with the `x86_64-unknown-linux-musl` target installed (`rustup target add x86_64-unknown-linux-musl` inside WSL). It produces a static binary under `artifacts/wsl-hook`. Run `tests/integration/wsl-control.ps1 -NativeHooks` to stage both binaries and test installation; add `-LinuxUser root` to exercise a non-default user in the disposable HOME. `tests/integration/wsl-hook-bench.ps1` compares Python, Windows Rust and Linux Rust paths, tests a stalled pipe, and writes raw measurements under `artifacts/wsl-hook-research`.
