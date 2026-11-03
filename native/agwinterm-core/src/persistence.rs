use super::*;
use crate::cell::ColorSpecKind;
use crate::persist::{PBuffer, PCell, PRow};
use prost::Message;

fn pack(c: Color) -> u32 {
    ((c.r as u32) << 16) | ((c.g as u32) << 8) | c.b as u32
}
fn color(v: u32) -> Color {
    Color {
        r: (v >> 16) as u8,
        g: (v >> 8) as u8,
        b: v as u8,
    }
}
fn spec(kind: u32, index: u32, rgb: u32) -> ColorSpec {
    ColorSpec {
        kind: match kind {
            1 => ColorSpecKind::Indexed,
            2 => ColorSpecKind::Rgb,
            _ => ColorSpecKind::Default,
        },
        index: index as u8,
        rgb: color(rgb),
    }
}

impl Emulator {
    pub fn serialize_buffer(&self, max_rows: usize, include_visible: bool) -> Vec<u8> {
        let mut rows = Vec::new();
        let mut add = |mut cells: Vec<Cell>, wrap: Option<usize>| {
            while cells.last() == Some(&Cell::EMPTY) {
                cells.pop();
            }
            rows.push(PRow {
                soft_wrapped: wrap.is_some(),
                wrap_length: wrap.unwrap_or(0) as u32,
                cells: cells
                    .into_iter()
                    .map(|c| {
                        let (parameters, uri) = self
                            .hyperlink(c.hyperlink)
                            .split_once(';')
                            .unwrap_or(("", ""));
                        PCell {
                            rune: c.rune,
                            fg: pack(c.foreground),
                            bg: pack(c.background),
                            attrs: c.attributes,
                            width: c.width as u32,
                            fg_kind: c.fg_spec.kind as u32,
                            fg_index: c.fg_spec.index as u32,
                            fg_rgb: pack(c.fg_spec.rgb),
                            bg_kind: c.bg_spec.kind as u32,
                            bg_index: c.bg_spec.index as u32,
                            bg_rgb: pack(c.bg_spec.rgb),
                            hyperlink_uri: uri.to_owned(),
                            hyperlink_parameters: parameters.to_owned(),
                        }
                    })
                    .collect(),
            });
        };
        for (i, row) in self.history.iter().enumerate() {
            add(row.clone(), self.history_wraps[i]);
        }
        if include_visible {
            for r in 0..self.screen().rows() {
                add(
                    (0..self.screen().cols())
                        .map(|c| self.screen().get(r, c))
                        .collect(),
                    self.screen().wraps[r],
                );
            }
        }
        while rows.last().is_some_and(|r| r.cells.is_empty()) {
            rows.pop();
        }
        if rows.len() > max_rows {
            rows.drain(0..rows.len() - max_rows);
        }
        PBuffer {
            version: 1,
            cols: self.screen().cols() as u32,
            rows,
        }
        .encode_to_vec()
    }

    pub fn restore_buffer(&mut self, bytes: &[u8]) -> bool {
        let Ok(buffer) = PBuffer::decode(bytes) else {
            return false;
        };
        if buffer.version != 1 {
            return false;
        }
        let mut restored_links = HashMap::new();
        for pr in buffer.rows {
            let mut row = Vec::with_capacity(pr.cells.len());
            for c in pr.cells {
                let hyperlink = if c.hyperlink_uri.is_empty() {
                    0
                } else {
                    let payload = format!("{};{}", c.hyperlink_parameters, c.hyperlink_uri);
                    *restored_links.entry(payload).or_insert_with_key(|payload| {
                        let id = self.next_hyperlink;
                        self.next_hyperlink = self
                            .next_hyperlink
                            .checked_add(1)
                            .expect("hyperlink id exhausted");
                        self.hyperlinks.insert(id, payload.clone());
                        id
                    })
                };
                row.push(Cell {
                    rune: c.rune,
                    foreground: color(c.fg),
                    background: color(c.bg),
                    attributes: c.attrs,
                    width: c.width as u8,
                    fg_spec: spec(c.fg_kind, c.fg_index, c.fg_rgb),
                    bg_spec: spec(c.bg_kind, c.bg_index, c.bg_rgb),
                    hyperlink,
                });
            }
            self.history.push(row);
            self.history_wraps.push(if pr.soft_wrapped {
                Some(pr.wrap_length.min(32768) as usize)
            } else {
                None
            });
        }
        self.restored_rows = self.history.len();
        self.reflow_restored(self.screen().cols());
        true
    }

