## Native picker (P16, full app; lite mirror pending)
- `--selected-id ID` chooses an exact initial item, including empty IDs; later query edits reset selection. Item JSON accepts status text, color #RRGGBB and shape dot|square|diamond; status is accessible but not searched.
- Pipe UTF-8 lines or JSON `[{"id":"a","label":"Alpha","subtitle":"optional consequence"}]` to `agwintermctl pick [open]`.
- `--prompt TEXT --query TEXT --allow-custom --window ID --follow --no-block`; no session target. Default display does not steal foreground; follow requests OS activation. Auto input treats leading `[` as JSON; use `--input-format lines` for `[Draft]`-style line labels, or `json` to require JSON.
- `--no-block` returns JSON `{id}`. Otherwise waits by exact id without a window selector; picked/custom exit 0, cancelled 2. Ctrl+C/failure best-effort cancels the owned pending picker.
- `pick result ID` prints the JSON outcome (`result: pending|picked|custom|cancelled`); pending exits 1. Picked adds item id/label/original index; custom adds trimmed query.
- `pick cancel ID` is idempotent for retained answers. Result/cancel optionally restrict `--window`; omit it for global exact-ID lookup across focus/owner closure.
- One pending/window, eight completed/window, 32 closed-window answers. Quick refuses. Items never execute; matching searches labels only, custom appears only when no item matches.
- Max 1000 items, 4096 UTF-16 units/field, 1 MiB UTF-8 stdin/open args; duplicate IDs and display controls refuse. Open/result JSON is payload even with --json; cancel uses the normal envelope under --json.
- Filtering work caps total label UTF-16 units times distinct query terms at 64 Mi. Over-complex edited queries disable choice until corrected. Mouse capture blocks opening; file drops on the owner are discarded.
- Initial UI open waits 10s and withdraws/rolls back on timeout. Best-effort cancel needs the received picker ID; a lost successful open reply can require user Escape/Cancel. No exactly-once transport guarantee.

## Native questions (C3, full app)
- Pipe a UTF-8 JSON form to `agwintermctl ask [open] [--window ID] [--follow] [--no-block]`. No-block returns `{id}`; otherwise it waits on that exact ID. `ask result ID` reads it; `ask cancel ID` is idempotent for retained results. Transport: --pipe/--socket, optional --json; output is always JSON.
- Form: optional title/prompt, fields with id/label/type text|multiline|single|multi; choices have id/label and optional allowOther belongs to the choice field. Buttons have id/label and optional one-character hotkey (Alt+character in current layout). Omitted buttons give submit. Labels/answers never execute.
- Native Back/Next retain fields; Tab navigates; multiline Enter inserts a newline. Cancel, Escape, dialog close and owner close produce reasons cancel, escape, closed and owner-closed. Answer adds button and values; text values are strings, choices are `{selected:[IDs],other:string|null}`.
- One ask/pick per owner, conflicting modal UI and quick refuse. Limits: 1 MiB form, 16 fields, 64 choices/field and 512 total, 8 buttons, 4096 UTF-16 display/text units and 16384 multiline answer units. Eight completed asks/live window and 32 from closed windows, memory only.
- Exit: answer/no-block/cancel 0, cancelled outcome 2, pending/refusal/transport 1, usage 2, Ctrl+C 130. A failed blocking wait best-effort cancels its known ID. A lost open reply can leave a dialog requiring user cancellation; never retry open blindly.

## Notify the user (desktop notification)
- `agwintermctl notify "build finished" [--title "npm"] [--category ok|normal|attention] [--target <id>]`
  — raise a notification against a session: an in-app banner (click it to jump to that session),
  a count badge on the session's sidebar row (green for ok, yellow for normal, red for attention
  or an omitted category; mixed unread messages show the highest priority), cleared when you next
  select it, and an OS
  tray balloon (unless `desktop-notifications = false` in the config). Great for signaling that a
  long task finished or that you need attention while the user is looking at another session.
  You can also emit it straight from the shell with an OSC sequence: `printf '\e]9;%s\a' "message"`
  (or OSC 777: `\e]777;notify;Title;Body\a`).
