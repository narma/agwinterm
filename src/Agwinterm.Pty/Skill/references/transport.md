Ordinary control requests exit 0 on success, 1 on refusal/transport/protocol failure,
and 2 on invalid CLI arguments. --json preserves the reply but ok:false exits 1.
Offline version and picker retain their documented special outcomes. A timeout does
not prove rollback. Unknown options, invalid numbers and duplicate selectors refuse
before sending. Use one --target, one --window and either --pipe or --socket; no batches.

agwinterm is a Windows terminal built for AI coding agents. When you run inside it you can
control it: report your status (a colored dot per session in the sidebar), manage sessions,
type into sessions, and show images — via the `agwintermctl` CLI (preferred) or by writing
one newline-delimited JSON request to its control pipe.

## Detect
You are inside agwinterm when `AGWINTERM_ENABLED=1`. Relevant env vars:
- `AGWINTERM_SESSION_ID` — your session id (most content commands default to it; focus/resize/command run retain active scope). Unique PER PANE:
  two agents in a split can't collide (each pane resolves as its own target). Inside a scratch, an
  overlay or the quick terminal it is THAT cover's id: `session text` / `type` / `keys` with no target act on
  the cover you run in. `session overlay` verbs resolve it by what it names — from inside a PANE overlay a
  bare `session overlay close` closes your own slot (see Overlays); from inside a scratch the bare form is the
  owning session's SESSION-WIDE slot (pass `--pane` or `--target` for anything else); from inside the quick
  terminal it is refused `no session matches that target` (the quick terminal belongs to no session).
- `AGWINTERM_PANE_ID` — explicit pane identity (same value; use when you specifically mean the pane).
- `AGWINTERM_WINDOW_ID` — your window id.
- `AGWINTERM_PIPE` — the control pipe name (full path `\\.\pipe\<name>`).

## Status and agent lifetime
Prefer the agent's native OSC 7501 reports or installed hooks. Do not send duplicate
manual statuses when either integration already reports them. OSC 7501 is emitted by
the interactive program to its terminal, not printed into a captured tool result.
`done` means a turn finished; `idle` means a live agent is waiting. Neither means exit.
On exit, producers send `state=clear`; returning to the shell also removes the agent.
Pins and saved resume commands do not prove that an agent is running.
For agents without native reports or hooks, report through the control API:
- `agwintermctl session status active`     — working
- `agwintermctl session status blocked`    — you need the user
- `agwintermctl session status completed`  — finished
- `agwintermctl session status idle`       — live, waiting for work

Flags (any status): `--sound [name]` plays a cue (default alert, a system-sound name
`beep|asterisk|exclamation|hand|question`, a Windows sound-event alias, or a `.wav` path);
`--blink` pulses the sidebar dot + title-bar bell until cleared; `--auto-reset` clears the
completed status back to idle when the user selects the session. Blocked survives viewing.
Example, when you need attention:
`agwintermctl session status blocked --sound --blink`.
(A default blocked cue can also be set once via `blocked-sound =` in the config.)

For agents without native status, run `agwintermctl install hooks` once. It wires Claude Code, Codex and Devin hooks so your
status updates automatically (active while working, blocked on permission prompts, completed on stop);
trust the Codex hooks once with /hooks. It also adds a SessionStart hook to Claude, Codex and Devin so the pane
resumes your exact session after a restart or reboot, in any shell;
and installs a generic PowerShell-profile bridge that marks any command matching `$env:AGWINTERM_AGENT_RE`
working on launch and clears it on exit via OSC 7501.
Claude, Codex and Devin use their dedicated hooks and are excluded from this generic bridge.

## If agwintermctl is not on PATH
Send one JSON line to the pipe named by `%AGWINTERM_PIPE%` (default `agwinterm`), e.g. in PowerShell:
```
$c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $env:AGWINTERM_PIPE, 'InOut')
$c.Connect(2000); $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush=$true
$w.WriteLine('{"cmd":"session.status","target":"' + $env:AGWINTERM_SESSION_ID + '","args":{"status":"active"}}')
```
Request shape: `{"cmd":"<area>.<command>","target":"<id|prefix|active>","args":{...}}`.
Response: `{"ok":true,"result":...}` or `{"ok":false,"error":"..."}`.
