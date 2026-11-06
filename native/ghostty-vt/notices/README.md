These uucode notices are copied verbatim from the archive pinned by Ghostty:

- Commit: `ea62149739404a73c202b48a33bf6dd2af4bd9b0`
- Source: https://deps.files.ghostty.org/uucode-ea62149739404a73c202b48a33bf6dd2af4bd9b0.tar.gz
- Archive SHA-256: `f37c4071a217853450d084cb9c12b6d4b68f887eb9cb42fe77a84f86056eacbf`

Zig's package filtering excludes the additional `licenses/` directory even
though `LICENSE.md` refers to it. Include these copies in the release notice
bundle. Refresh them when the pinned uucode revision changes. The extra
`resources/wcwidth` notices are retained conservatively with the source notices.
