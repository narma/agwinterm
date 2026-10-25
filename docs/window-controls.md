# Native window controls

New operations target an open library window by exact ID or unique prefix. Omitted target
means the foreground library window. Quick and closed windows refuse; next/previous wrap
through open library order and refuse if there is no other open window.

```
agwintermctl window new --name Build --minimized --json
agwintermctl window next --target WINDOW
agwintermctl window previous --target WINDOW
agwintermctl window minimize --target WINDOW
agwintermctl window restore --target WINDOW
agwintermctl window fullscreen on --target WINDOW
agwintermctl window monitors --json
agwintermctl window move --target WINDOW --monitor primary --json
agwintermctl window geometry --target WINDOW --json
```

Window operations use `--target`, not `--window`. Fullscreen also accepts off/toggle.
Minimized creation never shows an ordinary active window first. Minimize/restore do not
request foreground activation; navigation deliberately selects its destination.
These operations return an object with id, rect, restoreRect, monitor, dpi, minimized,
maximized and fullscreen. Rectangles contain X/Y/Width/Height in physical desktop pixels;
negative coordinates are valid. A minimized window's rect is the OS minimized rectangle;
restoreRect describes its future normal placement. Monitor enumeration returns device id,
primary, bounds and workArea. Pass a returned device id or `primary` to --monitor.
Movement fits the normal size into that work area; minimized windows remain minimized.
The original coordinate-based move and ordinary window-new replies stay unchanged.

```
agwintermctl session reveal --target PANE --window WINDOW
agwintermctl session reveal --target PANE --path-only --json
```

Reveal selects the current directory in Explorer. It resolves only primary/split panes
in the routed window, and does not select the session first. WSL conversion uses that
pane's distribution and user. Unknown targets, non-absolute/missing directories and
conversion failures refuse. `--path-only` performs the same path validation and returns
`{path, opened:false}` without starting Explorer; normal success returns opened:true.
Explorer launch acceptance does not prove that the user has viewed its window.

Wire commands are window.next/previous/minimize/restore/fullscreen/monitors/geometry,
window.new with boolean minimized, window.move with string monitor, and session.reveal
with boolean path-only. Fullscreen uses args.op. New operations reject unknown/duplicate
fields, incorrect types and conflicting selectors before mutation. Existing close/delete
and last-window rules are unchanged.
