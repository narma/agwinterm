using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class ZmxCompatibilityTests
{
    static string Fixture(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "WslZmx", file));
    [Fact]
    public void BothMappingDirectionsPreserveOriginsAndEndpoints()
    {
        var tree = JsonSerializer.Deserialize<AgtermTree>(Fixture("tree.json"))!;
        var imported = ZmxCompatibility.Map(tree, "remote-origin");
        Assert.Equal(2, imported.Count);
        Assert.All(imported, pane =>
        {
            Assert.Equal("origin-window", pane.WindowId);
            Assert.Equal("origin-workspace", pane.WorkspaceId);
            Assert.Equal("origin-session", pane.SessionId);
            Assert.NotEqual(pane.LocalPaneId, pane.SessionId);
            Assert.Equal(tree.Endpoint, pane.Endpoint);
        });
        Assert.Equal(tree.Sessions[0].Panes, imported.Select(ZmxCompatibility.ExportPane));
        var exported = ZmxCompatibility.Map(tree with { Endpoint = new("/opt/wsl/zmx", "/run/user/1000/aw") }, "local-wsl-origin");
        Assert.All(exported, pane => Assert.Equal("local-wsl-origin", pane.OriginId));
        Assert.False(ZmxCompatibility.SupportsSsh);
        Assert.False(ZmxCompatibility.SupportsPresentation);
    }

    [Fact]
    public void PinnedRoleAndScreenContractsRejectStaleAttachmentNotices()
    {
        using var data = JsonDocument.Parse(Fixture("contracts.json"));
        var state = new ZmxRoleState("n1");
        foreach (var raw in data.RootElement.GetProperty("roles").EnumerateArray())
            Assert.True(state.Apply(ZmxRoleNotice.Parse(raw.GetString()!)!));
        Assert.False(state.Apply(new("stale-attachment", "leader", uint.MaxValue)));
        Assert.False(state.Apply(new("n1", "leader", 7)));
        Assert.Equal("unowned", state.Role);
        var screen = ZmxScreen.Parse(data.RootElement.GetProperty("screen").GetString()!);
        Assert.Equal(83, screen!.Columns);
        Assert.Equal(2, screen.CursorRow);
        Assert.Contains("世界", screen.Text);
        Assert.Null(ZmxScreen.Parse("42 83 30 100 0 0\ninvalid cursor"));
        Assert.Null(ZmxRoleNotice.Parse("zmx-role;n1:owner:7"));
    }
}
