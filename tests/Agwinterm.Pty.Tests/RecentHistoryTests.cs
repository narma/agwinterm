using System.Text.Json;

namespace Agwinterm.Pty.Tests;

public class RecentHistoryTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "agw-recent-" + Guid.NewGuid());
    private string FilePath => Path.Combine(dir, "recent.json");
    public RecentHistoryTests() => Directory.CreateDirectory(dir);
    public void Dispose() => Directory.Delete(dir, true);
    private static ClosedSession Item(long seq = 1) => new(null, "test", "C:\\", "ws", "work", true, "label", "label", "s", seq,
        "context", new() { Id = "s", Name = "label", Profile = "test", Axis = "horizontal", Active = 1,
            Panes = [new() { Id = "right", Cwd = "C:\\", FontSize = 14, Ratio = .4f, RestoreCommand = "echo pinned" },
                new() { Id = "s", Cwd = "C:\\", FontSize = 18, Ratio = .6f }] }, "window", Guid.NewGuid().ToString());

    [Fact] public void RestartPreservesEntriesAndPaneMetadata()
    {
        var item = Item(); var h = new RecentHistory(); h.Sessions.Add(item); Assert.True(h.Save(FilePath));
        var copy = RecentHistory.Load(FilePath); var read = Assert.Single(copy.Sessions);
        Assert.Equal(item.EntryId, read.EntryId); Assert.Equal("horizontal", read.State.Axis);
        Assert.Equal("right", read.State.Panes[0].Id); Assert.Equal("echo pinned", read.State.Panes[0].RestoreCommand);
        Assert.Null(copy.Warning); Assert.Equal(2, copy.NextSequence());
    }
    [Fact] public void EntryBoundEvictsOldestWholeItems()
    {
        var h = new RecentHistory(); for (int i = 0; i < 30; i++) h.Sessions.Add(Item(i));
        h.Bound(); Assert.Equal(25, h.Sessions.Count); Assert.Equal(5, h.Sessions[0].Seq); Assert.NotNull(h.Warning);
    }
    [Fact] public void MemberBoundDoesNotTruncateSurvivingWorkspace()
    {
        var h = new RecentHistory();
        for (int i = 0; i < 3; i++) h.Workspaces.Add(new("work", Enumerable.Range(0, 500).Select(n => Item(n)).ToArray(), i,
            "ws" + i, "window", Guid.NewGuid().ToString()));
        h.Bound(); Assert.Equal(2, h.Workspaces.Count); Assert.All(h.Workspaces, w => Assert.Equal(500, w.Sessions.Count));
    }
    [Fact] public void ByteBoundEvictsWholeOversizedEntry()
    {
        var h = new RecentHistory(); var item = Item(); item.State.Panes[0].RestoreCommand = new string('a', RecentHistory.MaxBytes);
        h.Sessions.Add(item); h.Bound(); Assert.Empty(h.Sessions); Assert.NotNull(h.Warning);
    }
    [Theory][InlineData("{")][InlineData("null")][InlineData("{\"Sessions\":null}")]
    public void CorruptFileIsRetainedWithoutLaunch(string json)
    {
        File.WriteAllText(FilePath, json); var h = RecentHistory.Load(FilePath);
        Assert.Empty(h.Sessions); Assert.NotNull(h.Warning); Assert.True(File.Exists(FilePath + ".bad"));
    }
    [Fact] public void InvalidPaneAndDuplicateEntryAreRejected()
    {
        var h = new RecentHistory(); var item = Item(); item.State.Panes[0].Ratio = -1; h.Sessions.Add(item);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(h)); Assert.NotNull(RecentHistory.Load(FilePath).Warning);
        item.State.Panes[0].Ratio = 1; h.Sessions.Add(item);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(h)); Assert.NotNull(RecentHistory.Load(FilePath).Warning);
    }
    [Fact] public void WriteFailureRetainsInMemoryEntries()
    {
        var h = new RecentHistory(); h.Sessions.Add(Item());
        Assert.False(h.Save(dir)); Assert.Single(h.Sessions); Assert.Contains("not saved", h.Warning);
    }
    [Fact] public void ClearPersistsWithoutTouchingSeparateRestoreFile()
    {
        string live = Path.Combine(dir, "state.json"); File.WriteAllText(live, "untouched");
        var h = new RecentHistory(); h.Sessions.Add(Item()); Assert.True(h.Save(FilePath));
        h.Sessions.Clear(); Assert.True(h.Save(FilePath)); Assert.Empty(RecentHistory.Load(FilePath).Sessions);
        Assert.Equal("untouched", File.ReadAllText(live));
    }
    [Theory][InlineData("recent", "open")][InlineData("recent", "open", "bad")]
    [InlineData("recent", "list", "extra")][InlineData("recent", "clear", "--window", "active")]
    [InlineData("recent", "open", "--target", "active")]
    public void CliMisuseFailsBeforeTransport(params string[] args)
    { Assert.Equal(2, CtlRequestTests.RunCtl([..args, "--pipe", "missing-c5-" + Guid.NewGuid()]).ExitCode); }
}
