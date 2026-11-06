# agwinterm installers

Builds **per-user** Windows installers — end users need **no** .NET runtime installed:

- **`agwinterm-setup-<ver>.exe`** — the full `agwinterm` (.NET) terminal + `agwintermctl`, from a
  self-contained publish. Its shortcuts seed `--default-session-host server` on first run.
  Hosted sessions share a separate application host process; each terminal has one Ghostty
  owner. Direct in-process sessions remain available.
- **`agwinterm-lite-setup-<ver>.exe`** — the lightweight `agwinterm-lite` (C++/GDI) client for
  old / low-RAM machines, standalone. Always rides the Rust pty-host.

The full installer ships Ghostty and the Rust Linux helpers. It removes the retired Windows
Rust host and core from the full application's directory. The separate lite installation
and its native ABI assets remain independent.

The agwinterm setup also carries Microsoft's ConPTY, `conpty.dll` + `x64\OpenConsole.exe` from the
`Microsoft.Windows.Console.ConPTY` NuGet package (MIT), which `dotnet publish` puts in the stage.
Both host and in-process sessions use the bundled ConPTY.

On upgrade, setup asks the installed UI to checkpoint its open windows, creates a verified
backup under `%LOCALAPPDATA%\agwinterm-upgrade-backups`, closes the UI and ends the old host.
Running Windows programs end; layout, settings and history saved by the previous version
are restored at next launch. History requires the old `restore-buffer` setting. If an old
host has no matching UI, open that version once and retry. A failed checkpoint or backup
stops the install before any decision to close sessions or replace program files.

## Build

```powershell
installer\build.ps1        # full installer
installer\build-portable.ps1
```

