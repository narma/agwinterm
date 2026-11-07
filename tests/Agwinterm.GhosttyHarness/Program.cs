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
    string currentManifest = File.ReadAllText(manifest);
    var oldAbi = System.Text.Json.Nodes.JsonNode.Parse(currentManifest)!;
    oldAbi["abi"] = 7;
    File.WriteAllText(manifest, oldAbi.ToJsonString());
    Check(!GhosttyVt.TryLoad(mismatchDirectory, out _), "previous adapter ABI without clipboard exports is rejected before loading");
    File.WriteAllText(manifest, currentManifest);
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

Environment.SetEnvironmentVariable("AGWINTERM_GHOSTTY_COUNTERS", "1");
Check(GhosttyDiagnostics.Enabled, "native lifecycle diagnostics enabled before terminal allocation");
var initialCounts = GhosttyDiagnostics.Snapshot();
var initialNative = GhosttyDiagnostics.NativeAllocationSnapshot();
Check(initialNative is not null, "native allocation diagnostics export is available");
var initialPages=GhosttyDiagnostics.NativePageSnapshot();
Check(initialPages is not null,"native page diagnostics export is available");
using (var owner = new GhosttyTerminalCore(20, 5))
{
    Check(GhosttyDiagnostics.Snapshot().Owners == initialCounts.Owners + 1,
        "serverless core has one owner");
    Check(GhosttyDiagnostics.NativePageSnapshot()!.ResidentBytes > initialPages!.ResidentBytes,
        "page diagnostics include terminal storage outside the budget allocator");
    using (var replica = new GhosttyTerminalCore(20, 5))
    {
        replica.MarkLegacyReplica(); replica.MarkLegacyReplica();
        var hosted = GhosttyDiagnostics.Snapshot();
        Check(hosted.Owners == initialCounts.Owners + 1 && hosted.LegacyReplicas == initialCounts.LegacyReplicas + 1,
            "legacy hosted pair has one owner and one replica; classification is idempotent");
        owner.Feed("hello"u8); replica.Feed("hello"u8);
        Check(GhosttyDiagnostics.Snapshot().FeedBytes == initialCounts.FeedBytes + 10,
            "duplicate parsing is visible in feed byte counters");
        Check(GhosttyDiagnostics.Snapshot().OwnerFeedBytes == initialCounts.OwnerFeedBytes + 5 &&
            GhosttyDiagnostics.Snapshot().LegacyReplicaFeedBytes == initialCounts.LegacyReplicaFeedBytes + 5,
            "feed bytes distinguish owners from legacy replicas");
        owner.BeginPaint();
        Check(GhosttyDiagnostics.Snapshot().ActiveCaptures == initialCounts.ActiveCaptures + 1,
            "capture is counted separately from live terminals");
        owner.Dispose();
        Check(GhosttyDiagnostics.Snapshot().Owners == initialCounts.Owners + 1,
            "in-flight capture retains actual native owner after disposal request");
        owner.CompletePaint();
    }
}
var finalCounts = GhosttyDiagnostics.Snapshot();
var finalNative = GhosttyDiagnostics.NativeAllocationSnapshot()!;
Check(GhosttyDiagnostics.NativePageSnapshot() == initialPages,"native page counters return to baseline after deferred disposal");
Check(finalNative.CurrentBytes == initialNative!.CurrentBytes && finalNative.Allocations > initialNative.Allocations &&
    finalNative.AllocatedBytes > initialNative.AllocatedBytes && finalNative.Frees > initialNative.Frees,
    "native allocator records allocations and returns to baseline after disposal");
Check(finalCounts.Owners == initialCounts.Owners && finalCounts.LegacyReplicas == initialCounts.LegacyReplicas &&
    finalCounts.ActiveCaptures == initialCounts.ActiveCaptures &&
    finalCounts.TerminalsAllocated - initialCounts.TerminalsAllocated == 2 &&
    finalCounts.TerminalsReleased - initialCounts.TerminalsReleased == 2,
    "native counters return to baseline after deferred and repeated disposal");

using (var vt = new GhosttyVt(20, 5))
{
    var roles = new List<string>();
    vt.SetTitleFilter(title => { if (!title.StartsWith("zmx-role;")) return false; roles.Add(title); return true; });
    vt.Feed("\u001b]2;first\a\u001b]2;zmx-role;n:leader:1\a\u001b]2;second\a\u001b]2;zmx-role;n:follower:2\a"u8);
    Check(roles.SequenceEqual(new[] { "zmx-role;n:leader:1", "zmx-role;n:follower:2" }),
        "every live-role title survives a multi-title native feed in order");
    Check(vt.Title == "second", "consumed role titles do not replace the visible title");
    var snapshot = vt.CaptureSnapshot();
    vt.RestoreSnapshot(snapshot);
    Check(vt.Title == "second" && roles.Count == 3, "restored native title passes through the live-role filter");
    vt.Feed("\u001b]2;\a"u8);
    Check(vt.Title == "", "empty native title events still clear the visible title");
}

using (var core = new GhosttyTerminalCore(20, 6, CursorStyle.Bar, false))
{
    Check(core.CursorShape == 6, "cursor defaults available before the first paint");
    core.Feed("\u001b[3 q\u001b[2;5r\u001b[?25l"u8);
    Check(core.CursorShape == 3 && !core.CursorVisible && core.ScrollTop == 1 && core.ScrollBottom == 4,
        "live cursor and scrolling margins update without paint");
    core.Feed("\u001b]11;#000000\a"u8);
    Check(core.DynamicBg == 0xff000000, "explicit black background retains override presence");
    var snapshot = core.CaptureSnapshot();
    using var restored = new GhosttyTerminalCore(1, 1);
    restored.RestoreSnapshot(snapshot);
    Check(restored.CursorShape == 3 && restored.ScrollTop == 1 && restored.ScrollBottom == 4 && restored.DynamicBg == 0xff000000,
        "metadata survives snapshot before painting");
    core.Feed("\u001bc"u8);
    Check(core.CursorShape == 6 && core.CursorVisible && core.ScrollTop == 0 && core.ScrollBottom == 5,
        "RIS resets live cursor and margin metadata");
    core.Feed("\u001b]111\a"u8);
    Check(core.DynamicBg == 0, "OSC 111 removes background override presence");
}