    // The restored prefix is immutable terminal output, independent of ConPTY's live repaint.
    pub(super) fn reflow_restored(&mut self, cols: usize) {
        if self.restored_rows == 0 {
            return;
        }
        let mut rows = Vec::new();
        let mut wraps = Vec::new();
        let mut logical = Vec::new();
        let mut origins = Vec::new();
        let mut mapping = vec![0; self.restored_rows];
        for h in 0..self.restored_rows {
            origins.push((h, logical.len()));
            let len = self.history_wraps[h].unwrap_or(self.history[h].len());
            for c in 0..len {
                logical.push(self.get_history_cell(h, c));
            }
            if self.history_wraps[h].is_some() && h + 1 < self.restored_rows {
                continue;
            }
            let mut row = Vec::new();
            let mut c = 0;
            let mut origin = 0;
            while c < logical.len() {
                let width = if logical[c].width == 2
                    && c + 1 < logical.len()
                    && logical[c + 1].width == 0
                {
                    2
                } else {
                    1
                };
                if !row.is_empty() && row.len() + width > cols {
                    wraps.push(Some(row.len()));
                    rows.push(std::mem::take(&mut row));
                }
                while origin < origins.len() && origins[origin].1 <= c {
                    mapping[origins[origin].0] = rows.len();
                    origin += 1;
                }
                row.extend_from_slice(&logical[c..c + width]);
                c += width;
            }
            while origin < origins.len() {
                mapping[origins[origin].0] = rows.len();
                origin += 1;
            }
            rows.push(row);
            wraps.push(None);
            logical.clear();
            origins.clear();
        }
        let old_count = self.restored_rows;
        self.restored_rows = rows.len();
        self.history.splice(0..old_count, rows);
        self.history_wraps.splice(0..old_count, wraps);
        let map = |line: i64| {
            if line < 0 {
                line
            } else if (line as usize) < old_count {
                mapping[line as usize] as i64
            } else {
                line + self.restored_rows as i64 - old_count as i64
            }
        };
        for m in &mut self.marks {
            m.prompt_line = map(m.prompt_line);
            m.command_line = map(m.command_line);
            m.output_line = map(m.output_line);
            m.end_line = map(m.end_line);
        }
        if self.history.len() > self.scrollback_max + TRIM_SLACK {
            self.trim_history();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn text(e: &Emulator, row: usize) -> String {
        (0..e.history_row_stored_len(row))
            .filter_map(|c| {
                let cell = e.get_history_cell(row, c);
                if cell.width == 0 {
                    None
                } else {
                    char::from_u32(cell.rune as u32)
                }
            })
            .collect::<String>()
            .trim_end()
            .to_owned()
    }
    #[test]
    fn soft_wraps_and_hard_ends_survive_narrow_save_and_reflow() {
        let mut source = Terminal::new(5, 2);
        source.feed(b"abcdefghijk\r\n12345\r\nend");
        let mut bytes = source.emu.serialize_buffer(500, true);
        for width in [20, 3, 1, 7, 20] {
            let mut restored = Emulator::new(5, 4);
            assert!(restored.restore_buffer(&bytes));
            restored.resize(width, 4);
            bytes = restored.serialize_buffer(500, true);
            if width == 20 {
                assert_eq!(restored.history_count(), 3);
                assert_eq!(text(&restored, 0), "abcdefghijk");
                assert_eq!(text(&restored, 1), "12345");
                assert_eq!(text(&restored, 2), "end");
            }
        }
    }
    #[test]
    fn wide_padding_styles_and_links_roundtrip_without_id_collision() {
        let mut source = Terminal::new(5, 3);
        source.feed("\x1b]8;id=one;https://example.com\x1b\\\x1b[31mabcd中  😀z".as_bytes());
        let mut restored = Terminal::new(20, 4);
        assert!(
            restored
                .emu
                .restore_buffer(&source.emu.serialize_buffer(500, true))
        );
        assert_eq!(text(&restored.emu, 0), "abcd中  😀z");
        let before = restored.emu.serialize_buffer(500, false);
        restored.emu.resize(1, 4);
        restored.emu.resize(20, 4);
        assert_eq!(before, restored.emu.serialize_buffer(500, false));
        let id = restored.emu.get_history_cell(0, 0).hyperlink;
        let link = restored.emu.hyperlink(id).to_owned();
        restored.feed(b"\x1b]8;;https://new.test\x1b\\new");
        assert_eq!(restored.emu.hyperlink(id), link);
        assert_eq!(link, "id=one;https://example.com");
    }
    #[test]
    fn legacy_unicode_lines_keep_content_and_boundaries() {
        let mut restored = Emulator::new(3, 3);
        restored.seed_scrollback(&["ab中😀z", "next"]);
        restored.resize(20, 3);
        assert_eq!(text(&restored, 0), "ab中😀z");
        assert_eq!(text(&restored, 1), "next");
    }
    #[test]
    fn reflow_cap_keeps_newest_cells() {
        let mut restored = Emulator::new(1000, 3);
        restored.set_scrollback_max(10);
        let line = format!("{}YZ", "x".repeat(998));
        restored.seed_scrollback(&[&line]);
        restored.resize(1, 3);
        assert_eq!(restored.history_count(), 10);
        restored.resize(1000, 3);
        assert_eq!(text(&restored, 0), "xxxxxxxxYZ");
    }
}
