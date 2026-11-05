// Unicode case folding for terminal search. Retain the source cell for every
// produced byte, including expansions (sharp s, dotted I, ligatures). The
// sliding window can then keep its existing page overlap and highlight logic.
const std = @import("std");
const uucode = @import("uucode");
const Coordinate = @import("../point.zig").Coordinate;

pub fn fold(
    alloc: std.mem.Allocator,
    input: []const u8,
    source_map: ?[]const Coordinate,
    output_map: ?*std.ArrayList(Coordinate),
) std.mem.Allocator.Error![]u8 {
    var bytes: std.ArrayList(u8) = .empty;
    errdefer bytes.deinit(alloc);
    try bytes.ensureTotalCapacity(alloc, input.len);
    var iterator = (std.unicode.Utf8View.init(input) catch unreachable).iterator();
    while (iterator.i < input.len) {
        const offset = iterator.i;
        const cp = iterator.nextCodepoint().?;
        var single: [1]u21 = undefined;
        const folded = if (cp < 128) blk: {
            single[0] = std.ascii.toLower(@intCast(cp));
            break :blk single[0..];
        } else uucode.get(.case_folding_full, cp).with(&single, cp);
        for (folded) |value| {
            var encoded: [4]u8 = undefined;
            const len = std.unicode.utf8Encode(value, &encoded) catch unreachable;
            try bytes.appendSlice(alloc, encoded[0..len]);
            if (output_map) |map| try map.appendNTimes(alloc, source_map.?[offset], len);
        }
    }
    return bytes.toOwnedSlice(alloc);
}

test "search Unicode full folding retains original cell coordinates" {
    const alloc = std.testing.allocator;
    const input = "Straße Σς İ ﬃ 𐐀";
    var source: [input.len]Coordinate = undefined;
    for (&source, 0..) |*p, i| p.* = .{ .x = @intCast(i), .y = 0 };
    var map: std.ArrayList(Coordinate) = .empty;
    defer map.deinit(alloc);
    const result = try fold(alloc, input, &source, &map);
    defer alloc.free(result);
    try std.testing.expectEqualStrings("strasse σσ i\u{307} ffi 𐐨", result);
    try std.testing.expectEqual(result.len, map.items.len);
    try std.testing.expectEqual(@as(u16, 4), map.items[4].x);
    try std.testing.expectEqual(map.items[4], map.items[5]);
}
