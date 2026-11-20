---
name: agwinterm
description: Inspect or control Agwinterm terminal sessions and panes when a task needs terminal state, input, or UI actions.
---

# Agwinterm terminal control

Inside an Agwinterm pane (`AGWINTERM_ENABLED=1`), use `agwintermctl`.

## Find a pane and read output

Run `agwintermctl tree --json`. Choose the pane by ID; working directories can repeat.
`AGWINTERM_SESSION_ID` identifies your own pane or overlay.
Use `agwintermctl session text --lines 40 --target ID` for recent output.
Omit `--lines` for the visible screen. A main pane ID reads the shell beneath its overlay.

## Run a command in a new WSL pane

From the project's directory in Bash, open a background session in the same distribution:

```bash
id=$(agwintermctl session new --profile "WSL: $WSL_DISTRO_NAME" --cwd "$PWD" --no-select) &&
    id=${id%$'\r'} && agwintermctl session text --target "$id"
```

For a custom profile, use its exact name from `agwintermctl profiles list`.
Once the new pane shows a shell prompt, run this in the calling shell:

```bash
printf '%s\n\n' 'npm test' | agwintermctl session type --stdin --target "$id"
agwintermctl session text --lines 40 --target "$id"
```

Replace `npm test` with the Linux command. The CLI strips one trailing newline;
the second submits it. Read the output again if the command is still running.
Before sending input to an existing agent pane, check its composer for an unsent draft.

## Follow an agent

`agwintermctl session wait completed,blocked --scope pane --target ID --wait-timeout 30 --json`
waits for agent status. Exit 0 means a match, 3 means the wait expired.
Hooks or native OSC reports normally set status. Without either, use
`agwintermctl session status active|blocked|completed|idle`.
After a mutation times out, inspect state before retrying. Change session names or context
only at the user's request.

## Further commands

- [Sessions and workspaces](references/sessions.md) — Windows launches, moves, events, WSL live panes.
- [Pane input and output](references/io.md) — composer checks, keys, selection, clipboard.
- [Scratch and overlays](references/overlays.md) — open a slot and retrieve results.
- [User interaction](references/interaction.md) — pickers, questions, notifications.
- [Windows and layout](references/layout.md) — splits, sizing, appearance.
- [Settings and restore](references/settings.md) — configuration, recovery, installation.
- [Images](references/images.md) — files and shared-memory frames.
- [Environment and transport](references/transport.md) — targeting, status, control pipe.
