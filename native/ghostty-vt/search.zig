// Embedding-only Unicode entry point. All scheduling, page ownership and
// reflow/eviction tracking remain in Ghostty's existing TerminalSearch.
const std = @import("std");
const search = @import("search.zig");
const TerminalSearch = @import("../search/terminal.zig").TerminalSearch;
const lib = @import("../lib.zig");
const Result = @import("result.zig").Result;
const Selection = @import("selection.zig").CSelection;

pub fn unicodeNeedle(handle: search.Search, bytes: ?[*]const u8, len: usize) callconv(lib.calling_conv) Result {
    const wrapper = handle orelse return .invalid_value;
    const terminal = wrapper.terminal orelse return .invalid_value;
    if (len > 65536 or (len != 0 and bytes == null)) return .invalid_value;
    if (len == 0) return search.set(handle, .needle, null);
    const text = bytes.?[0..len];
    if (!std.unicode.utf8ValidateSlice(text)) return .invalid_value;
    const replacement = TerminalSearch.initMode(wrapper.alloc, text, true) catch return .out_of_memory;
    if (wrapper.search) |*old| old.deinit(terminal.terminal);
    wrapper.search = replacement;
    return .success;
}

// Unlike the public C tick, do not read wrapper.terminal: restore may detach
// that pointer concurrently. This operates exclusively on search-owned copies.
pub fn tick(handle: search.Search, status: *u32) callconv(lib.calling_conv) Result {
    const wrapper = handle orelse return .invalid_value;
    const state = if (wrapper.search) |*s| s else { status.* = 2; return .success; };
    var it = state.screens.iterator();
    while (it.next()) |entry| entry.value.tick() catch |err| switch (err) {
        error.OutOfMemory => return .out_of_memory,
        error.SearchComplete, error.FeedRequired => {},
    };
    status.* = switch (state.status()) { .running => 0, .feed_required => 1, .complete => 2 };
    return .success;
}

// Bounded export avoids allocating every upstream match just to return a capped
// result set. Requires terminal access, exactly like the public matches getter.
pub fn matches(handle: search.Search, offset: usize, output: [*]Selection, capacity: usize, total: *usize) callconv(lib.calling_conv) Result {
    const wrapper = handle orelse return .invalid_value;
    if (wrapper.terminal == null) return .invalid_value;
    const state = if (wrapper.search) |*s| s else { total.* = 0; return .success; };
    const screen = state.activeScreenSearch() orelse { total.* = 0; return .success; };
    total.* = screen.matchesLen();
    const count = @min(capacity, total.* -| offset);
    for (0..count) |i| {
        const untracked = screen.matchAt(offset + i).?.untracked();
        output[i] = .{ .start = .fromPin(untracked.start), .end = .fromPin(untracked.end), .rectangle = false };
    }
    return .success;
}