using(var outputCore=new GhosttyTerminalCore(12,8))
{
    Check(!outputCore.SelectCommandOutput(0,0,true,out _,out _,out _,out _),"command output requires shell marks");
    outputCore.Feed("\u001b]133;A\a$ \u001b]133;B\afirst\r\n\u001b]133;C\aFIRST-output-wrapped\r\n\u001b]133;D;0\a\u001b]133;A\a$ \u001b]133;B\asecond\r\n\u001b]133;C\aSECOND\r\n\u001b]133;D;0\a\u001b]133;A\a$ \u001b]133;B\a"u8);
    Check(outputCore.SelectCommandOutput(0,0,true,out int ar,out int ac,out int fr,out int fc) &&
        outputCore.FormatSelection(ar,ac,fr,fc,false,0) == "SECOND","latest command output excludes prompts and command input");
    Check(outputCore.SelectCommandOutput(1,2,false,out ar,out ac,out fr,out fc) &&
        outputCore.FormatSelection(ar,ac,fr,fc,false,0) == "FIRST-output-wrapped","selected command output follows native wrapped bounds");
    Check(!outputCore.SelectCommandOutput(0,0,false,out _,out _,out _,out _),"prompt cells are not command output");
    outputCore.Feed("\u001b[?1049h"u8);
    Check(!outputCore.SelectCommandOutput(0,0,true,out _,out _,out _,out _),"command output refuses alternate screen");
}

using(var outputCore=new GhosttyTerminalCore(40,5))
{
    outputCore.Feed("\u001b]133;A\a$ \u001b]133;B\acmd \u001b]133;C\ainline output\r\n\u001b]133;D;0\a\u001b]133;A\a$ \u001b]133;B\a"u8);
    Check(outputCore.SelectCommandOutput(0,0,true,out int ar,out int ac,out int fr,out int fc) &&
        outputCore.FormatSelection(ar,ac,fr,fc,false,0) == "inline output","command output can begin after command text on the same row");
    outputCore.Feed("\u001b[?2026h"u8);
    Check(!outputCore.SelectCommandOutput(0,0,true,out _,out _,out _,out _),"command output does not select unseen synchronized content");
}

using(var retainedCore=new GhosttyTerminalCore(40,5))
{
    retainedCore.Feed("\u001b[31mretained русский\u001b[0m"u8);
    using var selected=retainedCore.CreateSelection(0,0,0,15,false);
    var formats=selected.Formats;
    Check(formats.Vt is not null && formats.Html is not null,"selection captures HTML and VT before history changes");
    retainedCore.Feed("\u001b[2J\u001b[3J\u001b[Hreplacement"u8);
    Check(!selected.TryLocate(out _,out _,out _,out _),"evicted selection loses live coordinates");
    Check(selected.Formats == formats && formats.Html!.Contains("retained") && formats.Vt!.Contains("\u001b["),"formatted selection survives erased native rows");
    Check(formats.FileContent(2)!.Contains("<meta charset=\"utf-8\">") && formats.FileContent(0) == selected.Text,
        "exports provide UTF-8 HTML document and unchanged plain text");
}

using (var selectionCore = new GhosttyTerminalCore(5, 4))
{
    selectionCore.Feed("abcdefgh"u8);
    Check(selectionCore.SelectBounds(0,1,1,"",out int ar,out int ac,out int fr,out int fc) &&
        ar == 0 && ac == 0 && fr == 1 && fc == 2, "native word selection crosses soft wraps");
    using var selected = selectionCore.CreateSelection(ar,ac,fr,fc,false);
    Check(selected.Text == "abcdefgh", "native word bounds preserve logical copy text");
    Check(selectionCore.SelectBounds(0,1,1,"e",out ar,out ac,out fr,out fc) && ar == 1 && ac == 0 && fr == 1 && fc == 2,
        "native word selection honors configured delimiters across wraps");
    Check(selectionCore.SelectBounds(1,1,0,"",out ar,out ac,out fr,out fc) && ar == 0 && fr == 1,
        "native line selection follows a wrapped logical line");
    Check(selectionCore.FormatSelection(0,0,1,2,false,0) == "abcdefgh", "native formatter unwraps selected plain text");
    string html=selectionCore.FormatSelection(0,0,1,2,false,2);
    Check(html.Contains("abcdefgh") && html.Contains('<'), "native formatter exports selected HTML");
    bool limited=false;
    try { selectionCore.FormatSelection(0,0,1,2,false,2,4); } catch(InvalidDataException) { limited=true; }
    Check(limited,"formatted selection respects its output limit");
}

