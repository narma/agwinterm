using System.Text;
using Agwinterm.Core;

if (args.Length != 1) throw new ArgumentException("Pass the directory containing both Ghostty DLLs.");
int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    checks++; Console.WriteLine("PASS: " + name);
}
void Capture(GhosttyVt vt) { vt.BeginCapture(); vt.CompleteCapture(); }
string Row(GhosttyVt vt, int row) => string.Concat(Enumerable.Range(0, vt.Frame.Cols).Select(col => vt.Frame[row, col].Text)).TrimEnd();

Check(!GhosttyVt.TryLoad(Path.Combine(args[0], "missing-contract-fixture"), out _), "missing native package is rejected");
string mismatchDirectory = Path.Combine(Path.GetTempPath(), "agwinterm-ghostty-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(mismatchDirectory);
try
{
    string manifest = Path.Combine(mismatchDirectory, "ghostty-vt.provenance.json");
    File.Copy(Path.Combine(args[0], "ghostty-vt.provenance.json"), manifest);
    File.WriteAllBytes(Path.Combine(mismatchDirectory, "ghostty-vt.dll"), [0]);
    Check(!GhosttyVt.TryLoad(mismatchDirectory, out string? mismatch) && mismatch!.Contains("hash mismatch"),
        "mismatched upstream DLL is rejected before loading");
    File.WriteAllText(manifest, "{}");
    Check(!GhosttyVt.TryLoad(mismatchDirectory, out _), "malformed provenance is rejected");
}
finally
{
    foreach (string file in Directory.GetFiles(mismatchDirectory)) File.Delete(file);
    Directory.Delete(mismatchDirectory);
}
if (!GhosttyVt.TryLoad(args[0], out string? error)) throw new InvalidOperationException(error);

using (var vt = new GhosttyVt(20, 5))
{
    vt.Feed("Hello\r\n\u001b[31;1mred\u001b[0m"u8); Capture(vt);
    Check(Row(vt, 0) == "Hello" && Row(vt, 1) == "red", "native text and CRLF");
    Check(vt.Frame[1, 0].FgSpec.Kind == ColorSpecKind.Indexed && vt.Frame[1, 0].FgSpec.Index == 1 &&
        vt.Frame[1, 0].Attributes.HasFlag(CellAttributes.Bold), "indexed color and style");
    Check(vt.Frame[1, 0].FgSpec == ColorSpec.Indexed(1) && vt.Frame[0, 0].FgSpec == ColorSpec.Default,
        "native colors use canonical value semantics");
    Capture(vt); Check(vt.ChangedRows == 0, "unchanged frame exports no rows");
    vt.Feed("!"u8); Capture(vt); Check(vt.ChangedRows == 1, "single-row update");
    vt.Feed("\u001b[?1049h\u001b[HALT"u8); Capture(vt); Check(vt.IsAltScreen && Row(vt, 0) == "ALT", "alternate screen");
    vt.Feed("\u001b[?1049l"u8); Capture(vt); Check(!vt.IsAltScreen && Row(vt, 0) == "Hello", "primary screen restored");
    vt.Feed("\u001b[?2004h\u001b[?1004h\u001b[?1006h"u8);
    Check((vt.Modes & ((1u << 5) | (1u << 6) | (1u << 3))) == ((1u << 5) | (1u << 6) | (1u << 3)), "paste focus and mouse modes");
    vt.Resize(40, 8); Capture(vt); Check(vt.Frame.Cols == 40 && vt.Frame.Rows == 8, "resize");
    vt.Feed(ReadOnlySpan<byte>.Empty); Capture(vt); Check(vt.ChangedRows == 0, "empty input");
}
using (var vt = new GhosttyVt(20, 5))
{
    vt.SetCursorDefault(CursorStyle.Bar, false); Capture(vt);
    Check(vt.FrameCursorShape == 6, "configured steady bar cursor reaches the frame");
    vt.Feed("\u001b[3 q"u8); Capture(vt);
    Check(vt.FrameCursorShape == 3, "DECSCUSR cursor style comes from Ghostty");
    vt.Feed("\u001bc"u8); Capture(vt);
    Check(vt.FrameCursorShape == 6, "RIS restores configured cursor defaults");
    var host = new Host { Colors = (new Color(0x12, 0x34, 0x56), new Color(0x23, 0x45, 0x67)) }; vt.Host = host;
    vt.Feed("\u001b]10;?\a\u001b]11;?\a"u8);
    Check(host.Replies.Contains("1212/3434/5656") && host.Replies.Contains("2323/4545/6767"), "color queries use host theme defaults");
    vt.Feed("\u001b]11;rgb:aa/bb/cc\a"u8);
    host.Colors = (new Color(1, 2, 3), new Color(4, 5, 6));
    vt.Feed("\u001b]11;?\a"u8);
    Check(vt.Background == 0xaabbcc, "theme changes preserve explicit OSC background overrides");
    vt.Feed("\u001b]111\a"u8);
    Check(vt.Background == 0x040506, "OSC color reset returns to updated host defaults");
}
byte[] content = Encoding.UTF8.GetBytes("01234567890123456789中e\u0301👩🏽‍💻\r\n\u001b[38;2;12;34;56mcolor\u001b[0m\r\nlast");
using (var whole = new GhosttyVt(12, 6))
{
    whole.Feed(content); Capture(whole);
    foreach (int chunk in new[] { 1, 2, 3, 7, 17, 64 })
    {
        using var split = new GhosttyVt(12, 6);
        for (int offset = 0; offset < content.Length; offset += chunk)
            split.Feed(content.AsSpan(offset, Math.Min(chunk, content.Length - offset)));
        Capture(split);
        bool equal = true;
        for (int row = 0; row < 6; row++)
        for (int col = 0; col < 12; col++) equal &= whole.Frame[row, col] == split.Frame[row, col];
        Check(equal && whole.CursorRow == split.CursorRow && whole.CursorCol == split.CursorCol, $"UTF-8/VT chunk continuity ({chunk})");
    }
    Check(Enumerable.Range(0, 6).Any(row => Row(whole, row).Contains("e\u0301")), "combining cluster retained");
    Check(Enumerable.Range(0, 6).Any(row => Row(whole, row).Contains("👩🏽‍💻")), "ZWJ/skin-tone cluster retained");
    Check(Enumerable.Range(0, 6).Any(row => whole.GetFrameWrapLength(row) >= 0), "soft-wrap metadata");
}
using (var vt = new GhosttyVt(10, 3))
{
    var host = new Host(); vt.Host = host;
    vt.Feed("abc\u001b[6n\a"u8);
    Check(host.Replies == "\u001b[1;4R" && host.Bells == 1, "ordered PTY response and bell effects");
    vt.Feed([0xff, 0xfe, 0xe2]); vt.Feed("A"u8); Capture(vt);
    Check(vt.Frame.Cols == 10, "invalid UTF-8 does not crash");
    vt.SetScrollbackLimit(100);
    vt.Feed(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("line\r\n", 50))));
    Check(vt.HistoryCount > 0, "native scrollback");
    var row = new Cell[10];
    vt.ReadRow(vt.HistoryCount - 1, true, row);
    Check(string.Concat(row.Select(cell => cell.Text)).TrimEnd() == "line", "history row bulk read");
}
using (var vt = new GhosttyVt(40, 8))
{
    var host = new Host(); vt.Host = host;
    vt.Feed("\u001b]2;ghostty test\a\u001b]7;file://host/tmp/work\a"u8);
    Check(vt.Title == "ghostty test" && vt.Cwd.Contains("/tmp/work"), "title and cwd effects");
    vt.Feed("\u001b]777;notify;title;body\a\u001b]9;4;1;42\a\u001b]52;c;aGVsbG8=\a"u8);
    Check(host.Notification == "title:body" && host.ProgressValue == 42, "notification and progress effects");
    Check(host.Clipboard == "hello", "write-only clipboard effect");
    vt.Feed("\u001b]52;c;?\a"u8); Check(host.Replies.Length == 0, "clipboard reads remain disabled");
    vt.Feed("\u001b]777;agwinterm;command-finished;0123456789abcdef0123456789abcdef;1\a"u8);
    Check(host.Completed == 1, "authenticated shell completion is forwarded");
    vt.Feed("\u001b]133;A\a$ \u001b]133;B\acommand\r\n\u001b]133;C\aoutput\r\n\u001b]133;D;7\a"u8);
    var marks = vt.ReadMarks();
    Check(marks.Count == 1 && marks[0].ExitCode == 7 && marks[0].OutputLine == 1 && marks[0].EndLine == 2, "OSC 133 boundaries");
    vt.Feed("\u001b]8;;https://example.org/\aLINK\u001b]8;;\a"u8); Capture(vt);
    Check(vt.Frame[2, 0].Hyperlink?.Uri == "https://example.org/", "hyperlink URI survives capture");
    using var restored = new GhosttyVt(10, 3);
    restored.RestoreSnapshot(vt.CaptureSnapshot());
    var restoredMarks = restored.ReadMarks();
    Check(restoredMarks.Count == 1 && restoredMarks[0].ExitCode == 7 && restoredMarks[0].OutputLine == 1 &&
        restoredMarks[0].EndLine == 2, "snapshot preserves tracked command boundaries");
}
using (var vt = new GhosttyVt(20, 5))
{
    vt.Feed("\u001b]8;;https://before.example/\aLINK\u001b]8;;\a"u8);
    vt.BeginCapture();
    vt.Feed("\r\u001b]8;;https://after.example/\aNEXT\u001b]8;;\a"u8);
    vt.CompleteCapture();
    Check(Row(vt, 0) == "LINK" && vt.Frame[0, 0].Hyperlink?.Uri == "https://before.example/",
        "hyperlink metadata belongs to the captured frame");
    vt.Feed("\u001b[?2026h\rheld"u8); Capture(vt);
    Check(Row(vt, 0) == "NEXT" && vt.Frame[0, 0].Hyperlink?.Uri == "https://after.example/",
        "held frame retains its hyperlink metadata");
    vt.Feed("\u001b[?2026l\u001bc"u8);
    vt.Feed("before"u8); Capture(vt);
    vt.Feed("\u001b[?2026h\rAFTER!"u8); Capture(vt);
    Check(Row(vt, 0) == "before", "synchronized output retains the preceding frame");
    vt.Feed("\u001b[?2026l"u8); Capture(vt);
    Check(Row(vt, 0) == "AFTER!", "synchronized output publishes on release");
    vt.BeginCapture(); vt.Feed("\rnext"u8); vt.CompleteCapture();
    Check(Row(vt, 0) == "AFTER!", "capture is isolated from subsequent terminal writes");
    Capture(vt); Check(Row(vt, 0) == "nextR!", "subsequent capture sees terminal writes");
    vt.Feed("\u001b[?2026h\rtimeout"u8);
    Check(vt.IsSynchronizedOutputHeld, "synchronized output reports its hold deadline");
    Thread.Sleep(1100); Capture(vt);
    Check(Row(vt, 0) == "timeout" && !vt.IsSynchronizedOutputHeld, "synchronized output has a bounded hold time");
    vt.Feed("\u001b[?2026l\u001b[?2026h\rresize"u8);
    vt.Resize(30, 6); Capture(vt);
    Check(vt.Frame.Cols == 30 && vt.Frame.Rows == 6 && Row(vt, 0).StartsWith("resize"), "resize releases an incompatible held frame");
}
using (var vt = new GhosttyVt(12, 3))
{
    vt.Feed("\u001b[?2027hé👩🏽‍💻!"u8); Capture(vt);
    Check(vt.Frame[0, 0].Grapheme == "é" && vt.Frame[0, 1].Grapheme == "👩🏽‍💻" &&
        vt.Frame[0, 1].Width == 2 && vt.Frame[0, 3].Rune == '!', "grapheme mode maps a ZWJ emoji to one wide cell");
    var selection = new ContentSelection(vt.Frame, 0, 0, 0, 3, false);
    Check(selection.Text == "é👩🏽‍💻!", "selection preserves complete clusters");
    vt.Feed("\r\u001b[2Kè👩🏽‍💻!"u8); Capture(vt);
    Check(!selection.TryLocate(vt.Frame, out _) && selection.Text == "é👩🏽‍💻!", "selection distinguishes combining marks and retains copied text");
}
using (var vt = new GhosttyVt(80, 40))
{
    string cluster = "e" + new string('\u0301', 300);
    vt.Feed(Encoding.UTF8.GetBytes(cluster)); Capture(vt);
    var row = new Cell[80]; vt.ReadRow(0, false, row);
    // The pinned upstream page stores the base scalar plus at most 64 suffixes.
    Check(row[0].Grapheme == cluster[..65] && vt.Frame[0, 0].Grapheme == cluster[..65],
        "explicit reads and frames preserve the upstream grapheme limit");
    vt.Feed(Encoding.UTF8.GetBytes("\u001bc" + string.Concat(Enumerable.Repeat("é", 80 * 39)))); Capture(vt);
    Check(vt.Frame[38, 79].Grapheme == "é", "frame arena grows and retries without losing dirty rows");
    Capture(vt); Check(vt.ChangedRows == 0, "successful arena retry cleans the frame once");
}
var disposed = new GhosttyVt(10, 3); disposed.Dispose(); disposed.Dispose();
try { disposed.Feed("x"u8); throw new Exception("Disposed write was accepted"); }
catch (ObjectDisposedException) { Check(true, "disposed handle rejected"); }
using (var source = new GhosttyVt(20, 5))
using (var restored = new GhosttyVt(10, 3))
{
    source.Feed("saved é\r\n\u001b[?9001h\u001b[31"u8);
    byte[] snapshot = source.CaptureSnapshot();
    restored.RestoreSnapshot(snapshot); restored.Feed("mRED"u8); Capture(restored);
    Check(Row(restored, 0) == "saved é" && Row(restored, 1) == "RED" && restored.Frame[1, 0].FgSpec.Index == 1,
        "native snapshot preserves graphemes and partial VT sequence");
    Check(restored.Win32InputMode, "snapshot preserves Windows input mode");
    byte[] invalidNative = (byte[])snapshot.Clone();
    // Valid outer checksum, invalid native mark count. Rejection must be atomic.
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(invalidNative.AsSpan(56 + 397 + 8, 4), 513);
    System.Security.Cryptography.SHA256.HashData(invalidNative.AsSpan(0, invalidNative.Length - 32)).CopyTo(invalidNative.AsSpan(invalidNative.Length - 32));
    try { restored.RestoreSnapshot(invalidNative); throw new Exception("Invalid native snapshot was accepted"); }
    catch (InvalidOperationException) { Check(true, "native decoder rejects malformed metadata behind a valid checksum"); }
    byte[] damaged = (byte[])snapshot.Clone(); damaged[damaged.Length / 2] ^= 1;
    try { restored.RestoreSnapshot(damaged); throw new Exception("Corrupt snapshot was accepted"); }
    catch (InvalidDataException) { Check(true, "damaged snapshot rejected"); }
    Capture(restored); Check(Row(restored, 1) == "RED", "failed restore retains live state");
    restored.Resize(40, 8); Capture(restored); Check(restored.Frame.Cols == 40, "resize after native restore");
}
using (var vt = new GhosttyVt(20, 5))
{
    foreach (byte value in "\u001b[?9001h"u8) vt.Feed(new[] { value });
    Check(vt.Win32InputMode, "chunked ConPTY Win32 input mode");
    vt.Feed("\u001b[?9001l\u001b]999;literal \u001b[?9001h\u001b\\"u8);
    Check(!vt.Win32InputMode, "mode-looking text inside OSC is ignored by Windows observer");
    vt.Feed("\u001b[?25;9001h"u8); Check(vt.Win32InputMode, "Win32 mode in a parameter list");
    vt.Feed("\u001bc"u8); Check(!vt.Win32InputMode, "RIS resets Windows input mode");
}
using (var vt = new GhosttyVt(5, 3))
{
    object gate = new();
    lock (gate)
    {
        vt.Feed("abcdefghij"u8);
        using var anchor = vt.TrackPoint(1, 2);
        vt.Resize(10, 3);
        Check(anchor.TryLocate(out int row, out int col) && row == 0 && col == 7,
            "native anchors follow the same character across reflow");
        vt.SetScrollbackLimit(4);
        vt.Feed("\r\n1\r\n2\r\n3\r\n4\r\n5\r\n6\r\n7\r\n"u8);
        long epoch = vt.ScrollEpoch; int history = vt.HistoryCount;
        // Upstream trims whole pages, so the public line limit includes page slack.
        vt.Feed(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("8\r\n", 20000))));
        Check(vt.ScrollEpoch > epoch && vt.HistoryCount - history < 20000,
            "scroll epoch advances when history evicts rows");
        Check(!anchor.TryLocate(out _, out _), "evicted native anchor is invalidated instead of pointing at unrelated text");
        using var live = vt.TrackPoint(vt.HistoryCount, 0);
        byte[] snapshot = vt.CaptureSnapshot(); vt.RestoreSnapshot(snapshot);
        Check(!live.TryLocate(out _, out _), "snapshot replacement invalidates references into the old terminal");
    }
}
using (var core = new GhosttyTerminalCore(10, 5))
{
    object gate = new();
    lock (gate)
    {
        core.Feed("zero\r\none\r\ntwo\r\nthree\r\nfour"u8);
        using var selection = core.CreateSelection(1, 0, 1, 2, false);
        core.Feed("\u001b[2;4r\u001b[4;1H\n"u8);
        Check(!selection.TryLocate(out _, out _, out _, out _),
            "deleted selection cannot silently retarget after partial-region scroll");
    }
}
using (var core = new GhosttyTerminalCore(5, 4))
{
    object gate = new();
    lock (gate)
    {
        core.Feed("abcd中Z\r\nhard"u8);
        using var selection = core.CreateSelection(0, 0, 1, 2, false);
        using var reversed = core.CreateSelection(1, 2, 0, 0, false);
        using var rectangle = core.CreateSelection(0, 0, 1, 2, true);
        Check(selection.Text == "abcd中Z" && reversed.Text == selection.Text, "forward and reverse copy omit wide-glyph wrap padding");
        Check(rectangle.Text == "abc\r\n中Z", "rectangular copy retains physical row boundaries");
        Check(TerminalSelectionText.Read(core, 1, 0, 2, 3, false) == "中Z\r\nhard", "logical copy preserves hard line boundaries");
        core.Resize(20, 4);
        Check(selection.TryLocate(out int ar, out int ac, out int fr, out int fc) && ar == 0 && ac == 0 && fr == 0 && fc == 6,
            "native selection reflows without changing copied text");
        Check(!rectangle.TryLocate(out _, out _, out _, out _), "reflow cannot silently change rectangular copied text");
    }
}
using (var core = new GhosttyTerminalCore(240, 100))
{
    core.Host = new Host { Colors = (new Color(229, 229, 229), new Color(20, 30, 40)) };
    core.Feed("default"u8);
    Check(core.DynamicBg == 0, "theme background does not masquerade as an OSC override");
    core.Feed("\u001b]11;rgb:00/00/00\a"u8);
    Check(core.DynamicBg == 0xff000000u, "explicit black background retains its presence bit");
    core.Feed("\u001b]111\a"u8);
    Check(core.DynamicBg == 0, "background reset clears the application override");
    object gate = new();
    bool independent = true;
    for (int i = 0; i < 100; i++)
    {
        lock (gate) { core.Feed("\u001b[H\u001b[2Kbefore"u8); core.BeginPaint(); }
        var writer = Task.Run(() =>
        {
            lock (gate)
            {
                core.Feed("\r\u001b[2Kafter text"u8);
                return core.DumpRow(0);
            }
        });
        var frame = core.CompletePaint();
        independent &= string.Concat(Enumerable.Range(0, frame.Cols).Select(col => frame[0, col].Text)).TrimEnd() == "before";
        independent &= core.PaintCursor.Col == 6 && writer.GetAwaiter().GetResult() == "after text";
    }
    Check(independent, "100 concurrent paints retain independent text, cursor and control reads");
}
for (int i = 0; i < 100; i++) { using var vt = new GhosttyVt(80, 24); vt.Feed("create/dispose"u8); Capture(vt); }
Check(true, "100 terminal lifetimes");
for (int i = 0; i < 100; i++)
{
    var vt = new GhosttyVt(10, 3); var anchor = vt.TrackPoint(0, 0);
    vt.Dispose(); anchor.Dispose();
}
Check(true, "anchors retain their allocator until release after owner disposal");
using (var vt = new GhosttyVt(10, 3))
{
    object gate = new();
    lock (gate)
    {
        for (int i = 0; i < 1000; i++) AbandonAnchor(vt);
        GC.Collect(); GC.WaitForPendingFinalizers();
        vt.Feed("still live"u8); Capture(vt);
        Check(Row(vt, 0) == "still live", "anchor finalization needs no managed session lock");
    }
}
using (var vt = new GhosttyVt(20, 5))
{
    var host = new Host(); vt.Host = host;
    const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==";
    vt.Feed(Encoding.ASCII.GetBytes($"\u001b_Ga=t,f=100,i=42;{png}\u001b\\"));
    Check(host.Replies.Contains("i=42;OK"), "Windows PNG decoder accepts Kitty PNG payload");
    host.Replies = "";
    vt.Feed("\u001b_Ga=t,f=100,i=43;bm90IGEgcG5n\u001b\\"u8);
    Check(host.Replies.Contains("i=43;") && !host.Replies.Contains(";OK"), "invalid PNG is rejected with protocol error");
    host.Replies = "";
    vt.Feed("\u001b_Ga=t,f=32,s=1,v=1,i=44;/wAA/w==\u001b\\"u8);
    Check(host.Replies.Contains("i=44;OK"), "raw Kitty RGBA needs no decoder");
    host.Replies = "";
    vt.Feed("\u001b_Ga=t,f=100,t=f,i=45;QzpcV2luZG93c1x3aW4uaW5p\u001b\\"u8);
    Check(host.Replies.Contains("i=45;") && !host.Replies.Contains(";OK"), "Kitty file media remains disabled");
    vt.Feed("after images"u8); Capture(vt);
    Check(Row(vt, 0) == "after images", "graphics errors leave terminal input usable");
    vt.Resize(20, 5, 8, 16);
    vt.Feed("\u001b[2;3H\u001b_Ga=p,i=42,p=1,c=2,r=2,C=1,z=-1;\u001b\\"u8); Capture(vt);
    var first = vt.FrameImages.Single();
    Check(first.Col == 2 && first.Row == 1 && first.Width == 16 && first.Height == 32 &&
        first.Layer == GhosttyImageLayer.BelowText, "native placement retains geometry and layer");
    Check(first.Image.Pixels.Span.SequenceEqual(new byte[] { 255, 0, 0, 255 }), "WIC exports exact unpremultiplied red RGBA pixels");
    Capture(vt);
    Check(ReferenceEquals(first.Image, vt.FrameImages.Single().Image), "unchanged images reuse managed pixel buffers");
    vt.BeginCapture(); vt.Feed("\u001b_Ga=d,d=I,i=42;\u001b\\"u8); vt.CompleteCapture();
    Check(vt.FrameImages.Single().Image.Pixels.Span.SequenceEqual(first.Image.Pixels.Span), "captured pixels survive concurrent native image deletion");
    Capture(vt); Check(vt.FrameImages.Count == 0, "next frame removes deleted placement");
    vt.Feed("\u001b_Ga=T,f=32,s=1,v=1,i=44,c=2,r=2,C=1;/wAA/w==\u001b\\"u8); Capture(vt);
    var red = vt.FrameImages.Single().Image;
    vt.Feed("\u001b[?2026h\u001b_Ga=T,f=32,s=1,v=1,i=44,c=2,r=2,C=1;AP8A/w==\u001b\\"u8); Capture(vt);
    Check(ReferenceEquals(red, vt.FrameImages.Single().Image), "synchronized output retains preceding image generation");
    vt.Feed("\u001b[?2026l"u8); Capture(vt);
    Check(vt.FrameImages.Single().Image.Generation != red.Generation &&
        vt.FrameImages.Single().Image.Pixels.Span.SequenceEqual(new byte[] { 0, 255, 0, 255 }), "same-ID replacement publishes new green pixels");
    byte[] imageSnapshot = vt.CaptureSnapshot();
    vt.Feed("\u001b[?1049h"u8); Capture(vt); Check(vt.FrameImages.Count == 0, "alternate screen has separate image storage");
    vt.Feed("\u001b[?1049l"u8); Capture(vt); Check(vt.FrameImages.Count == 1, "primary image survives alternate screen");
    vt.RestoreSnapshot(imageSnapshot); Capture(vt);
    Check(vt.FrameImages.Single().Image.Pixels.Span.SequenceEqual(new byte[] { 0, 255, 0, 255 }), "native snapshot restores graphics and pixels");
}
using (var vt = new GhosttyVt(20, 5))
{
    vt.Resize(20, 5, 8, 16);
    vt.Feed(Encoding.ASCII.GetBytes("\u001b_Ga=T,f=32,i=2,s=1,v=1,q=2;/wAA/w==\u001b\\" +
        "\u001b_Ga=f,f=32,i=2,s=1,v=1,z=40,q=2;AAD//w==\u001b\\" +
        "\u001b_Ga=a,i=2,r=1,z=30,q=2\u001b\\\u001b_Ga=a,i=2,s=3,q=2\u001b\\"));
    Capture(vt); var firstFrame = vt.FrameImages.Single().Image;
    Check(vt.NextAnimationDeadline > 0, "playing animation supplies a redraw deadline");
    Thread.Sleep((int)Math.Clamp(vt.NextAnimationDeadline - Environment.TickCount64 + 20, 1, 1000));
    Capture(vt);
    Check(vt.FrameImages.Single().Image.Generation != firstFrame.Generation &&
        !vt.FrameImages.Single().Image.Pixels.Span.SequenceEqual(firstFrame.Pixels.Span), "animation advances pixels and generation without terminal input");
    vt.Feed("\u001b_Ga=a,i=2,s=1,q=2\u001b\\"u8); Capture(vt);
    Check(vt.NextAnimationDeadline == 0, "stopped animation requires no periodic redraw");
}
using (var vt = new GhosttyVt(20, 5))
{
    vt.Feed("closing"u8); vt.BeginCapture();
    vt.Dispose();
    bool rejected = false;
    try { vt.Feed("late output"u8); } catch (ObjectDisposedException) { rejected = true; }
    Check(rejected, "closing terminal rejects new input while a paint is in flight");
    vt.CompleteCapture();
    Check(Row(vt, 0) == "closing", "in-flight paint completes before native terminal disposal");
    vt.Dispose();
}
using (var vt = new GhosttyVt(20, 5))
{
    string Key(uint vk, uint mods = 0, string text = "", uint action = 1) => Encoding.UTF8.GetString(vt.EncodeKey(vk, action, mods, text,
        text.Length == 0 ? 0 : (uint)Rune.GetRuneAt(text, 0).Value));
    Check(Key(0x26) == "\u001b[A", "native normal cursor key encoding");
    vt.Feed("\u001b[?1h"u8);
    Check(Key(0x26) == "\u001bOA", "native application cursor key encoding follows DEC mode");
    Check(Key(0x26, 2) == "\u001b[1;5A", "native modified cursor key encoding");
    Check(Key(0x41, 2, "a") == "\u0001", "native Ctrl+A encoding");
    Check(Key(0x41, 0, "ф") == "ф", "native input preserves keyboard layout text");
    Check(Key(0x08) == "\u007f", "native default backspace");
    vt.Feed("\u001b[?67h"u8);
    Check(Key(0x08) == "\b", "native backarrow mode");
    vt.Feed("\u001b[>3u"u8);
    Check(Key(0x41, 2, "a").Contains("97;5u"), "native Kitty disambiguation");
    Check(Key(0x41, 2, "a", 0).Contains(":3u"), "native Kitty key release");
    vt.Feed("\u001b[<u"u8);
    Check(Key(0x41, 0, "a", 0) == "", "legacy key release produces no output");
}
using (var source = new GhosttyTerminalCore(12, 4))
using (var destination = new GhosttyTerminalCore(6, 3))
{
    source.Feed("\u001b[?2027h\u001b[31;1mabcdefghijk中e\u0301\u001b[0m\r\nlegacy"u8);
    byte[] blob = BufferPersist.Serialize(source);
    Check(BufferPersist.TryParse(blob, out var history) && history.Rows.SelectMany(r => r.Cells).Any(c => c.Grapheme == "e\u0301"),
        "history protobuf preserves native graphemes");
    destination.Feed("LIVE\u001b[?1h\u001b]2;fresh shell\a\u001b[38;2;"u8);
    using var selection = destination.CreateSelection(0, 0, 0, 3, false);
    BufferPersist.Restore(destination, history);
    destination.Feed("1;2;3m!"u8);
    Check(destination.DumpRow(0) == "LIVE!" && destination.CursorCol == 5 && destination.Title == "fresh shell",
        "history prepend preserves live screen cursor and title");
    Check(destination.Screen[0, 4].Foreground == new Color(1, 2, 3), "history prepend preserves unfinished native parser input");
    Check(Encoding.ASCII.GetString(destination.EncodeKey(0x26, 1, 0)) == "\u001bOA", "history prepend preserves live input modes");
    var logical = new StringBuilder();
    for (int row = 0; row < destination.HistoryCount; row++)
    {
        logical.Append(destination.DumpHistoryRow(row));
        if (destination.GetRowWrapLength(row) < 0) logical.Append('\n');
    }
    Check(logical.ToString() == "abcdefghijk中e\u0301\nlegacy\n", "history reflow preserves logical lines and wide-glyph padding");
    Check(destination.GetHistoryCell(0, 0).FgSpec.Index == 1 && destination.GetHistoryCell(0, 0).Attributes.HasFlag(CellAttributes.Bold),
        "history prepend preserves indexed colors and attributes");
    Check(selection.TryLocate(out int anchoredRow, out _, out _, out _) && anchoredRow == destination.HistoryCount,
        "history prepend keeps live selection anchors on their original content");
    destination.SeedScrollback(["older plain"]);
    Check(destination.DumpHistoryRow(0).StartsWith("older") && destination.DumpRow(0) == "LIVE!", "plain legacy history also preserves live content");
}
using (var source = new GhosttyTerminalCore(20, 3))
using (var destination = new GhosttyTerminalCore(20, 3))
{
    source.Feed("\u001b]8;;https://example.org/history\u001b\\linked\u001b]8;;\u001b\\"u8);
    Check(BufferPersist.TryParse(BufferPersist.Serialize(source), out var history), "parse linked history");
    BufferPersist.Restore(destination, history);
    Check(destination.GetHistoryCell(0, 0).Hyperlink?.Uri == "https://example.org/history", "prepended page owns hyperlink strings after scratch terminal destruction");
    destination.Feed("\u001b[?1049hALT"u8);
    destination.SeedScrollback(["primary history"]);
    Check(destination.IsAltScreen && destination.DumpRow(0) == "ALT", "history import leaves active alternate screen unchanged");
    destination.Feed("\u001b[?1049l"u8);
    Check(destination.DumpHistoryRow(0) == "primary history", "history import targets primary screen while alternate is active");
}
Console.WriteLine($"{checks} Ghostty native contract checks passed.");

[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
static void AbandonAnchor(GhosttyVt vt) => _ = vt.TrackPoint(0, 0);

sealed class Host : IHostActions
{
    public string Replies = "";
    public int Bells;
    public string Notification = "", Clipboard = "";
    public int ProgressValue;
    public long Completed;
    public void Respond(string reply) => Replies += reply;
    public void Bell() => Bells++;
    public void Notify(string title, string body) => Notification = title + ":" + body;
    public void Progress(int state, int value) => ProgressValue = value;
    public void ClipboardWrite(string text) => Clipboard = text;
    public (Color Foreground, Color Background)? Colors;
    public (Color Foreground, Color Background)? DefaultColors => Colors;
    public void CommandFinished(string token, long sequence) => Completed = sequence;
    public void Unhandled(string kind, string detail) { }
}
