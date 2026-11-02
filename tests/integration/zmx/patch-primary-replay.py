"""Preserve primary content/history when attaching while alternate is active."""
from pathlib import Path
import sys

path = Path(sys.argv[1]) / "src/util.zig"
source = path.read_text()
old = """    // If state contains color override, restore it.
    writeColorOverrides(&builder.writer, term);"""
signature = "pub fn serializeTerminalState(alloc: std.mem.Allocator, term: *ghostty_vt.Terminal) ?[]const u8 {"
wrapper = """pub fn serializeTerminalState(alloc: std.mem.Allocator, term: *ghostty_vt.Terminal) ?[]const u8 {
    if (term.screens.active_key != .alternate) return serializeActiveScreen(alloc, term);
    // Snapshot both buffers without changing the live terminal. Enter alternate
    // exactly once, after the saved primary cursor has been reconstructed.
    var primary = term.*;
    primary.screens.switchTo(.primary);
    primary.modes.set(.alt_screen_legacy, false);
    primary.modes.set(.alt_screen, false);
    primary.modes.set(.alt_screen_save_cursor_clear_enter, false);
    const main_bytes = serializeActiveScreen(alloc, &primary) orelse return null;
    defer alloc.free(main_bytes);
    var alternate = term.*;
    alternate.modes.set(.alt_screen_legacy, false);
    alternate.modes.set(.alt_screen, false);
    alternate.modes.set(.alt_screen_save_cursor_clear_enter, false);
    const alt_bytes = serializeActiveScreen(alloc, &alternate) orelse return null;
    defer alloc.free(alt_bytes);
    return std.mem.concat(alloc, u8, &.{ "\\x1b[?1049l\\x1b[H", main_bytes,
        "\\x1b[?1049h\\x1b[H", alt_bytes }) catch null;
}

fn serializeActiveScreen(alloc: std.mem.Allocator, term: *ghostty_vt.Terminal) ?[]const u8 {"""
assert source.count(signature) == 1
source = source.replace(signature, wrapper)
needle = '        // Clear visible screen after scrollback.'
assert source.count(needle) == 1
source = source.replace(needle, '''        // Push every serialized history row out of the viewport before ED2.
        // Otherwise the last viewport's worth of history is silently erased.
        for (0..pages.rows + 1) |_| builder.writer.writeAll("\\r\\n") catch return null;

''' + needle)
source += r'''

test "agwinterm full primary history survives alternate snapshot" {
    const alloc = testing.allocator;
    const io = testing.io;
    var term = try testCreateTerminal(alloc, io, 80, 24, "");
    defer term.deinit(alloc);
    var input = term.vtStream();
    defer input.deinit();
    var buf: [40]u8 = undefined;
    for (0..90) |i| input.nextSlice(try std.fmt.bufPrint(&buf, "HISTORY-{d:0>3}\r\n", .{i}));
    input.nextSlice("\x1b[?1049h\x1b[HCOLORED-ALT\x1b[4;7H");
    var client = try serializeRoundtrip(alloc, io, &term);
    defer client.deinit(alloc);
    try expectScreensMatch(alloc, &term, &client);
    try expectCursorAt(&client, 3, 6);
    var output = client.vtStream();
    defer output.deinit();
    input.nextSlice("\x1b[?1049l");
    output.nextSlice("\x1b[?1049l");
    const text = serializeTerminal(alloc, &client, .plain) orelse return error.SerializationFailed;
    defer alloc.free(text);
    for (0..90) |i| {
        const marker = try std.fmt.bufPrint(&buf, "HISTORY-{d:0>3}", .{i});
        try testing.expectEqual(@as(usize, 1), std.mem.count(u8, text, marker));
    }
}
'''
path.write_text(source)
