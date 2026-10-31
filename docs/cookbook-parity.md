# Embedded web cookbook coverage

The tested source baseline is
[`umputun/agterm` commit `714ec616c81add35627a1f8c3d5cb50ab9e030a1`](https://github.com/umputun/agterm/tree/714ec616c81add35627a1f8c3d5cb50ab9e030a1/cookbook).
Fixtures, adapters and third-party notices are in
[`tests/fixtures/cookbook`](../tests/fixtures/cookbook/README.md).

| Recipe | Embedded web surface and current evidence | Deliberate differences / remaining checks |
| --- | --- | --- |
| html-doc-overlay | All four pinned templates open; show adapter reloads the same page ID and refuses hidden program overlays; background opening and bounded loading errors pass; host theme aliases update without losing edits | Explicit session/pipe replaces AGTERM transport. External-browser fallback and publishing are not ported. |
| annotate-document | Actual template submits file/block annotations and empty Submit; Cancel maps to exit 2; final output backups pass; timeout keeps a pending backup ID and recoverable result; two reviews isolate saved notes across reload and retain drafts across theme/session changes | Adapter explicitly selects bounded `--bridge review` and a session-wide page by default; native API also supports pane pages. Neutral sidebar tint and DIP sizing. |
| scheduled-jobs | Representative pinned htmx 2.0.4 localhost fixture: Run, Toggle, filtering and five-second refresh tested through the embedded page, without host bridge | Sample counters only. Scheduler installation, launchd/Homebrew/cronn deployment and Windows scheduling parity are outside this change |
| session-snapshot | Pinned standalone viewer rendered and launched embedded; search, keyboard selection/details and user-initiated copy tested; exact restore text does not create a session | Upstream uses macOS `open` for an external browser. Collection and restoration are not ported. |

These four rows describe web dependencies, not complete recipe ports. The remaining
42 recipes at this baseline have no direct embedded-web dependency in the reviewed
inventory; this is not a claim that they work on Windows. `sqlite-browser`, for
example, uses a terminal program. HTML publishing still has separate native ask,
Python hosting and cloudflared requirements.

URL navigation follows agterm's explicit `--browse` split: without it the original
origin is pinned; with it HTTP(S) redirects/links may change sites. The representative
dashboard acceptance run opts into browse to test both modes. Browse does not imply
JS, persistent storage or a host bridge. Local review isolation is unchanged.

The local parity extension adds split-pane pages, explicit resource-directory grants,
opt-in persistent URL storage, general trusted-local/declarative commands, blocking
page results, PNG/JPEG/GIF/WebP images and shell-less companions. It also provides
three-second live-document Undo, shared saved zoom, original/current reload,
native identity/history/copy/reveal controls and Settings → General → Open links in.
Tests cover actual Ctrl-click opening, native file paste refusal, form commands with
document scripts disabled, cross-window bridge overrides and persistent storage
across application restart. See the separate local-extension evidence in
[release validation](webview2-release-validation.md).

Deliberate platform differences: Ctrl-click replaces macOS Cmd-click; Windows
Explorer replaces Finder; transport is the Agwinterm named pipe. Theme changes
preserve local documents and drafts instead of reloading them. The review adapter
keeps its explicit narrower bridge. Remote display/replication is deferred to a
separate change by user decision; agterm/zmx compatibility is the likely direction,
not yet selected. Full browser tabs, extensions, recipe deployment, publishing and
scheduler installation are not included.

Development validation on 2026-10-31 used Windows 11 Pro build 26300,
Microsoft.Web.WebView2 SDK 1.0.4314.35, Evergreen Runtime 156.0.4314.58 and .NET SDK
10.0.303. Evidence: `artifacts/webview2-cookbook-test.log`,
`artifacts/webview2-navigation-test.log`, `artifacts/webview2-wsl-resources.log`,
`artifacts/webview2-program-regression.log` and `artifacts/webview2-outcomes-tests.log`.
The same cookbook suite passes the published installer payload and isolated portable
exe, including busy-terminal/dashboard interaction and simulated missing-runtime
refusal. See [release validation](webview2-release-validation.md) for package,
startup and regression evidence. The user accepted synthetic DPI checks and will
verify IME elsewhere. Clean-machine and disconnected-machine checks remain manual
release requirements; simulated missing-runtime refusal does not claim those passes.
