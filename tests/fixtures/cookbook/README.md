# Web cookbook compatibility fixtures

Source baseline: [umputun/agterm at 714ec616c81add35627a1f8c3d5cb50ab9e030a1](https://github.com/umputun/agterm/tree/714ec616c81add35627a1f8c3d5cb50ab9e030a1/cookbook).
Copied templates and review adapter retain the MIT license in `LICENSE.agterm`.
The original `html-doc-overlay/assets/{editorial,brief,classic,custom}.html`,
`annotate-document/assets/page.html` and `session-snapshot/assets/page.html` are
kept as templates. `annotate-document/scripts/webrev.py` is adapted locally.
Bundled markdown-it 14.1.0 and highlight.js 11.11.1 were fetched from that same
commit; their MIT and BSD-3-Clause terms are in `LICENSE.markdown-it` and
`LICENSE.highlight.js`. Header notices remain in both library files.

These are executable web-surface fixtures, not full ports of the four recipes.
No scheduler, publishing service, restore command or third-party skill is installed.

Set `AGWINTERMCTL` to the actual CLI executable, `AGWINTERM_PIPE` to the instance's
pipe and `AGWINTERM_SESSION_ID` to an exact session ID. The adapters use ordinary
CLI JSON replies; Windows and WSL callers may select the appropriate CLI executable.
Python 3.9 or newer is required. No Python packages are needed.

```powershell
py tests/fixtures/cookbook/show.py tests/fixtures/cookbook/html-doc-overlay/assets/editorial.html
py tests/fixtures/cookbook/annotate-document/scripts/webrev.py review document.md --fit --output review.txt --timeout 120
py tests/fixtures/cookbook/annotate-document/scripts/webrev.py result PAGE_ID
```

`show.py` opens at 95% with native navigation without selecting a background owner,
reloads an existing page for the same file, replaces a different web page and refuses
over a program overlay, including a hidden one. Unlike upstream's macOS fallback,
outside an explicitly identified Agwinterm session it refuses instead of launching
the browser. Sharing and Markdown publishing are not included.

The review adapter explicitly grants `--js --bridge review`, unwraps
`result.pageOutcome`, maps `AGTERM_*` transport/session variables to `AGWINTERM_*`
and uses an exact pane ID only for status updates. Pane-local web overlays refuse.
The macOS sidebar tint setting has no direct equivalent: the fixture uses neutral
sidebar shift while host CSS variables follow Agwinterm's theme. Fit dimensions
are DIPs and zoom is read from `htmlOverlays`. Review exit codes are 0 submitted
(including empty), 2 dismissed, 1 error/pending; ordinary CLI code 2 still means
invalid arguments. `--timeout` leaves the page open and writes its pending ID to
the backup. Query that ID to recover a later result. Submission/dismissal replaces
the backup atomically; restart or eviction of the 32-finished-result history needs
the backup rather than an assumed persistent browser profile.

## Localhost dashboard

`py tests/fixtures/cookbook/scheduled-jobs/dashboard.py` prints its loopback URL.
Open it with `session overlay open --url URL --js --navigation --target SESSION_ID`.
The fixture uses htmx 2.0.4 from `bigskysoftware/htmx` tag `v2.0.4` (`dist/htmx.min.js`),
with the upstream Zero-Clause BSD license in `LICENSE.htmx`. Its run/toggle endpoints
and five-second refresh represent the scheduled-jobs web surface at the pinned
agterm baseline; actions change in-memory sample counters only. It does not install
cronn, launchd, Homebrew or a Windows scheduling replacement. No bridge is granted.

## Snapshot viewer

```powershell
py tests/fixtures/cookbook/session-snapshot/render.py tests/fixtures/cookbook/session-snapshot/sample.json snapshot.html --target SESSION_ID
```

This renders the upstream standalone search/details/copy viewer with embedded
sample data, then opens it with JavaScript and no host bridge. Upstream's snapshot
script launches an external browser using macOS `open`; this adapter changes only
the viewer launch and explanatory footer. Snapshot collection and restore are not
ported. Copying a command never executes it. Tests preserve the previous clipboard.
