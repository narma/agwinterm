# Terminal clipboard protocols

## Copy command output

The action palette offers **Select Command Output** and **Copy Command Output**.
Optional `keymap.conf` bindings use `select_command_output` and
`copy_command_output`; no default shortcut is replaced. With an existing
selection, the action targets the command output at its focus cell. Clear the
selection to target the most recent command with an OSC 133 output-start mark.
Prompt and command-input cells are excluded by Ghostty's semantic selection.

These actions require shell integration marks, refuse alternate-screen and
synchronized-output frames, and leave the clipboard alone when no output can be
selected. Hosted requests reject a changed frame; retry the action on the current
frame. Copy retains the selection, so `copy_selection_html` and
`copy_selection_vt` can copy the same output afterwards.

## Export and retained formatting

**Export Selection as Text / HTML / VT** in the action palette opens the Windows
save dialog. Keymap action names are `export_selection_text`,
`export_selection_html` and `export_selection_vt`. All files are UTF-8 without a
BOM; text preserves Windows line endings, HTML is a standalone document with
styles and links, and VT contains terminal styling sequences. The save dialog
confirms overwrites; cancellation leaves the selection and existing files alone.
Writes use a temporary file beside the destination and publish only on success.

HTML and VT are captured with the selected text, under the terminal owner lock.
Hosted selection replies carry these immutable representations to the GUI.
Clearing history, repainting a TUI, changing styles, or losing tracked coordinates
does not change the captured formatting. A new selection replaces the snapshot.
Each formatted representation is limited to 100,000 UTF-8 bytes; a larger
representation is unavailable, without truncating it or losing plain text.
Selections newly made on a frozen synchronized-output frame retain plain text
only; an already captured formatted selection remains available during a freeze.

## Application clipboard access

Agwinterm uses libghostty-vt to parse OSC 52 and the [OSC 5522 clipboard protocol](https://sw.kovidgoyal.net/kitty/clipboard/), assemble chunks and aliases, and encode replies.

```
clipboard-write = true
clipboard-read = false
```

Writes follow the existing `clipboard-write` policy. Reading data or listing MIME
types requires explicitly enabling `clipboard-read`, including for remote programs
running inside a terminal session. Denied reads return no clipboard content. No
session-password grants override these settings.

| Representation | Windows integration |
| --- | --- |
| `text/plain`, `text/plain;charset=utf-8` | Unicode clipboard text |
| `text/html` | Windows HTML Format with UTF-8 fragment offsets |
| `image/png` | Windows PNG clipboard format |
| Other MIME types | Fixed private MIME bundle, preserving binary length |

Every representation also lives in one fixed, length-prefixed MIME bundle so empty data and
trailing zero bytes round-trip exactly. Reads can also consume ordinary Windows
Unicode, HTML and PNG data produced by other applications. An empty transaction
clears the clipboard; an empty representation remains a present MIME value.
Windows primary/selection clipboards are unsupported (`ENOSYS`).

Limits: 1 MiB of aggregate content, 16 representations/requested types and 128
ASCII characters per MIME name. Oversized native writes are rejected by Ghostty
before reaching Windows. Unsupported destinations, invalid UTF-8 text, unavailable
clipboard access and OS failures return protocol errors. Validation and allocation
precede clipboard replacement. An unexpected failure after replacement starts can
leave the clipboard empty, but never publishes a partially populated transaction.
Native response events and the owner reply queue have a separate 2-MiB allowance
for base64 expansion; ordinary paste and input limits are unchanged.

The native callback borrows pointers only during the request. Managed code copies
them and returns the actual result before Ghostty encodes an acknowledgement.
Hosted sessions use a duplex effects channel with attachment identity, ordered
sequence, request ID and deadline checks. They never send a query back into the
paused terminal, wait for viewport credit, or replay an uncertain write after
disconnect. Detached sessions cannot access the GUI clipboard.

Windows can synchronously call another application's clipboard owner (including
delayed rendering). Background operations therefore allow only one outstanding
OS operation and release the terminal caller after two seconds. A timed-out OS
call may still finish; no success is reported and subsequent calls return busy
until it releases the clipboard. Requests that expire before replacement starts
are rejected; a timeout inside a Windows call can leave the final state uncertain.
The hosted request deadline is three seconds, and the owner stops waiting after
five seconds. Such failures are not automatically retried.

Ordinary copying, formatted copying and paste share the clipboard access gate.
The existing immutable selection and saved-frame behavior is unchanged.
