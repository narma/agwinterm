using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class NavigationTests
{
    [Fact] public void DefaultBindingsCannotBeMutatedByConsumers()
    {
        var collection = Assert.IsAssignableFrom<IList<(string Chord, string Action)>>(Keymap.DefaultBindings);
        Assert.True(collection.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => collection[0] = ("f1", "delete_workspace"));
        Assert.Equal("new_session", Keymap.Parse("").Bindings["ctrl+shift+t"]);
    }

    [Theory]
    [InlineData("next", 1)][InlineData("prev", -1)][InlineData("previous", -1)]
    [InlineData(null, 0)][InlineData("", 0)][InlineData("up", 0)][InlineData("Next", 0)]
    public void DirectionIsExplicit(string? word, int step)
    { Assert.Equal(step != 0, WorkspaceNavigation.TryDirection(word, out int actual)); Assert.Equal(step, actual); }

    [Theory]
    [InlineData(0, -1, 1, -1)][InlineData(1, 0, 1, -1)]
    [InlineData(3, 0, -1, 2)][InlineData(3, 2, 1, 0)]
    [InlineData(3, 1, 1, 2)][InlineData(3, 1, -1, 0)]
    [InlineData(3, -1, 1, 0)][InlineData(3, -1, -1, 2)]
    [InlineData(3, 1, 0, -1)]
    public void NavigationWrapsVisibleOrder(int count, int current, int step, int expected)
        => Assert.Equal(expected, WorkspaceNavigation.Destination(count, current, step));

    [Fact] public void AlternativesExpandWithoutRemovingDefaults()
    {
        var result = Keymap.Parse("map ctrl+g | alt+g = next_workspace\nmap leader a | b = previous_workspace\nleader=f10");
        Assert.Empty(result.Diagnostics);
        Assert.Equal("next_workspace", result.Bindings["ctrl+g"]);
        Assert.Equal("next_workspace", result.Bindings["alt+g"]);
        Assert.Equal("previous_workspace", result.LeaderBindings["a"]);
        Assert.Equal("previous_workspace", result.LeaderBindings["b"]);
        Assert.Equal("new_session", result.Bindings["ctrl+shift+t"]);
        Assert.Equal("f10", result.Leader);
    }

    [Theory][InlineData("ctrl+g | bogus")][InlineData("| ctrl+g")][InlineData("ctrl+g |")][InlineData("ctrl+g || alt+g")]
    public void BadAlternativeRejectsWholeLine(string keys)
    {
        var result = Keymap.Parse($"map {keys} = next_workspace");
        Assert.Single(result.Diagnostics); Assert.False(result.Bindings.ContainsKey("ctrl+g"));
    }

    [Fact] public void CommandsKeepPipesAndLastMappingWins()
    {
        var result = Keymap.Parse("command Pipe = echo a | more\nmap f6 | f7 = command:Pipe\nmap f6=toggle_workspace_collapse");
        Assert.Empty(result.Diagnostics); Assert.Equal("echo a | more", Assert.Single(result.Commands).Text);
        Assert.Equal("command:Pipe", result.Bindings["f7"]);
        Assert.Equal("toggle_workspace_collapse", result.Bindings["f6"]);
    }

    [Fact] public void LeaderIsStillExactlyOneChord()
    { var result = Keymap.Parse("leader=f6|f7"); Assert.Null(result.Leader); Assert.Single(result.Diagnostics); }

    [Fact] public void UnmapDropsDefaultBinding()
    {
        var result = Keymap.Parse("unmap ctrl+d\nmap ctrl+alt+d = split_pane");
        Assert.Empty(result.Diagnostics);
        Assert.False(result.Bindings.ContainsKey("ctrl+d"));
        Assert.Equal("split_pane", result.Bindings["ctrl+alt+d"]);
    }

    [Fact] public void UnmapCtrlTabRemovesBindingFromParseResult()
    {
        var result = Keymap.Parse("unmap ctrl+tab");
        Assert.Empty(result.Diagnostics);
        Assert.False(result.Bindings.ContainsKey("ctrl+tab"));
    }

    [Fact] public void UnmapPipeClearsBothChords()
    {
        var result = Keymap.Parse("map ctrl+g | alt+g = next_workspace\nunmap ctrl+g | alt+g");
        Assert.Empty(result.Diagnostics);
        Assert.False(result.Bindings.ContainsKey("ctrl+g"));
        Assert.False(result.Bindings.ContainsKey("alt+g"));
    }

    [Fact] public void UnmapLeaderDropsLeaderBinding()
    {
        var result = Keymap.Parse("leader=f10\nmap leader g = next_workspace\nunmap leader g");
        Assert.Empty(result.Diagnostics);
        Assert.False(result.LeaderBindings.ContainsKey("g"));
    }

    [Fact] public void BadUnmapChordProducesOneDiagnostic()
    {
        var result = Keymap.Parse("unmap not+a+real+chord");
        Assert.Single(result.Diagnostics);
        Assert.Contains("bad unmap chord", result.Diagnostics[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact] public void InsertVkMapsToInsertChord()
        => Assert.Equal("insert", Keymap.ChordFor(0x2D, false, false, false));

    [Theory]
    [InlineData("pageup", 0x21, "pageup", "PgUp")]
    [InlineData("pgup", 0x21, "pageup", "PgUp")]
    [InlineData("pagedown", 0x22, "pagedown", "PgDown")]
    [InlineData("pgdown", 0x22, "pagedown", "PgDown")]
    [InlineData("PgUp", 0x21, "pageup", "PgUp")]
    [InlineData("PgDown", 0x22, "pagedown", "PgDown")]
    public void PageChordsMatchLiveKeysWithEveryModifierCombination(string token, int vk, string canonical, string display)
    {
        foreach (bool ctrl in new[] { false, true })
        foreach (bool alt in new[] { false, true })
        foreach (bool shift in new[] { false, true })
        {
            string prefix = (ctrl ? "ctrl+" : "") + (alt ? "alt+" : "") + (shift ? "shift+" : "");
            Assert.Equal(prefix + canonical, Keymap.Canonicalize(prefix + token));
            Assert.Equal(prefix + canonical, Keymap.ChordFor(vk, ctrl, alt, shift));
        }
        Assert.Equal("Ctrl+Shift+" + display, Keymap.DisplayChord("ctrl+shift+" + canonical));
    }

    [Fact] public void PageAliasesBindAndUnmapTheSameLiveSessionChords()
    {
        var result = Keymap.Parse("map ctrl+shift+pgup = previous_session\nmap ctrl+shift+pgdown = next_session");
        Assert.Empty(result.Diagnostics);
        Assert.Equal("previous_session", result.Bindings[Keymap.ChordFor(0x21, true, false, true)!]);
        Assert.Equal("next_session", result.Bindings[Keymap.ChordFor(0x22, true, false, true)!]);

        result = Keymap.Parse("map ctrl+shift+pageup = previous_session\nmap ctrl+shift+pagedown = next_session\nunmap ctrl+shift+pgup | ctrl+shift+pgdown");
        Assert.Empty(result.Diagnostics);
        Assert.False(result.Bindings.ContainsKey("ctrl+shift+pageup"));
        Assert.False(result.Bindings.ContainsKey("ctrl+shift+pagedown"));
    }

    [Theory]
    [InlineData("", "ctrl+tab", true)]
    [InlineData("", "ctrl+shift+tab", true)]
    [InlineData("unmap ctrl+tab", "ctrl+tab", false)]
    [InlineData("unmap ctrl+shift+tab", "ctrl+shift+tab", false)]
    [InlineData("map ctrl+tab = toggle_sidebar", "ctrl+tab", false)]
    public void MruTabWalkRequiresAnExplicitSessionCycleBinding(string config, string chord, bool expected)
        => Assert.Equal(expected, Keymap.UsesMruTabWalk(Keymap.Parse(config).Bindings, chord));

    [Theory]
    [InlineData("ctrl+insert", "ctrl+insert")]
    [InlineData("ctrl+ins", "ctrl+insert")]
    [InlineData("shift+insert", "shift+insert")]
    public void InsertChordsCanonicalize(string chord, string expected)
        => Assert.Equal(expected, Keymap.Canonicalize(chord));

    [Theory]
    [InlineData("CMD.EXE", false, true, "cmd")][InlineData("pwsh.exe", false, true, "pwsh")]
    [InlineData("powershell.exe", false, true, "powershell")][InlineData("bash", false, true, "bash")]
    [InlineData("cmd.exe", true, true, null)][InlineData("cmd.exe", false, false, null)]
    [InlineData("python.exe", false, true, null)][InlineData(null, false, true, null)]
    public void ShellHintsAreConservative(string? name, bool children, bool live, string? expected)
        => Assert.Equal(expected, ForegroundShellNames.Recognize(name, children, live));
}
