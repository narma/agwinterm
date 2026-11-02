using System.Text.Json.Nodes;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class AgtermRemoteDiscoveryTests
{
    static JsonObject Envelope()
    {
        var tree = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "WslZmx", "tree.json")))!;
        return new() { ["ok"] = true, ["result"] = new JsonObject { ["remote"] = tree } };
    }
    static JsonNode Remote(JsonNode node) => node["result"]!["remote"]!;
    static JsonNode Session(JsonNode node) => Remote(node)["sessions"]![0]!;
    static AgtermRemoteInventory Decode(JsonNode node) => AgtermRemoteDiscovery.Decode(0, node.ToJsonString());

    [Fact]
    public void ReadsActualEmptyPeerEnvelopeWithoutEnablingAttachment()
    {
        var result = AgtermRemoteDiscovery.Decode(0, File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "WslZmx", "remote-empty.json")));
        Assert.Empty(result.Sessions);
        Assert.Equal("/Applications/agterm.app/Contents/MacOS/zmx", result.Endpoint.Executable);
        Assert.False(ZmxCompatibility.SupportsSsh);
    }

    [Theory]
    [InlineData("horizontal")]
    [InlineData("vertical")]
    public void SplitRolesAreOrderedAndAdditiveMetadataIgnored(string axis)
    {
        var root = Envelope();
        Session(root)["splitAxis"] = axis;
        var panes = Session(root)["panes"]!.AsArray();
        var left = panes[0]!.DeepClone();
        panes.RemoveAt(0);
        panes.Add(left);
        Remote(root)["futureMetadata"] = new JsonObject { ["version"] = 12 };
        Remote(root)["host"] = "untrusted-origin-host";
        var result = Decode(root);
        var session = Assert.Single(result.Sessions);
        Assert.True(session.CanImportTerminal);
        Assert.Equal(axis, session.Session.SplitAxis);
        Assert.Equal(new[] { "left", "right" }, session.Session.Panes.Select(p => p.Role));
        Assert.Equal("origin-window", session.Session.WindowId);
    }

    [Fact]
    public void PageAndTerminalCompanionsRemainVisibleButUnsupported()
    {
        var root = Envelope();
        var page = Session(root).DeepClone();
        page["id"] = "page";
        page["content"] = "page";
        page["parent"] = "origin-session";
        page["panes"] = new JsonArray();
        page["splitAxis"] = null;
        Remote(root)["sessions"]!.AsArray().Add(page);
        var result = Decode(root);
        Assert.True(result.Sessions[0].CanImportTerminal);
        Assert.False(result.Sessions[1].CanImportTerminal);
        Assert.NotNull(result.Sessions[1].UnsupportedReason);
        Session(root)["parent"] = "another-parent";
        Assert.False(Decode(root).Sessions[0].CanImportTerminal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(255)]
    public void FailedProcessCannotSupplySuccess(int status) =>
        Assert.Throws<AgtermDiscoveryException>(() => AgtermRemoteDiscovery.Decode(status, Envelope().ToJsonString()));

    [Theory]
    [InlineData("{\"ok\":true,\"ok\":false}")]
    [InlineData("{\"ok\":false,\"error\":\"no live mode\"}")]
    [InlineData("{\"ok\":true,\"result\":{}}")]
    [InlineData("{\"ok\":true")]
    [InlineData("[]")]
    public void RefusesMalformedOrFailedEnvelope(string json) =>
        Assert.Throws<AgtermDiscoveryException>(() => AgtermRemoteDiscovery.Decode(0, json));

    [Theory]
    [InlineData("duplicate-role")]
    [InlineData("duplicate-daemon")]
    [InlineData("missing-left")]
    [InlineData("invalid-daemon")]
    [InlineData("missing-endpoint")]
    [InlineData("invalid-axis")]
    [InlineData("duplicate-session")]
    [InlineData("wrong-type")]
    [InlineData("page-with-panes")]
    [InlineData("conflicting-window")]
    [InlineData("conflicting-workspace")]
    public void RefusesAmbiguousTopology(string corruption)
    {
        var root = Envelope();
        var session = Session(root);
        switch (corruption)
        {
            case "duplicate-role": session["panes"]![1]!["pane"] = "left"; break;
            case "duplicate-daemon": session["panes"]![1]!["daemon"] = session["panes"]![0]!["daemon"]!.DeepClone(); break;
            case "missing-left": session["panes"]!.AsArray().RemoveAt(0); break;
            case "invalid-daemon": session["panes"]![0]!["daemon"] = "other"; break;
            case "missing-endpoint": Remote(root).AsObject().Remove("endpoint"); break;
            case "invalid-axis": session["splitAxis"] = "leftRight"; break;
            case "duplicate-session": Remote(root)["sessions"]!.AsArray().Add(session.DeepClone()); break;
            case "wrong-type": session["panes"] = false; break;
            case "page-with-panes": session["content"] = "page"; break;
            default:
                var second = session.DeepClone();
                second["id"] = "other";
                second["panes"] = new JsonArray();
                second["splitAxis"] = null;
                second["content"] = "page";
                second["parent"] = "origin-session";
                second[corruption == "conflicting-window" ? "windowName" : "workspaceName"] = "conflict";
                Remote(root)["sessions"]!.AsArray().Add(second);
                break;
        }
        Assert.Throws<AgtermDiscoveryException>(() => Decode(root));
    }

    [Fact]
    public void RefusesNestedDuplicatesAndOutputLimits()
    {
        var json = Envelope().ToJsonString().Replace("\"pane\":\"left\"", "\"pane\":\"left\",\"pane\":\"right\"");
        Assert.Throws<AgtermDiscoveryException>(() => AgtermRemoteDiscovery.Decode(0, json));
        Assert.Throws<AgtermDiscoveryException>(() => AgtermRemoteDiscovery.Decode(0, new string(' ', AgtermRemoteDiscovery.MaxBytes + 1)));
        Assert.Throws<AgtermDiscoveryException>(() => AgtermRemoteDiscovery.Decode(0, new string('界', AgtermRemoteDiscovery.MaxBytes / 2)));
        var root = Envelope();
        var rows = Remote(root)["sessions"]!.AsArray();
        for (int i = 0; i < AgtermRemoteDiscovery.MaxSessions; i++) rows.Add(null);
        Assert.Throws<AgtermDiscoveryException>(() => Decode(root));
    }

    [Fact]
    public void SinglePaneNeedsNoAxisAndOriginPathsCanContainShellMetacharacters()
    {
        var root = Envelope();
        Session(root)["panes"]!.AsArray().RemoveAt(1);
        Session(root)["splitAxis"] = null;
        Remote(root)["endpoint"]!["executable"] = "/Applications/a' b\\c.app/zmx";
        Assert.Single(Assert.Single(Decode(root).Sessions).Session.Panes);
        Session(root)["splitAxis"] = "vertical";
        Assert.Throws<AgtermDiscoveryException>(() => Decode(root));
    }
}
