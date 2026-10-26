namespace Agwinterm.Core.Tests;

public class WorkingSetsTests
{
    private static WorkingWorkspace[] Workspaces => [new("a", "Alpha", true, [new("a1", "One", true), new("a2", "Two", false)]),
        new("b", "Beta", false, [new("b1", "One", true)]), new("c", "Empty", true, [])];
    [Fact] public void LegacyFocusMigratesAndNewDisabledSetWins()
    {
        var old = WorkingSets.LoadFocus(["a", "b"], null, null, "a"); Assert.Equal(["a"], old.Ids); Assert.True(old.Enabled);
        var modern = WorkingSets.LoadFocus(["a", "b"], ["b", "unknown", "a", "b"], false, "a");
        Assert.Equal(["a", "b"], modern.Ids); Assert.False(modern.Enabled);
    }
    [Fact] public void OrdinaryProjectionHonorsFocusCollapseAndEmptyGroups()
    {
        var rows = WorkingSets.Rows(Workspaces, false, "plain", ["b", "c"], true);
        Assert.Equal(["b", "c"], rows.Select(r => r.WorkspaceId)); Assert.All(rows, r => Assert.Null(r.SessionId));
        Assert.Equal(2, WorkingSets.Rows(Workspaces, false, "plain", [], true).Count(r => r.SessionId is not null));
    }
    [Fact] public void FlaggedLayoutsIgnoreFocusAndKeepTheirLabelContract()
    {
        var plain = WorkingSets.Rows(Workspaces, true, "plain", ["c"], true);
        Assert.Equal(["One", "One"], plain.Select(r => r.Label));
        Assert.Equal(["One : Alpha", "One : Beta"], WorkingSets.Rows(Workspaces, true, "flat", [], false).Select(r => r.Label));
        Assert.Equal(["Alpha", "One", "Beta"], WorkingSets.Rows(Workspaces, true, "tree", [], false).Select(r => r.Label));
        Assert.Equal("plain", WorkingSets.Layout("invalid"));
    }
    [Fact] public void AmbiguousSelectorsNeverPickTheFirst()
    {
        (string, string)[] sessions = [("a1", "Same"), ("a2", "Same"), ("b", "Unique")];
        Assert.Equal("a1", WorkingSets.Resolve("a1", sessions)); Assert.Null(WorkingSets.Resolve("a", sessions));
        Assert.Null(WorkingSets.Resolve("Same", sessions)); Assert.Equal("b", WorkingSets.Resolve("unique", sessions));
    }
}
