# Native questions

`agwintermctl ask open` reads one UTF-8 JSON form from stdin and waits for its exact
request ID. `--no-block` returns `{"id":"..."}` immediately. `ask result ID` reads an
outcome and `ask cancel ID` cancels a pending request. All CLI output is JSON, including
without `--json`. `--pipe`/`--socket` select transport; `--window` selects an owner or
restricts result/cancel lookup. Optional `ask open --target EXACT_SESSION_UUID` pins
the question to a session in that selected window. Names, prefixes and `active` are
refused for this option; the window and session are resolved before the question opens.
Closing that session (including soft-close or workspace disposal) settles the question
as `cancelled` with reason `owner-closed`; result retrieval keeps its original ownership.
Without `--target`, questions retain window-only ownership, with no caller/active-session
fallback. `ask result ID` and `ask cancel ID` still use exact question IDs, never session IDs.
`--follow` requests Windows activation; omission preserves foreground policy.

An explicitly session-targeted question may be delegated to that exported session's
presenter. The supported remote subset is one field, up to six buttons and fifty choices.
Delegated single-choice questions follow agterm: the first item starts selected and
nonblank Other excludes a normal selection. These rules do not change locally presented
forms. Unsupported forms remain local; window-only questions are never exported.

```json
{
  "title": "Deployment decision",
  "prompt": "Review the proposed action",
  "fields": [
    {"id":"note","label":"Comment","type":"multiline"},
    {"id":"targets","label":"Targets","type":"multi","allowOther":true,
     "choices":[{"id":"test","label":"Test"},{"id":"stage","label":"Staging"}]}
  ],
  "buttons": [{"id":"approve","label":"Approve","hotkey":"д"},
              {"id":"revise","label":"Revise","hotkey":"r"}]
}
```

All properties are optional at the form level. Fields require nonempty `id`, `label`
and `type`: `text`, `multiline`, `single` or `multi`. Choice fields require a nonempty
`choices` array of unique ID/label pairs; only they accept boolean `allowOther`.
No fields are mandatory to answer. Initial text and selections are empty. Buttons
require unique nonempty IDs/labels; omitted buttons give `submit`, while an explicitly
empty array refuses. Hotkeys are unique single letters or digits, case-insensitive.
Unknown/duplicate properties, duplicate IDs, invalid types and display controls refuse.
Labels and answers are data, never shell commands or markup.

One field is shown per page. Back/Next preserve values, Tab/Shift+Tab traverse native
controls, multiline Enter inserts a newline, and Alt+hotkey uses the active layout
(including Cyrillic). Submit actions remain available on every page. Native controls
expose labels, values and selection state. Single choices may remain unselected;
multiple selections return IDs in the original input order.
On short or high-DPI screens, the form scrolls vertically rather than overlapping its
fields and buttons. The scrollbar and mouse wheel move through the form; keyboard
focus automatically reveals the selected control.
Native accessibility is verified through MSAA. The existing process-wide UI Automation
proxy limitation remains; this stage does not claim to repair it or certify a screen reader.

Wire commands are `ask.open` with `args:{form:{...},follow:false}`, `ask.result` and
`ask.cancel` with exact `target` ID. They retain ordinary `{ok,result}` envelopes;
open result is `{id}`, result is `{ask:OUTCOME}`, cancel result is `"cancelled"`.
CLI cancellation acknowledgement is `{id,cancelled:true}`; it does not overwrite an
already answered outcome. Outcome payloads:

```json
{"result":"pending"}
{"result":"answered","button":"approve","values":{"note":"Ready","targets":{"selected":["test"],"other":""}}}
{"result":"cancelled","reason":"escape"}
```

Text fields return strings. Choice fields return `{selected:[IDs],other:string|null}`;
other is null when unavailable and an empty string when available but blank.
Cancellation reasons: `cancel` for button/API, `escape`, `closed` for dialog X, and
`owner-closed`. First completion wins; repeated reads/cancel cannot mutate it.

Limits: 1 MiB UTF-8 form, 16 fields, 64 choices per field/512 total, 8 buttons,
4096 UTF-16 units per ID/display/single-line value/other text, 16384 per multiline
answer. Multiline permits CR/LF/tab; other control characters refuse. Completed asks
retain the latest 8 per live window and 32 from closed windows in completion order.
Restart forgets them. Ask and pick share one pending dialog slot per owner and refuse
overlapping settings/edit/menu/drag operations; the quick terminal cannot own one.

Exit codes: answered/no-block/cancel 0, cancelled outcome 2, pending/refusal/transport
failure 1, invalid usage/form 2, Ctrl+C 130. Blocking polls only the created exact ID
without a moving window selector or answer deadline (100 ms for 10 polls, then 500 ms).
Each transport exchange is bounded to 15 seconds, including a 3-second connection
limit; native queued opening withdraws after 10 seconds. A failed/interrupted wait
makes one best-effort exact-ID cancel. If the open reply is lost, creation may have
occurred without a known ID: no retry or guessed cancellation is safe, and the owned
dialog can require user Escape/Cancel. No-block intentionally leaves it pending.

The native WSL bridge obtains a no-block ID internally and performs exact-ID polling
so real terminal SIGINT can cancel the owned form. Result/cancel and explicit no-block
remain ordinary Windows CLI calls. This does not change payloads or exit conventions.
