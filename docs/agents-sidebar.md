# Agents sidebar

The lower part of the sidebar lists agents across all workspaces in the current
window, including background sessions and hidden split panes. A session appears
after reporting status through the existing hooks/control API, binding an agent,
or sending OSC 7501. Untouched shells are not listed.

* **Working**: the agent is busy.
* **Blocked**: it needs an answer, permission, authentication, or has reported an
  error. Opening the terminal does not dismiss this state.
* **Done**: work finished while its terminal was not being viewed. An immediate
  idle report does not erase the unread completion.
* **Idle**: the agent is at rest, or its completion has been viewed.

Click a row to focus its terminal, including a hidden split. OSC 7501 child records
appear underneath their terminal's root record; selecting them opens the terminal
that emitted them. A blocked child raises its parent's priority. A separate worker
terminal created with `session new --no-select` appears independently when it reports
status. This does not discover private, in-process workers that expose neither a
terminal nor an OSC record.

The header's sort menu offers **Priority** (Blocked, Done, Working, Idle; newer
changes first) and **Grouped by workspace**. Right-click a row to **Pin agent** or
**Unpin agent**, or collapse/expand its OSC workers. Pins form the first section and
are independent of session flags. Pins, sort mode and panel collapse state are saved
in the window state. Scroll over the panel to reach additional agents.

Keyboard: **F6**, then **Tab**, enters the agents list. Use arrows/Home/End to move,
Enter/Space to open, **P** to pin/unpin, **S** to sort, and Escape to return to the
terminal. Tab switches back to the sessions list. Visible agents and header buttons
also expose UI Automation Invoke actions.

## OSC 7501

Parsing, validation and feature-query replies use the bundled Ghostty implementation.
No hook, interpreter or extra runtime dependency is needed. For example, a program
can write these sequences (ESC means byte 27 and BEL means byte 7):

```text
ESC ] 7501 ; state=working:app=codex:progress=40 BEL
ESC ] 7501 ; state=blocked:id=workers/test:kind=permission:msg=QXBwbHk/ BEL
ESC ] 7501 ; state=done:app=codex BEL
ESC ] 7501 ; state=clear:id=workers BEL
```

Both BEL and ESC-backslash termination are accepted. Reports replace complete
records; clear removes the named subtree, or all records when no ID is supplied.
There are at most 64 records per terminal, evicting the least recently updated.
The app name inherits from the nearest ancestor. Decoded titles/messages are plain
text with invisible formatting controls removed. Error reports use Blocked attention.

Active OSC records drive the panel; clear withdraws them. The existing session status
indicator and API receive the most urgent OSC state too. A shell's primary OSC 133
prompt or process exit removes transient working/blocked records, retaining done/error
records. Full terminal reset clears all records. The Windows session host retains
records while detached and replays them when a window reconnects.

## Checks

`ProgramStatusTests` exercises actual native parsing, byte boundaries, queries,
invalid input, reset/prompt lifecycle, bounded records and unread completion.
The `Agents` UI suite exercises navigation, attention, nested records, pin/unpin,
sorting, persistence and collapse through the normal Hyper-V runner. Run it with
both in-process sessions and `-TestParameters @{SessionHost='server';SingleOwner=$true}`.
