using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace Agwinterm.Core.Tests;

/// <summary>
/// Selection follows content instead of screen coordinates, including TUI viewport redraws.
/// Feeds real VT output into a real emulator and calls the UI's actual selection methods;
/// no window, child process, clipboard, or Claude/Codex installation is needed.
/// </summary>
public class SelectionScrollRegressionTests
{
    private static readonly Assembly Ui = Assembly.LoadFrom(SelectionBoundsTests.FindUiAssembly());
    private static readonly Type ProgramType = Ui.GetType("Agwinterm.Win32.Program", true)!;
    private static readonly Type PaneType = ProgramType.GetNestedType("Pane", BindingFlags.NonPublic)!;

    private sealed class Fixture : IDisposable
    {
        private readonly IDisposable _session;
        public readonly object Pane;
        public readonly ITerminalCore Core;

        public Fixture(bool alt, bool rust = false)
        {
            var sessionType = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(Ui.Location)!,
                "Agwinterm.Pty.dll")).GetType("Agwinterm.Pty.TerminalSession", true)!;
            _session = (IDisposable)Activator.CreateInstance(sessionType, 30, 6)!;
            Core = (ITerminalCore)sessionType.GetProperty("Emulator")!.GetValue(_session)!;
            if (rust)
            {
                Core = new RustTerminalCore(30, 6);
                // Do not change the global CoreFactory while other tests create sessions.
                sessionType.GetField("<Emulator>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(_session, Core);
            }
            Pane = RuntimeHelpers.GetUninitializedObject(PaneType);
            Set("S", _session);
            if (alt) Feed("\x1b[?1049h\x1b[?1000h\x1b[?1006h");
            Paint(0);
            Select(2);
            Assert.Equal("MARKER02", Text());
        }