using (var core = new GhosttyTerminalCore(30, 6))
{
    core.Feed("alpha beta gamma\r\n0123456789\r\nABCDEFGHIJ"u8);
    string Drag(GhosttySelectionGesture gesture,int row,int col,bool block=false)
    {
        Check(gesture.Update(row,col,block,out int ar,out int ac,out int fr,out int fc),"native gesture produces bounds");
        return TerminalSelectionText.Read(core,ar,ac,fr,fc,block);
    }
    using var word=new GhosttySelectionGesture(core,0,7,1,"");
    Check(Drag(word,0,13) == "beta gamma","double-click drag extends by whole words");
    Check(Drag(word,0,1) == "alpha beta","word drag reverses around the original press");
    Check(Drag(word,0,7) == "beta","word drag retracts to the original word");
    using var forward=new GhosttySelectionGesture(core,1,2,0,"");
    using var reverse=new GhosttySelectionGesture(core,2,5,0,"");
    Check(Drag(forward,2,5,true) == "2345\r\nCDEF","native rectangular gesture clips each physical row");
    Check(Drag(reverse,1,2,true) == "2345\r\nCDEF","reverse rectangular gesture has identical text");
    Check(Drag(reverse,2,2) == "CDEF","reverse cell drag includes both endpoints");
    using var line=new GhosttySelectionGesture(core,1,3,2,"");
    Check(Drag(line,2,2) == "0123456789\r\nABCDEFGHIJ","triple-click drag extends by whole lines");
    using var saved=core.CreateSelection(1,2,2,5,true);
    core.Feed("\u001b[2J"u8);
    Check(!saved.TryLocate(out _,out _,out _,out _) && saved.Text == "2345\r\nCDEF","redraw invalidates bounds but retains native copied text");
}

using(var core=new GhosttyTerminalCore(100,100))
{
    string text=new('x',8000);
    core.Feed(System.Text.Encoding.UTF8.GetBytes(text));
    Check(TerminalSelectionText.Read(core,0,0,79,99,false) == text,"plain copy grows its native output buffer beyond 4 KiB");
    bool limited=false;
    try { TerminalSelectionText.Read(core,0,0,79,99,false,7000); } catch(InvalidDataException) { limited=true; }
    Check(limited,"plain native copy enforces the managed character limit");
    using var gesture=new GhosttySelectionGesture(core,0,50,0,"");
    core.Resize(50,100);
    Check(gesture.Update(1,55 % 50,false,out int ar,out int ac,out int fr,out int fc) && ar == 1 && ac == 0 && fr == 1 && fc == 5,
        "gesture press origin follows reflow before the next drag");
}

using(var core=new GhosttyTerminalCore(20,4))
{
    core.Feed(System.Text.Encoding.UTF8.GetBytes("a\u2028b\r\nc"));
    Check(TerminalSelectionText.Read(core,0,0,1,0,false) == "a\u2028b\r\nc",
        "Windows newline conversion preserves printed Unicode line separators");
}

using(var core=new GhosttyTerminalCore(20,5))
{
    var host=new Host(); core.Host=host;
    ClipboardRequest? received=null;
    host.ClipboardHandler=request=> { received=request; return new(0); };
    const string begin="\u001b]5522;type=write:id=c1\u001b\\";
    const string end="\u001b]5522;type=wdata\u001b\\";
    core.Feed(System.Text.Encoding.UTF8.GetBytes(begin+"\u001b]5522;type=wdata:mime=dGV4dC9wbGFpbg==;R2hvc3Q=\u001b\\"));
    Check(received is null,"OSC 5522 does not publish partial transactions");
    core.Feed(System.Text.Encoding.UTF8.GetBytes("\u001b]5522;type=wdata:mime=dGV4dC9wbGFpbg==;dHk=\u001b\\"+
        "\u001b]5522;type=wdata:mime=dGV4dC9odG1s;PGI+aGk8L2I+\u001b\\"+end));
    Check(received?.Contents.Length == 2 && System.Text.Encoding.UTF8.GetString(received.Contents[0].Data) == "Ghostty",
        "OSC 5522 joins chunks and delivers all MIME representations atomically");
    Check(host.Replies.Contains("status=DONE:id=c1"),"OSC 5522 success follows the synchronous host result");
    foreach(var (status,name) in new[]{(1,"EPERM"),(2,"ENOSYS"),(3,"EBUSY"),(4,"EINVAL"),(5,"EIO")})
    {
        host.ClipboardHandler=_=>new(status); host.Replies="";
        core.Feed(System.Text.Encoding.UTF8.GetBytes(begin+end));
        Check(host.Replies.Contains("status="+name+":id=c1"),"OSC 5522 forwards "+name);
    }
    host.Replies=""; host.ClipboardHandler=request=>new(0,[new("text/plain","hello"u8.ToArray())],["text/plain"]);
    core.Feed("\u001b]5522;type=read:id=r1;dGV4dC9wbGFpbg==\u001b\\"u8);
    Check(host.Replies.Contains("status=DATA:id=r1:mime=dGV4dC9wbGFpbg==;aGVsbG8=") && host.Replies.Contains("status=DONE:id=r1"),
        "OSC 5522 read returns native MIME data and completion");
    host.Replies=""; host.ClipboardHandler=_=>ClipboardResult.Denied;
    core.Feed("\u001b]5522;type=read:id=r2;dGV4dC9wbGFpbg==\u001b\\"u8);
    Check(host.Replies.Contains("status=EPERM:id=r2"),"OSC 5522 denied read never returns clipboard data");
    host.Replies=""; host.ClipboardHandler=_=>throw new IOException("test");
    core.Feed(System.Text.Encoding.UTF8.GetBytes(begin+end));
    Check(host.Replies.Contains("status=EIO:id=c1"),"clipboard exceptions cannot cross the native callback boundary");
    received=null; host.Replies=""; host.ClipboardHandler=request=> { received=request; return new(0); };
    string oversized=Convert.ToBase64String(new byte[ClipboardRequest.MaximumBytes+1]);
    core.Feed(System.Text.Encoding.UTF8.GetBytes(begin+"\u001b]5522;type=wdata:mime=YXBwbGljYXRpb24vb2N0ZXQtc3RyZWFt;"+oversized+"\u001b\\"+end));
    Check(received is null && host.Replies.Contains("status=EFBIG"),"oversized OSC 5522 writes are rejected before host clipboard access");
    host.Replies=""; host.ClipboardHandler=_=>new(0,[new("text/plain",new byte[800000])]);
    core.Feed("\u001b]5522;type=read:id=large;dGV4dC9wbGFpbg==\u001b\\"u8);
    Check(host.Replies.Length > 1024*1024 && host.Replies.Contains("status=DONE:id=large"),
        "large clipboard reply survives base64 expansion in the native event queue");
}