Prereqs:
- .NET SDK with the `net10.0-windows` target (the repo pins .NET 10).
- Rust and the Windows linker for the Windows integration helper.
- The static Linux hooks at `artifacts/wsl-hook/agwinterm-hook-linux-x64` and `agwinterm-hook-linux-arm64`. PR and release CI build and test them on native Linux runners. Local builds can run `tools/build-wsl-hook.ps1` using Rust in WSL with the `x86_64-unknown-linux-musl` and `aarch64-unknown-linux-musl` targets installed; packaging builds missing artifacts automatically. The installer ships both Linux binaries and `agwinterm-hook.exe`.
- [Inno Setup 6](https://jrsoftware.org/isinfo.php) — `ISCC.exe` on PATH, the default install location,
  or the per-user winget location (`winget install JRSoftware.InnoSetup`).

`build-portable.ps1` independently builds a self-contained CLI and Windows hook, then embeds them and both Linux hooks in the single-file application. It does not require Inno Setup or a prior installer build. Tools are extracted lazily into `%LOCALAPPDATA%\agwinterm\portable-tools\<payload-hash>`, keeping installed wrappers valid across app updates. For headless extraction without changing PATH, run the portable exe with `--extract-tools <directory>`; this creates a content-addressed subdirectory. `tests/integration/portable-tools.ps1` verifies the shipped exe in isolation from build-stage sidecars.

`build.ps1`:
1. publishes `Agwinterm.Win32` (the app) and `Agwinterm.Ctl` (`agwintermctl`) as **self-contained win-x64**
   (a folder, not single-file — robust for the Vortice native libs and the on-disk `themes\`/`assets\`)
   into `installer\stage`,
2. stages the verified Ghostty runtime and Windows/Linux integration helpers,
3. compiles `installer\agwinterm.iss` and writes `installer\Output\agwinterm-setup-<ver>.exe`.

## What the installers do

Deliberately **minimal / non-invasive** (agterm-style): they only copy files and create shortcuts.
They do **not** touch your `PATH`, profile, or config.

- **Per-user, no admin** (`PrivilegesRequired=lowest`); main installs to
  `%LOCALAPPDATA%\Programs\agwinterm`, lite to `%LOCALAPPDATA%\Programs\agwinterm-lite`.
- **Start-menu** shortcut (`agwinterm` / `agwinterm lite`); **desktop** shortcut via a checkbox (task).
- **Launch on finish**: a "Launch …" checkbox (interactive installs).
- **Uninstall** removes the app files (your `%LOCALAPPDATA%\agwinterm` / `%LOCALAPPDATA%\agwinterm-lite`
  config/sessions are left intact).
- Upgrading the main setup from 0.15.0–0.16.0 (which bundled lite) removes the bundled
  `agwinterm-lite.exe` and its old shortcuts — install the lite setup to keep lite.

### Integrations are opt-in (from inside the app)

Open the action palette (**Ctrl+Shift+P**) and run the **Install …** entries when you want them:

- **Install Command-Line Tool (PATH)** — adds `agwintermctl` to your user `PATH` (so shells & AI agents can call it).
- **Install Agent Status Hooks** — Claude Code / Codex / Devin / generic-agent status reporting, plus a
  transparent `claude` launcher that binds Claude's session id to the agwinterm pane so restart
  auto-resumes the conversation. When an existing Devin JSONC config needs changes, its exact original
  is kept in a numbered `.agwinterm.bak` sibling before the active config is rewritten as JSON without
  comments or formatting.
- **Install Agent Skill** — teaches agents to drive agwinterm via `agwintermctl`.
- **Install Shell Integration** — a `$PROFILE` OSC-7 hook for live cwd (also works out of the box without this).

Each is reversible / re-runnable and can also be driven headless: `agwintermctl install cli|hooks|skill|shell`.

## Local GUI-only update

For day-to-day GUI development, double-click `installer\update-gui.cmd`, or run:

```powershell
.\installer\update-gui.ps1
# Custom installation or update without launching the GUI:
.\installer\update-gui.ps1 -InstallDir 'D:\Programs\agwinterm' -NoLaunch
# Include CLI changes such as new command flags:
.\installer\update-gui.ps1 -IncludeCli
```

The script publishes the current .NET GUI to a fresh folder, waits up to 120 seconds
for you to close the installed GUI normally, backs up changed files, updates them,
verifies their hashes, and launches the GUI again. Use `-WaitSeconds 600` for more time.
It never kills processes. Close the GUI in your usual session-preserving way; closing
individual windows while other windows remain can terminate those windows' sessions.
Only server-hosted sessions can survive a GUI exit; in-process sessions cannot.

The Rust pty-host, native Rust core, ConPTY/OpenConsole and hooks stay untouched.
The CLI stays untouched unless `-IncludeCli` is supplied; then GUI and CLI are
published together and use the same lock checks, backup and rollback.
The changed GUI assemblies, managed dependencies, .NET runtime, themes, and assets
come from `dotnet publish`. No Inno Setup or Rust build is needed. Backups and publish
output remain under `artifacts/gui-update/`; a copy/verification failure attempts to
restore previous files and reports any rollback failure. A locked file aborts the
preflight before any installed file is changed. A running C# `--pty-host` uses the GUI
executable and must exit before that executable can be updated.

Use this for GUI changes compatible with the installed Rust core and host protocol.
For changes to the native core/ABI, host/protocol, ConPTY, hooks, or CLI, use the full
build and installer instead, after finishing the sessions that depend on those files.
This script requires an existing installation; it does not create shortcuts or update
the Windows uninstall registration.

## Silent install / uninstall

```powershell
agwinterm-setup-<ver>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
agwinterm-lite-setup-<ver>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
# uninstall:
"%LOCALAPPDATA%\Programs\agwinterm\unins000.exe" /VERYSILENT
"%LOCALAPPDATA%\Programs\agwinterm-lite\unins000.exe" /VERYSILENT
```

## Optional WebView2 runtime

Web overlays use Microsoft WebView2 Evergreen. Normal terminal startup never
downloads or initializes it. Both package formats ship the pinned SDK Core
assembly and x64 loader; the Chromium runtime is a separate shared installation.
Run `agwintermctl install webview2` to explicitly install it with winget, or run
`install-webview2.cmd` from the installed application directory. These actions
are never invoked by ordinary app startup or by the installer itself.

For an offline machine, obtain Microsoft's Evergreen Standalone Installer from
the [WebView2 download page](https://developer.microsoft.com/microsoft-edge/webview2/)
on another machine and run it explicitly. Terminal use remains available without
the runtime; web requests return an installation hint and preserve existing overlays.

The portable exe embeds its native loader through .NET single-file extraction.
Extraction uses the .NET bundle directory (or `DOTNET_BUNDLE_EXTRACT_BASE_DIR`
when explicitly configured), independently of portable CLI/hook extraction.
Measure loader extraction, application bytes, runtime installation and temporary
browser profiles separately when comparing package sizes.

## Signing (not done)

The installer is **unsigned**, so Windows SmartScreen will warn on first run. To sign, obtain a
code-signing certificate and sign both the app exe and the setup exe, e.g.:

```powershell
signtool sign /fd SHA256 /a /tr http://timestamp.digicert.com /td SHA256 <file>.exe
```

Sign the published `Agwinterm.Win32.exe`/`agwintermctl.exe` (in `stage\`) **before** compiling, then
sign the produced `agwinterm-setup-<ver>.exe`.
