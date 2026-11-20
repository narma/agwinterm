## Show an image inline
- `agwintermctl image show C:\path\pic.png --row 2 --col 4`
- `agwintermctl image sixel C:\path\pic.six [--row R --col C]` — unsupported by the Ghostty adapter; use Kitty graphics or `image show` instead
- `agwintermctl image frameshm <Local\agwinterm-frame-NAME> [--slot N] [--seq N] ...` — display
  a high-rate raw BGRA/RGBA frame from a versioned shared-memory mapping. This is a specialized
  producer path; query `agwintermctl session metrics [<pane-id>] --json` before sizing it and
  follow https://github.com/narma/agwinterm/blob/narma/docs/specs/image-frameshm.md for the
  versioned contract. Ordinary image files should use `image show`.
Note: Windows ConPTY strips inline terminal-graphics sequences, so images MUST be delivered
through this control channel — not by printing escape codes to stdout.
