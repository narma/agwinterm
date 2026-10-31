using System.Text.Json;

namespace Agwinterm.Pty.Tests;

public class WorkingSetRequestTests
{
    [Theory]
    [InlineData("session.close", "{\"targets\":[]}")]
    [InlineData("session.close", "{\"targets\":[\"a\",\"a\"]}")]
    [InlineData("session.close", "{\"targets\":[null]}")]
    [InlineData("session.move", "{\"targets\":[\"a\"]}")]
    [InlineData("session.move", "{\"before\":\"a\",\"after\":\"b\"}")]
    [InlineData("session.new", "{\"before\":\"a\",\"create-workspace\":true}")]
    [InlineData("workspace.new", "{\"collapsed\":\"true\"}")]
    [InlineData("workspace.flag", "{\"op\":\"all\"}")]
    [InlineData("workspace.focus", "{\"op\":\"on\",\"op\":\"off\"}")]
    [InlineData("sidebar.flagged-layout", "{\"layout\":\"other\"}")]
    public void InvalidRequestsRefuseBeforeMutation(string command, string json)
    { using var d = JsonDocument.Parse(json); Assert.Throws<ArgumentException>(() => WorkingSetRequest.Validate(command, d.RootElement, null)); }
    [Theory][InlineData("defaultDir")][InlineData("default-dir")]
    public void CollapsedWorkspaceAcceptsDefaultDirectoryAndRefusesMissingPath(string key)
    {
        var args = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["collapsed"] = true, [key] = Environment.CurrentDirectory });
        Assert.True(WorkingSetRequest.Applies("workspace.new", args));
        WorkingSetRequest.Validate("workspace.new", args, null);
        args = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["collapsed"] = true, [key] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()) });
        Assert.Throws<ArgumentException>(() => WorkingSetRequest.Validate("workspace.new", args, null));
    }
    [Fact] public void BatchSingleTargetConflictAndLimitAreRejected()
    {
        var args = JsonSerializer.SerializeToElement(new { targets = new[] { "a" } });
        Assert.Throws<ArgumentException>(() => WorkingSetRequest.Validate("session.close", args, "a"));
        args = JsonSerializer.SerializeToElement(new { targets = Enumerable.Range(0, 129).Select(i => i.ToString()).ToArray() });
        Assert.Throws<ArgumentException>(() => WorkingSetRequest.Validate("session.close", args, null));
    }
    [Theory]
    [InlineData("session", "move", "--targets", "a,a", "--after", "c")]
    [InlineData("session", "close", "--targets", "a", "--target", "b")]
    [InlineData("session", "new", "--before", "a", "--after", "b")]
    [InlineData("workspace", "filter", "bad")]
    [InlineData("sidebar", "flagged-layout", "bad")]
    public void InvalidCliDoesNotConnect(params string[] args)
    { Assert.Equal(2, CtlRequestTests.RunCtl([..args, "--pipe", "absent-c6-" + Guid.NewGuid()]).ExitCode); }
    [Fact] public void AdditiveRestoreFieldsRoundTripDisabledMembership()
    {
        var state = new AppState { FocusedWorkspaceIds = ["a", "b"], WorkspaceFilterEnabled = false, FlaggedLayout = "tree" };
        Assert.True(RestoreState.TryDeserialize(RestoreState.Serialize(state), out var copy));
        Assert.Equal(state.FocusedWorkspaceIds, copy!.FocusedWorkspaceIds); Assert.False(copy.WorkspaceFilterEnabled); Assert.Equal("tree", copy.FlaggedLayout);
        Assert.DoesNotContain("FocusedWorkspaceIds", RestoreState.Serialize(new()));
    }
}
