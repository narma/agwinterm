---
name: agwinterm
description: Inspect or control Agwinterm terminal sessions and panes when a task needs terminal state, input, or UI actions.
---

# Agwinterm terminal control

Use `agwintermctl` from an Agwinterm pane (`AGWINTERM_ENABLED=1`).

## Find a pane and read its output

1. Run `agwintermctl tree --json` to list windows, workspaces, sessions and pane IDs.
2. Choose the pane by ID. Several panes can share a working directory.
   `AGWINTERM_SESSION_ID` identifies your own pane or overlay.
3. Run `agwintermctl session text --lines 40 --target ID` for recent output.
   Omit `--lines` for the visible screen. A main pane ID reads the shell underneath its overlay.

## Control a session

- Let installed hooks or native OSC reports handle agent status. When neither is available,
  use `agwintermctl session status active|blocked|completed|idle` as appropriate.
- For another pane, pass its ID explicitly. Before typing into an agent's composer,
  check for a draft using the [input and output guide](references/io.md).
- Change session names and context only at the user's request.
- A timeout leaves the outcome uncertain. Check the terminal state before repeating an action.

## Command guides

Open the guide for the operation you need.

- [Sessions and workspaces](references/sessions.md) — create, move, target, wait for status, watch events and manage WSL live panes.
- [Pane input and output](references/io.md) — read scrollback, inspect the cursor, use the clipboard, type and send keys.
- [Scratch terminals and overlays](references/overlays.md) — choose a slot, launch a program and retrieve its result.
- [User interaction](references/interaction.md) — open a picker, ask questions and send notifications.
- [Windows and layout](references/layout.md) — flag workspaces, split or resize panes, use custom commands and change appearance.
- [Settings and restore](references/settings.md) — configure the terminal, recover sessions and install integrations.
- [Images](references/images.md) — display image files or frames from shared memory.
- [Environment and transport](references/transport.md) — resolve targets, report status manually and use the control pipe if the CLI is unavailable.