        public void Set(string field, object value) => PaneType.GetField(field)!.SetValue(Pane, value);
        public int Line => (int)PaneType.GetField("SelAncLine")!.GetValue(Pane)!;
        public bool Visible => (bool)PaneType.GetField("SelContentVisible")!.GetValue(Pane)!;
        public void Feed(string text) => Core.Feed(Encoding.UTF8.GetBytes(text));
        public void Paint(int first)
        {
            for (int row = 0; row < 6; row++)
                Feed($"\x1b[{row + 1};1H\x1b[2KMARKER{first + row:00} distinct row {first + row}");
        }
        public void Select(int row, int? last = null, int from = 0, int to = 7, bool block = false)
        {
            Set("SelAncLine", Core.HistoryCount + row);
            Set("SelFocLine", Core.HistoryCount + (last ?? row));
            Set("SelAncCol", from);
            Set("SelFocCol", to);
            Set("BlockSel", block);
            Set("HasSel", true);
            Invoke("StampSel");
        }
        private object? Invoke(string method) => ProgramType.GetMethod(method,
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new[] { Pane });
        public string Text() => (string)Invoke("SelectionText")!;
        public void AssertSelection(int row)
        {
            Assert.Equal("MARKER02", Text()); // empty (clear-on-wheel) is also a failure
            Assert.True(Visible);
            Assert.Equal(Core.HistoryCount + row, Line);
            Assert.Equal(Line, (int)PaneType.GetField("SelFocLine")!.GetValue(Pane)!);
        }
        public void Dispose() { _session.Dispose(); (Core as IDisposable)?.Dispose(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Regression", "TuiSelectionScroll")]
    public void AltScreen_VtScrollKeepsSelectedWordAndMovesHighlight(bool up)
    {
        using var f = new Fixture(alt: true);
        f.Feed(up ? "\x1b[S" : "\x1b[T");
        Assert.Contains("MARKER02", f.Core.DumpRow(up ? 1 : 3));
        f.AssertSelection(up ? 1 : 3);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(-1, 3)]
    [Trait("Regression", "TuiSelectionScroll")]
    public void AltScreen_RepaintedScrollKeepsSelectedWordAndMovesHighlight(int first, int expectedRow)
    {
        using var f = new Fixture(alt: true);
        // A TUI can redraw its viewport after a wheel event without emitting SU/SD at all.
        // Distinct row contents ensure a selection stuck to row 2 cannot pass accidentally.
        f.Feed("\x1b[?2026h");
        f.Paint(first);
        f.Feed("\x1b[?2026l");
        Assert.Contains("MARKER02", f.Core.DumpRow(expectedRow));
        f.AssertSelection(expectedRow);
    }

    [Fact]
    public void AltScreen_StatusRepaintKeepsSelection()
    {
        using var f = new Fixture(alt: true);
        f.Feed("\x1b[6;1H\x1b[2KWorking...");
        f.AssertSelection(2);
    }

    [Fact]
    public void MainScreen_OutputAndViewportScrollKeepSelectedText()
    {
        using var f = new Fixture(alt: false);
        f.Feed("\x1b[6;1H\r\nNEWLINE");
        Assert.Equal(1, f.Core.HistoryCount);
        f.AssertSelection(1);
        f.Set("ScrollOffset", 1);
        f.AssertSelection(1);
        Assert.Equal(2, f.Line - f.Core.HistoryCount + 1); // visible row when scrolled back
        f.Set("ScrollOffset", 0);
        f.AssertSelection(1);
    }

    [Fact]
    public void OffscreenTextStillCopiesAndHighlightsWhenItReturns()
    {
        using var f = new Fixture(alt: true);
        f.Paint(10);
        Assert.Equal("MARKER02", f.Text());
        Assert.False(f.Visible);
        f.Paint(1);
        f.AssertSelection(1);
    }

    [Fact]
    public void SynchronizedRedrawDoesNotAttachToAnIntermediateFrame()
    {
        using var f = new Fixture(alt: true);
        f.Feed("\x1b[?2026h\x1b[2J");
        Assert.Equal("MARKER02", f.Text());
        Assert.False(f.Visible);
        f.Paint(1);
        Assert.Equal("MARKER02", f.Text());
        Assert.False(f.Visible);
        f.Feed("\x1b[?2026l");
        f.AssertSelection(1);
    }

    [Fact]
    public void RepeatedWordUsesItsRowContextInsteadOfTheOldScreenPosition()
    {
        using var f = new Fixture(alt: true);
        f.Feed("\x1b[4;1HMARKER02 different row");
        f.Select(2);
        f.Feed("\x1b[S");
        f.AssertSelection(1); // row 2 also contains MARKER02, but belongs to a different line
    }

    [Fact]
    public void AmbiguousCopiesKeepTextWithoutHighlightingAnArbitraryOccurrence()
    {
        using var f = new Fixture(alt: true);
        f.Feed("\x1b[2J\x1b[2;1HMARKER02\x1b[5;1HMARKER02");
        Assert.Equal("MARKER02", f.Text());
        Assert.False(f.Visible);
    }

    [Fact]
    public void MatchingWordInAnUnrelatedFrameIsNotEnoughToMoveHighlight()
    {
        using var f = new Fixture(alt: true);
        f.Feed("\x1b[2J\x1b[5;1HMARKER02 unrelated content");
        Assert.Equal("MARKER02", f.Text());
        Assert.False(f.Visible);
    }

    [Fact]
    public void PartiallyOffscreenBlockKeepsAllTextAndVisibleRows()
    {
        using var f = new Fixture(alt: true);
        f.Select(1, last: 3, block: true);
        string selected = f.Text();
        Assert.Equal("MARKER01\r\nMARKER02\r\nMARKER03", selected);
        f.Feed("\x1b[2S");
        Assert.Equal(selected, f.Text());
        Assert.True(f.Visible);
        Assert.Equal(-1, f.Line);
    }

    [Fact]
    public void ReversedUnicodeSelectionKeepsWideCellsAndDirection()
    {
        using var f = new Fixture(alt: true);
        f.Feed("\x1b[3;1H\x1b[2KПривет界😀");
        f.Select(2, from: 9, to: 0);
        string selected = f.Text();
        Assert.Equal("Привет界😀", selected);
        f.Feed("\x1b[T");
        Assert.Equal(selected, f.Text());
        Assert.True(f.Visible);
        Assert.Equal(3, f.Line);
        Assert.Equal(9, (int)PaneType.GetField("SelAncCol")!.GetValue(f.Pane)!);
    }

    [Fact]
    public void SwitchingScreensClearsContentFromThePreviousScreen()
    {
        using var f = new Fixture(alt: true);
        f.Feed("\x1b[?1049l");
        Assert.Equal("", f.Text());
    }

    [Fact]
    public void MouseReportingOnMainScreenAlsoTracksRedraws()
    {
        using var f = new Fixture(alt: false);
        f.Feed("\x1b[?1000h");
        f.Select(2);
        f.Paint(1);
        f.AssertSelection(1);
    }

    [Fact]
    public void PartialScrollRegionMovesSelectionButNotTheStatusLine()
    {
        using var f = new Fixture(alt: false);
        f.Feed("\x1b[1;5r");
        f.Select(2);
        f.Feed("\x1b[S");
        f.AssertSelection(1);
        Assert.Contains("MARKER05", f.Core.DumpRow(5));
    }

    [Fact]
    public void RustCoreTracksScrollRedrawAndOffscreenReturn()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agwinterm.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string dll = Path.Combine(dir!.FullName, "native", "target", "release", "agwinterm_core.dll");
        // Same optional native build convention as RustEmulatorCoreTests.
        if (!File.Exists(dll)) return;
        Assert.True(RustEmulatorCore.TryLoad(dll, out var error), error);
        using var f = new Fixture(alt: true, rust: true);
        f.Feed("\x1b[S");
        f.AssertSelection(1);
        f.Paint(-1);
        f.AssertSelection(3);
        f.Paint(10);
        Assert.Equal("MARKER02", f.Text());
        Assert.False(f.Visible);
        f.Paint(0);
        f.AssertSelection(2);
    }
}