using (var themeCore = new GhosttyTerminalCore(20, 5))
{
    var themeHost = new Host { Colors = (Color.DefaultForeground, Color.DefaultBackground) };
    themeCore.Host = themeHost;
    themeCore.Feed("\u001b[?996n"u8);
    Check(themeHost.Replies == "\u001b[?997;1n", "dark theme query uses native reply");
    themeHost.Replies = "";
    themeHost.Colors = (Color.DefaultBackground, new Color(255, 255, 255));
    themeCore.SyncTheme();
    Check(themeHost.Replies == "", "theme change is silent without subscription");
    themeCore.Feed("\u001b[?996n"u8);
    Check(themeHost.Replies == "\u001b[?997;2n", "light theme query uses updated host theme");
    themeCore.Feed("\u001b[?2031h"u8);
    themeHost.Replies = "";
    themeHost.Colors = (Color.DefaultForeground, Color.DefaultBackground);
    themeCore.SyncTheme();
    Check(themeHost.Replies == "\u001b[?997;1n", "subscribed theme change replies without further output or paint");
    themeHost.Replies = "";
    themeCore.SyncTheme();
    themeHost.Colors = (Color.DefaultForeground, new Color(10, 20, 30));
    themeCore.SyncTheme();
    Check(themeHost.Replies == "", "same scheme and repeated synchronization do not notify");
    themeCore.Feed("\u001b]11;#ffffff\a\u001b[?996n"u8);
    Check(themeHost.Replies == "\u001b[?997;1n", "OSC background override does not change host theme identity");
    themeCore.Feed("\u001b[?2031l"u8);
    themeHost.Replies = "";
    themeHost.Colors = (Color.DefaultBackground, new Color(255, 255, 255));
    themeCore.SyncTheme();
    Check(themeHost.Replies == "", "unsubscribed theme changes remain silent");
}

using (var geometry = new GhosttyVt(20,5))
{
    var host=new Host(); geometry.Host=host;
    geometry.Resize(30,10,9,19);
    geometry.Feed("\u001b[14t\u001b[16t\u001b[18t"u8);
    Check(host.Replies == "\u001b[4;190;270t\u001b[6;19;9t\u001b[8;10;30t", "native size queries report pane and cell geometry");
    geometry.Feed("\u001b[?2048h"u8); host.Replies="";
    geometry.Resize(40,12,10,20);
    Check(host.Replies == "\u001b[48;12;40;240;400t", "subscribed resize reports updated geometry");
    host.Replies=""; geometry.Feed("\u001b[>q"u8);
    Check(host.Replies.Contains("Agwinterm (libghostty-vt 1.0.0)"), "terminal version identifies the embedding application");
}

