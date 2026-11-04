// Narrow embedding extension: prepend already-parsed pages without replaying
// bytes into the destination's live VT stream. Parsing/reflow remain upstream.
const std = @import("std");
const terminal = @import("terminal.zig");
const PageList = @import("../PageList.zig");
const lib = @import("../lib.zig");
const Result = @import("result.zig").Result;

pub fn prepend(destination_: terminal.Terminal, source_: terminal.Terminal, max_rows: u32, added: *usize) callconv(lib.calling_conv) Result {
    added.* = 0;
    const destination = destination_ orelse return .invalid_value;
    const source = source_ orelse return .invalid_value;
    if (destination == source) return .invalid_value;
    const dst = &destination.terminal.screens.get(.primary).?.pages;
    const src = &source.terminal.screens.get(.primary).?.pages;
    if (dst.cols != src.cols) return .invalid_value;
    const history = src.total_rows - src.rows;
    const count = @min(history, max_rows);
    if (count == 0) return .success;
    const alloc = destination.terminal.gpa();
    var prepared: std.ArrayList(PageList.PageAllocation) = .empty;
    defer {
        for (prepared.items) |*item| item.deinit();
        prepared.deinit(alloc);
    }
    var iterator = src.pageIterator(.left_up,
        .{ .screen = .{ .x = 0, .y = @intCast(history - count) } },
        .{ .screen = .{ .x = 0, .y = @intCast(history - 1) } });
    while (iterator.next()) |chunk| {
        var item = dst.allocatePage(chunk.node.capacity()) catch return .out_of_memory;
        item.page().size.rows = chunk.end - chunk.start;
        item.page().size.cols = chunk.node.cols();
        item.page().cloneFrom(chunk.node.page(), chunk.start, chunk.end) catch {
            item.deinit(); return .out_of_memory;
        };
        prepared.append(alloc, item) catch { item.deinit(); return .out_of_memory; };
    }
    // All allocating work precedes mutation. Retain the newest pages that fit
    // the destination's configured history limits, as the snapshot importer does.
    for (prepared.items) |*item| {
        const rows = item.page().size.rows;
        item.finalize(.prepend, .{}) catch break;
        added.* += rows;
    }
    return .success;
}
