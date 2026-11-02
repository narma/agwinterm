using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class KeyboardRestoreTests
{
    [Fact]
    public void AlternateSnapshotRetainsHiddenPrimaryCellsAndSavedCursor()
    {
        var original = new TerminalEmulator(80, 24);
        original.Feed("\x1b[31mPRIMARY\x1b[0m\x1b[5;9H\x1b[?1049hALTERNATE"u8);
        var replay = new TerminalEmulator(80, 24);
        replay.Feed(Encoding.UTF8.GetBytes(original.DumpModes() + original.DumpScreen()));
        Assert.True(replay.IsAltScreen);
        replay.Feed("\x1b[?1049l"u8);
        original.Feed("\x1b[?1049l"u8);
        Assert.Equal(original.CursorRow, replay.CursorRow);
        Assert.Equal(original.CursorCol, replay.CursorCol);
        for (int col = 0; col < 80; col++) Assert.Equal(original.Screen[0, col], replay.Screen[0, col]);
    }
    [Fact]
    public void ReattachPreservesKeyboardStackAndLaterPop()
    {
        var original = new TerminalEmulator(80, 24);
        original.Feed("\x1b[>1u\x1b[>3u"u8);
        var replay = new TerminalEmulator(80, 24);
        replay.Feed(Encoding.UTF8.GetBytes(original.DumpModes()));
        Assert.Equal(3, replay.KeyboardFlags);
        replay.Feed("\x1b[<u"u8);
        Assert.Equal(1, replay.KeyboardFlags);
        replay.Feed("\x1b[<u"u8);
        Assert.Equal(0, replay.KeyboardFlags);
    }
}