using (var core = new GhosttyTerminalCore(20, 5))
{
    var palette = Theme.DefaultPalette(); palette[1] = new Color(12, 34, 56);
    var host = new Host { Colors = (new Color(100, 110, 120), new Color(1, 2, 3)), Palette = palette, Cursor = new Color(9, 8, 7) };
    core.Host = host;
    core.Feed("\u001b[31mA\u001b[0mB"u8);
    core.BeginPaint(); var frame = core.CompletePaint();
    Check(frame[0, 0].Foreground == palette[1] && frame[0, 1].Foreground == host.Colors.Value.Foreground && core.PaintCursorColor == host.Cursor,
        "theme palette defaults and cursor reach native paint");
    core.Feed("\u001b]4;1;#aabbcc\a\u001b]10;#223344\a\u001b]12;#334455\a"u8);
    core.BeginPaint(); frame = core.CompletePaint();
    Check(frame[0, 0].Foreground == new Color(0xaa, 0xbb, 0xcc) && frame[0, 1].Foreground == new Color(0x22, 0x33, 0x44) && core.PaintCursorColor == new Color(0x33, 0x44, 0x55),
        "OSC palette foreground and cursor changes recolor existing cells");
    _ = core.Screen;
    host.Palette = Theme.DefaultPalette(); host.Palette[1] = new Color(99, 88, 77);
    host.Colors = (new Color(4, 5, 6), new Color(7, 8, 9)); host.Cursor = new Color(6, 5, 4);
    Check(core.Screen[0, 1].Background == new Color(7, 8, 9) && core.Screen[0, 0].Foreground == new Color(0xaa, 0xbb, 0xcc),
        "hidden query refreshes theme without output and preserves OSC override");
    core.Feed("\u001b]104;1\a\u001b]110\a\u001b]112\a"u8);
    core.BeginPaint(); frame = core.CompletePaint();
    Check(frame[0, 0].Foreground == host.Palette[1] && frame[0, 1].Foreground == new Color(4, 5, 6) && core.PaintCursorColor == host.Cursor,
        "OSC resets use the latest theme defaults");
    core.BeginPaint();
    core.Feed("\u001b]4;1;#112233\a\u001b]12;#223344\a"u8);
    frame = core.CompletePaint();
    Check(frame[0, 0].Foreground == host.Palette[1] && core.PaintCursorColor == host.Cursor,
        "in-flight paint retains captured palette and cursor color");
    core.BeginPaint(); frame = core.CompletePaint();
    Check(frame[0, 0].Foreground == new Color(0x11, 0x22, 0x33) && core.PaintCursorColor == new Color(0x22, 0x33, 0x44),
        "next paint observes updated palette and cursor color");
}
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
    vt.Feed("\u001b[0 q"u8);
    vt.SetCursorDefault(CursorStyle.Block,false); Capture(vt);
    Check(vt.FrameCursorShape == 2,"live default change reaches a cursor reset with DECSCUSR zero");
    vt.SetCursorDefault(CursorStyle.Bar,false); Capture(vt);
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
using (var vt = new GhosttyVt(20, 3))
{
    const CellAttributes expected = CellAttributes.Bold | CellAttributes.Italic | CellAttributes.Underline |
        CellAttributes.Inverse | CellAttributes.Dim | CellAttributes.Strikethrough;
    vt.Feed("\u001b[1;3;4;7;2;9;38;2;12;34;56;48;5;123mX\u001b[0mY"u8); Capture(vt);
    Check(vt.Frame[0, 0].Attributes == expected && vt.Frame[0, 1].Attributes == CellAttributes.None,
        "frozen style flag bits preserve every supported attribute and reset");
    Check(vt.Frame[0, 0].FgSpec == ColorSpec.FromRgb(new Color(12, 34, 56)) &&
        vt.Frame[0, 0].BgSpec == ColorSpec.Indexed(123), "frozen style color payload preserves RGB and palette tags");
    vt.Feed("\r\n\r\n\r\n"u8);
    var cells = new Cell[20]; vt.ReadRow(0, true, cells);
    Check(cells[0].Attributes == expected && cells[1].Attributes == CellAttributes.None,
        "history grid refs and frozen styles preserve combined attributes");
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
    vt.Feed("\u001b]52;c;?\a"u8); Check(!host.Replies.Contains("aGVsbG8=") && host.Replies.Contains("]52;"), "denied OSC 52 read replies without clipboard data");
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
using (var vt = new GhosttyVt(20, 8))
{
    vt.Resize(20, 8, 8, 16);
    vt.Feed("\u001b[2;3H\u001b_Ga=T,f=32,s=1,v=1,i=71,p=1,c=2,r=2,C=1;/wAA/w==\u001b\\"u8);
    vt.Feed("\u001b_Ga=T,f=32,s=1,v=1,i=72,p=2,P=71,Q=1,H=3,V=1,c=1,r=1,C=1;AP8A/w==\u001b\\"u8);
    Capture(vt);
    var child = vt.FrameImages.Single(image => image.Id == 72);
    Check(child.Col == 5 && child.Row == 2 && child.Width == 8 && child.Height == 16,
        "relative Kitty placement resolves its parent and cell offsets natively");
    byte[] snapshot = vt.CaptureSnapshot();
    vt.RestoreSnapshot(snapshot); Capture(vt);
    Check(vt.FrameImages.Any(image => image.Id == 72 && image.Col == 5 && image.Row == 2),
        "relative Kitty placement survives native snapshot restore");
    vt.Feed("\u001b_Ga=d,d=I,i=71;\u001b\\"u8); Capture(vt);
    Check(vt.FrameImages.Count == 0, "deleting a parent removes orphan relative placements");
    vt.Feed("\u001b_Ga=t,f=32,s=1,v=1,i=73;AAD//w==\u001b\\\u001b_Ga=p,i=73,U=1,c=1,r=1;\u001b\\"u8);
    vt.Feed(Encoding.UTF8.GetBytes("\u001b[4;6H\u001b[38;2;0;0;73m\U0010eeee\u0305\u0305\u001b[0m"));
    Capture(vt);
    Check(vt.FrameImages.Any(image => image.Id == 73 && image.Col == 5 && image.Row == 3),
        "Unicode Kitty placeholder exports the virtual image at the grapheme cell");
    vt.Feed("\u001b[2;2H\u001b_Ga=p,i=73,p=7,c=1,r=1,C=1;\u001b\\"u8);
    vt.Feed(Encoding.UTF8.GetBytes("\u001b[6;9H\u001b[38;2;0;0;73;58;2;0;0;7m\U0010eeee\u0305\u0305\u001b[0m"));
    Capture(vt);
    Check(vt.FrameImages.Any(image => image.Id == 73 && image.Col == 1 && image.Row == 1) &&
        !vt.FrameImages.Any(image => image.Id == 73 && image.Col == 8 && image.Row == 5),
        "an explicit placeholder cannot target a non-virtual Kitty placement");
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
    Check(Encoding.UTF8.GetString(vt.EncodeKey(0, 1, 0, "😀", 0x1f600)) == "😀", "native committed astral text encoding");
    Check(Encoding.UTF8.GetString(vt.EncodeKey(0x51, 1, 0, "@", 'q')) == "@",
        "native layout text with normalized Windows AltGr modifiers");
    Check(Key(0x08) == "\u007f", "native default backspace");
    vt.Feed("\u001b[?67h"u8);
    Check(Key(0x08) == "\b", "native backarrow mode");
    vt.Feed("\u001b[>3u"u8);
    Check(Key(0x41, 2, "a").Contains("97;5u"), "native Kitty disambiguation");
    Check(Key(0x41, 2, "a", 2).Contains(":2u"), "native Kitty key repeat");
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
using (var vt = new GhosttyVt(80, 24))
{
    string Mouse(uint action, uint button, uint mods = 0, float x = 16, float y = 36)
        => Encoding.UTF8.GetString(vt.EncodeMouse(action, button, mods, x, y, 8, 18));
    Check(Mouse(0, 1) == "", "native mouse disabled emits nothing");
    vt.Feed("\x1b[?1000h\x1b[?1006h"u8);
    Check(Mouse(0, 1) == "\x1b[<0;3;3M", "native SGR left press");
    Check(Mouse(1, 1) == "\x1b[<0;3;3m", "native SGR left release");
    Check(Mouse(0, 2, 3) == "\x1b[<22;3;3M", "native mouse right button and Shift Ctrl");
    Check(Mouse(0, 4) == "\x1b[<64;3;3M" && Mouse(0, 5) == "\x1b[<65;3;3M", "native wheel direction");
    Check(Mouse(2, 0) == "", "normal mouse tracking suppresses motion");
    vt.Feed("\x1b[?1002h"u8);
    Check(Mouse(2, 2) == "\x1b[<34;3;3M" && Mouse(2, 0) == "", "drag mode supports right button and suppresses hover");
    vt.Feed("\x1b[?1003h\x1b[?1016h"u8);
    Check(Mouse(0, 1, 0, 17, 37) == "\x1b[<0;17;37M", "native SGR pixel coordinates follow upstream");
    Check(Mouse(2, 0, 0, 19, 39) == "\x1b[<35;19;39M", "native hover pixel coordinates");
    Check(Encoding.UTF8.GetString(vt.EncodePaste("a\nb")) == "a\rb", "native unbracketed paste newline policy");
    vt.Feed("\x1b[?2004h"u8);
    Check(Encoding.UTF8.GetString(vt.EncodePaste("a\nb")) == "\x1b[200~a\nb\x1b[201~", "native bracketed paste keeps newlines");
    Check(Encoding.UTF8.GetString(vt.EncodePaste("a\x1b[201~\0b")) == "\x1b[200~a [201~ b\x1b[201~", "native paste sanitizes embedded terminator and NUL");
    Check(vt.EncodePaste("").Length == 0, "empty native paste emits nothing");
}
using (var vt = new GhosttyVt(20, 4))
{
    vt.Resize(20, 4, 8, 18);
    vt.Feed("\x1b]8;;https://example.org/history\x1b\\HISTORY\x1b]8;;\x1b\\\r\n\x1b_Ga=T,f=32,s=1,v=1,i=900,c=3,r=1,C=1;Ed9J/w==\x1b\\"u8);
    Capture(vt);
    var pixels = vt.FrameImages.Single().Image;
    vt.Feed("\r\n2\r\n3\r\n4\r\n5\r\n6"u8);
    Capture(vt); Check(vt.FrameImages.Count == 0, "scrolled image leaves active viewport");
    int history = vt.HistoryCount;
    vt.BeginCapture(history); vt.CompleteCapture();
    Check(Row(vt, 0) == "HISTORY" && vt.Frame[0, 0].Hyperlink?.Uri == "https://example.org/history",
        "history capture aligns text and hyperlink metadata");
    Check(vt.FrameImages.Count == 1 && vt.FrameImages[0].Row == 1,
        "native history capture retains image position");
    Check(vt.FrameImages[0].Image.Generation == pixels.Generation, "history image retains native generation");
    vt.BeginCapture(history);
    vt.Feed("\x1b_Ga=d,d=I,i=900\x1b\\"u8);
    vt.CompleteCapture();
    Check(vt.FrameImages.Count == 1, "in-flight history capture survives image deletion");
    vt.BeginCapture(history); vt.CompleteCapture();
    Check(vt.FrameImages.Count == 0, "next history capture observes image deletion");
    Capture(vt); Check(Row(vt, 3) == "6", "history painting leaves live parser and viewport intact");
}
using (var vt = new GhosttyVt(8, 4))
{
    vt.Feed("🔴 🔵\r\n"u8); Capture(vt);
    Check(vt.Frame[0, 0].Grapheme is null && vt.Frame[0, 0].Width == 2, "single-codepoint emoji uses the fitted solo glyph path");
    vt.Feed("\u001bc"u8);
    vt.Feed(Encoding.UTF8.GetBytes("\x1b[?2027hA👩🏽‍💻B e\u0301\r\n123456789END"));
    var emoji = vt.Search("👩🏽‍💻").Single();
    Check(emoji.Line == 0 && emoji.Col0 == 1 && emoji.Col1 == 2, $"native search reports complete emoji cell span ({emoji})");
    var combining = vt.Search("e\u0301").Single();
    Check(combining.Col0 == 5 && combining.Col1 == 5, "native search retains combining grapheme coordinates");
    var wrapped = vt.Search("789end").Single();
    Check(wrapped.Line == 1 && wrapped.Col0 == 6 && wrapped.EndLine == 2 && wrapped.Col1 == 3,
        "native search crosses soft wraps with ASCII case folding");
    vt.Feed("\x1b[?1049hALT"u8);
    Check(vt.Search("END").Count == 0 && vt.Search("ALT").Count == 1, "native search follows alternate screen");
    vt.Feed("\x1b[?1049l"u8);
    Check(vt.Search("END").Count == 1, "native search restores primary results");
    var saved = vt.CaptureSnapshot(); vt.RestoreSnapshot(saved);
    Check(vt.Search("END").Count == 1, "snapshot replacement rebuilds native search ownership");
}
using (var core = new GhosttyTerminalCore(80, 20))
{
    core.Feed("\u001b]133;A\aPROMPT>\u001b]133;B\acommand-long-text\r\n\u001b]133;C\aOUTPUT-abcdefghijklmnopqrstuvwxyz\r\n\u001b]133;D;9\a"u8);
    using var selected = core.CreateSelection(0, 0, 1, 32, false);
    string original = selected.Text;
    foreach (int width in new[] { 20, 8, 40, 5, 80 })
    {
        core.Resize(width, 20);
        Check(selected.TryLocate(out _, out _, out _, out _) && selected.Text == original,
            $"selection survives repeated width change to {width}");
        var mark = core.Marks.Single();
        var prompt = core.Search("PROMPT>").Single();
        var command = core.Search("command-long-text").Single();
        var output = core.Search("OUTPUT-").Single();
        Check(mark.PromptLine == prompt.Line && mark.CommandLine == command.Line && mark.OutputLine == output.Line && mark.ExitCode == 9,
            $"command boundaries track reflow at width {width}");
    }
}
using (var core = new GhosttyTerminalCore(12, 8))
{
    core.Feed(Encoding.UTF8.GetBytes("\x1b[?2027hПРИВЕТ мир\r\nΣ σ ς\r\n𐐀 é 👩🏽‍💻\r\nАБВГДЕЖЗИЙКЛМНОП"));
    Check(core.Search("привет").Count == 1, "Unicode Cyrillic case-insensitive search");
    Check(core.Search("σ").Count == 3, "Unicode Greek final sigma search");
    Check(core.Search("𐐨").Count == 1, "Unicode astral case-insensitive search");
    Check(core.Search("клмн").Single().Line != core.Search("клмн").Single().EndLine,
        "Unicode case-insensitive match crosses native soft wrap");
    var gate = new object();
    var result = core.SearchAsync("👩🏽‍💻", gate, CancellationToken.None).GetAwaiter().GetResult();
    Check(result is not null && result.Matches.SequenceEqual(core.Search("👩🏽‍💻")),
        "background search preserves native cluster coordinates");
    core.Feed("\x1b[?1049hALT"u8);
    result = core.SearchAsync("привет", gate, CancellationToken.None).GetAwaiter().GetResult();
    Check(result is not null && result.Matches.Count == 0, "background search isolates alternate screen");
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    bool observed = false;
    try { core.SearchAsync("ALT", gate, cancelled.Token).GetAwaiter().GetResult(); }
    catch (OperationCanceledException) { observed = true; }
    Check(observed, "background search cancellation is observable");
}
using (var core = new GhosttyTerminalCore(80, 24))
{
    core.ScrollbackMax = 30000;
    byte[] batch = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Latin русский 😀 history line\r\n", 500)));
    for (int i = 0; i < 40; i++) core.Feed(batch);
    var gate = new object();
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var search = core.SearchAsync("РУССКИЙ", gate, CancellationToken.None);
    double maxWaitMs = 0; int probes = 0;
    while (!search.IsCompleted)
    {
        var probe = System.Diagnostics.Stopwatch.StartNew();
        lock (gate) { _ = core.CursorRow; }
        maxWaitMs = Math.Max(maxWaitMs, probe.Elapsed.TotalMilliseconds); probes++;
        Thread.Sleep(1);
    }
    var result = search.GetAwaiter().GetResult();
    Check(result is not null && result.Matches.Count == 20000, "large-history background Unicode search finds all rows");
    Check(probes > 1, "large-history search releases session lock during scan");
    Console.WriteLine($"SEARCH BENCH: rows={core.HistoryCount + core.Rows}, elapsedMs={watch.Elapsed.TotalMilliseconds:F2}, lockProbeMaxMs={maxWaitMs:F2}, probes={probes}");
    using var incremental = core.CreateSearch("history");
    var changing = incremental.RunAsync(gate, CancellationToken.None);
    for (int i = 0; i < 20 && !changing.IsCompleted; i++)
    {
        Thread.Sleep(1);
        lock (gate) core.Resize(i % 2 == 0 ? 79 : 80, 24);
    }
    _ = changing.GetAwaiter().GetResult();
    var refreshed = incremental.RunAsync(gate, CancellationToken.None).GetAwaiter().GetResult();
    Check(refreshed.Matches.SequenceEqual(core.Search("history")), "native search reconciles reflow without stale coordinates");
    using var live = core.CreateSearch("РУССКИЙ");
    using var stopWriter = new CancellationTokenSource();
    using var writerStarted = new ManualResetEventSlim();
    var writer = Task.Run(() =>
    {
        writerStarted.Set();
        while (!stopWriter.IsCancellationRequested)
        {
            lock (gate) core.Feed("LIVE OUTPUT\r\n"u8);
            Thread.Sleep(1);
        }
    });
    writerStarted.Wait();
    try
    {
        var duringOutput = live.RunAsync(gate, CancellationToken.None);
        Check(duringOutput.Wait(TimeSpan.FromSeconds(10)), "native search completes while output continues");
        Check(duringOutput.Result.Matches.Count > 0, "live-output search publishes historical matches");
    }
    finally { stopWriter.Cancel(); writer.GetAwaiter().GetResult(); }
    var afterOutput = live.RunAsync(gate, CancellationToken.None).GetAwaiter().GetResult();
    Check(afterOutput.Matches.SequenceEqual(core.Search("РУССКИЙ")), "incremental results reconcile continued output exactly");
    using var cancellation = new CancellationTokenSource();
    var cancelTask = core.SearchAsync("absent", gate, cancellation.Token);
    cancellation.Cancel();
    bool observed = false;
    try { cancelTask.GetAwaiter().GetResult(); } catch (OperationCanceledException) { observed = true; }
    Check(observed, "large-history search stops after cancellation");
}
using (var closingCore = new GhosttyTerminalCore(16, 8))
{
    object ownerLock = new();
    Task<GhosttyTerminalCore.SearchResult> pending;
    lock (ownerLock)
    {
        pending = closingCore.SearchAsync("closed", ownerLock, CancellationToken.None);
        closingCore.Dispose();
    }
    bool closed = false;
    try { pending.GetAwaiter().GetResult(); }
    catch (ObjectDisposedException) { closed = true; }
    Check(closed, "pending search refuses a closed owner before accessing native state");
}
using (var core = new GhosttyTerminalCore(16, 8))
{
    core.Feed(Encoding.UTF8.GetBytes("\x1b[?2027hStraße ẞ ﬃ K İ\r\nSTRASSE\r\n"));
    Check(core.Search("strasse").Count == 2, "native Unicode search expands sharp-s");
    Check(core.Search("ß").Count == 3, "native Unicode search folds query and text symmetrically");
    var ligature = core.Search("FFI").Single();
    Check(ligature.Col0 == ligature.Col1, "folded ligature retains original cell endpoints");
    Check(core.Search("k").Count == 1 && core.Search("i\u0307").Count == 1,
        "native Unicode search handles Kelvin and dotted-I expansions");
    Check(core.Search("s").Distinct().Count() == core.Search("s").Count,
        "expanded offsets do not duplicate a highlighted cell");
    using var persistent = core.CreateSearch("strasse");
    var gate = new object();
    Check(persistent.RunAsync(gate, CancellationToken.None).GetAwaiter().GetResult().Matches.Count == 2,
        "persistent native search initializes");
    core.Feed("\x1b[?1049hALT STRASSE"u8);
    Check(persistent.RunAsync(gate, CancellationToken.None).GetAwaiter().GetResult().Matches.Count == 1,
        "persistent native search follows alternate screen");
    core.Feed("\x1b[?1049l"u8);
    Check(persistent.RunAsync(gate, CancellationToken.None).GetAwaiter().GetResult().Matches.Count == 2,
        "persistent native search recovers primary results");
    var saved = core.CaptureSnapshot(); core.RestoreSnapshot(saved);
    Check(persistent.RunAsync(gate, CancellationToken.None).GetAwaiter().GetResult().Matches.Count == 2,
        "persistent search recreates detached native ownership after restore");
    core.SeedScrollback(["older STRASSE"]);
    Check(persistent.RunAsync(gate, CancellationToken.None).GetAwaiter().GetResult().Matches.Count == 3,
        "native search discovers prepended history after completion");
    core.ScrollbackMax = 0;
    Check(persistent.RunAsync(gate, CancellationToken.None).GetAwaiter().GetResult().Matches.SequenceEqual(core.Search("strasse")),
        "native search reconciles pruned history");
}
using (var core = new GhosttyTerminalCore(80, 24))
{
    core.ScrollbackMax = 3000;
    core.Feed(Encoding.UTF8.GetBytes(new string('x', 100100)));
    using var capped = core.CreateSearch("X");
    var result = capped.RunAsync(new object(), CancellationToken.None).GetAwaiter().GetResult();
    Check(result.Truncated && result.Matches.Count == GhosttySearch.MatchLimit, $"native search caps exported results (count={result.Matches.Count}, truncated={result.Truncated}, history={core.HistoryCount})");
    core.ScrollbackMax = 0;
    result = capped.RunAsync(new object(), CancellationToken.None).GetAwaiter().GetResult();
    Check(result.Matches.SequenceEqual(core.Search("X")), "capped search prunes stale references after eviction");
    core.Dispose();
    bool rejected = false;
    try { capped.RunAsync(new object(), CancellationToken.None).GetAwaiter().GetResult(); }
    catch (ObjectDisposedException) { rejected = true; }
    Check(rejected, "search rejects a disposed core while safely retaining allocator ownership");
}
using (var core = new GhosttyTerminalCore(80, 24))
{
    core.ScrollbackMax = 3000;
    core.Feed(Encoding.UTF8.GetBytes(new string('ß', 60000)));
    using var search = core.CreateSearch("s");
    var result = search.RunAsync(new object(), CancellationToken.None).GetAwaiter().GetResult();
    Check(!result.Truncated && result.Matches.Count == 60000,
        "folded duplicate offsets do not exhaust the unique match budget");
}
using (var core = new GhosttyTerminalCore(80, 24))
{
    core.ScrollbackMax = 3000;
    core.Feed(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("ab", 50050))));
    var matches = core.Search("BA");
    Check(matches.Count == 50049, "native search crosses page boundaries without duplicate overlap results");
    Check(matches.All(m => m.Col0 % 2 == 1 && m.Col1 % 2 == 0 &&
        (m.EndLine * 80 + m.Col1) - (m.Line * 80 + m.Col0) == 1),
        "cross-page native search keeps forward cell endpoints");
}
using (var core = new GhosttyTerminalCore(20, 4))
{
    var gate = new object();
    using var source = new GhosttyFrameSource(core, gate, "capture-owner", 1);
    core.Feed("first"u8);
    using var first = source.Capture(0,8,18)!;
    core.Feed("\rsecond"u8);
    using var second = source.Capture(0,8,18)!;
    Check(first[0,0].Rune == 'f' && second[0,0].Rune == 's', "owner frame leases preserve prior content across captures");
    Check(second.Identity.TerminalGeneration > first.Identity.TerminalGeneration, "frame identity follows owner generation");
    core.Feed("\x1b[?2026h\rheld"u8);
    using var held = source.Capture(0,8,18)!;
    Check(held.Identity == second.Identity && held[0,0].Rune == 's', "synchronized output retains complete prior frame and metadata");
    core.Feed("\x1b[?2026l"u8);
    using var released = source.Capture(0,8,18)!;
    Check(released[0,0].Rune == 'h', "synchronized release publishes latest owner frame");
    core.Dispose(); source.Dispose();
    Check(first[0,0].Rune == 'f', "immutable frame outlives native owner and capture source");
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
    public Func<ClipboardRequest,ClipboardResult>? ClipboardHandler;
    public ClipboardResult ClipboardExchange(ClipboardRequest request)
    {
        if(ClipboardHandler is { } handler) return handler(request);
        if(request.Read) return ClipboardResult.Denied;
        Clipboard=System.Text.Encoding.UTF8.GetString(request.Contents.FirstOrDefault()?.Data ?? []);
        return new(0);
    }
    public (Color Foreground, Color Background)? Colors;
    public Color[]? Palette;
    public Color? Cursor;
    public IReadOnlyList<Color>? DefaultPalette => Palette;
    public Color? DefaultCursorColor => Cursor;
    public (Color Foreground, Color Background)? DefaultColors => Colors;
    public void CommandFinished(string token, long sequence) => Completed = sequence;
    public void Unhandled(string kind, string detail) { }
}
