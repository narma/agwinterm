## Settings & config
- `agwintermctl settings`                                — open the Settings window (grouped appearance/behavior controls)
- `agwintermctl config list`                             — every config key with its current value
- `agwintermctl config get <key>` · `config set <key> <value>` — read/change a setting (persists to agwinterm.conf, applies live)
  Keys: font-family, font-size, cursor-style, cursor-blink, cursor-blink-ms, theme, scrollback-lines,
  inactive-pane-dim (0-100, dims non-active split panes), window-opacity (30-100), sidebar-tint (-100..100),
  scroll-speed (1-10), follow-output (bottom default / always), new-session-dir, right-click-paste, copy-on-select, desktop-notifications, shell-integration,
  restore-commands, blocked-sound, omp-theme. Changing the theme retints the WHOLE window (sidebar/title/status), not just the terminal.

## Config / restore
- Working sets: `workspace flag on|off|toggle --target W` includes hidden members; `workspace new --collapsed` starts folded.
- `session new/move --before S|--after S` uses an unambiguous session anchor; explicit workspace must agree. IDs survive moves.
- `session close --targets A,B` / `session move [WORKSPACE] --targets A,B [--before C|--after C]` are explicit bounded batches.
  Repeated --target still refuses. Inspect `outcome` and each item's `changed/error`: exit 0 can contain partial errors.
- `workspace focus on|off|toggle --target W` edits membership; `workspace filter on|off|toggle` retains it; focus clear removes it.
- `sidebar flagged-layout plain|flat|tree|toggle` is per-window; plain preserves the local names-only default. Tree/window state
  expose `workingSet` with focusedWorkspaceIds, workspaceFilterEnabled, sidebarFlaggedLayout and visibleSessionIds.
- `agwintermctl recent list` — newest-first durable closed sessions/workspaces with exact entry IDs, source window and remaining count.
- `agwintermctl recent open ENTRY-ID [--window ID]` — restore into an open library window (default active), without forcing OS focus.
  Preserves split IDs/layout, profile/cwd, context, flags and restore pins. Pins are retained, not replayed. Inspect `outcome`,
  `members` and `remaining`: partial workspace recovery retains failed members and must be retried in the same destination.
- `agwintermctl recent clear [ENTRY-ID]` — clear only recent history. List/clear reject window/target selectors.
- `agwintermctl keymap reload`                           — re-parse keymap.conf (reports diagnostics)
- `agwintermctl restore clear`                           — clear the saved session-tree state
- `agwintermctl restore capture [--target ID]`            — capture the foreground command of EVERY real pane (or of the one
  pane/session named) into its restore slot NOW and save, so a crash, a `Stop-Process`, a power loss or a missed
  update-quit still finds it there; without this the capture happened only on a clean quit. Call it before a long
  run, or from a hook. It can take SECONDS (one CIM process query for all panes, up to 15 s) — do not put it on a
  hot path. Replies `{captured, replayOnRestore, panes:[{pane, session, captured}]}`: `captured` per pane is the
  command line the shell is running, or `null` = that shell had no non-denylisted child (the honest answer, and
  it is written into the slot — a fresh capture overwrites an older checkpoint, including to empty; `powershell`,
  `cmd` and the other shells on `restore-denylist.conf` never count as a command). `replayOnRestore` is the
  `restore-commands` toggle: the capture ALWAYS happens, but the slot is typed back at restart only when this is
  true — check it, or `config set restore-commands true`, or the checkpoint is kept and never replayed. Read the
  slots back in `tree --json` as `capturedCommands`, keyed by pane id like `restoreCommands`. An unknown target,
  or a scratch/overlay/quick pane (never restored, so no slot), is refused and nothing is captured or saved; a
  process query that fails or times out is a refusal too, never an empty answer for every pane. ONE refusal
  leaves something behind and says so: "captured into memory but the state file could not be written" — the
  slots are filled (`tree` shows them) but this save did not put the checkpoint on disk; fix
  the state directory and capture again. `session context` / `session rename` replies describe the value in
  memory the same way — their save is best-effort and silent.
- `agwintermctl install hooks|skill|shell`               — install agent-status hooks / this skill / shell-integration (live cwd)
- `agwintermctl install cli [--remove]`                  — add (or remove) agwintermctl on the user PATH
  Installation uses the caller's session profile (or `--target ID`): WSL installs into that distribution/user's Linux home.
  WSL integration uses the bundled native Linux x64 helper and Windows interop; Python is not required. Shell integration supports bash/zsh. Run `profiles reload` to discover newly installed distributions.
  Linux file arguments read by the Windows CLI, such as `image show`, need `wslpath -w`; session cwd arguments remain Linux paths.
- `agwintermctl omp list` / `omp set <name> [--persist]` — list / apply oh-my-posh themes (live; --persist keeps it for new sessions).
  A name that resolves to no theme is REFUSED (`ok:false`, the theme in effect unchanged) — it used to answer `ok:true` with the text.
