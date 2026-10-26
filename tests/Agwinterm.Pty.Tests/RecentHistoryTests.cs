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
    [Theory][InlineData(16384)][InlineData(16385)][InlineData(65536)]
    public void SupportedLongPinsAndNamesRoundTripWithoutQuarantiningOtherEntries(int length)
    {
        var item = Item() with { Name = new string('n', 4097), Display = new string('n', 4097) };
        item.State.Name = item.Name;
        item.State.Panes[0].RestoreCommand = new string('p', length) + "\0opaque";
        var history = new RecentHistory(); history.Sessions.AddRange([Item(), item]);
        Assert.True(history.Save(FilePath), history.Warning);
        var loaded = RecentHistory.Load(FilePath);
        Assert.Null(loaded.Warning); Assert.Equal(2, loaded.Sessions.Count);
        Assert.Equal(item.State.Panes[0].RestoreCommand, loaded.Sessions.Single(s => s.EntryId == item.EntryId).State.Panes[0].RestoreCommand);
        Assert.False(File.Exists(FilePath + ".bad"));
    }
    [Fact] public void SaveRejectsValuesThatLoadWouldRejectWithoutReplacingGoodHistory()
    {
        var history = new RecentHistory(); history.Sessions.Add(Item()); Assert.True(history.Save(FilePath));
        string previous = File.ReadAllText(FilePath);
        history.Sessions[0].State.Panes[0].Ratio = -1;
        Assert.False(history.Save(FilePath)); Assert.Equal(previous, File.ReadAllText(FilePath));
    }
    [Fact] public async Task IndependentWritersMergeAddsAndDoNotResurrectClearedEntries()
    {
        var first = new RecentHistoryWriter(FilePath); var second = new RecentHistoryWriter(FilePath);
        var a = new RecentHistory(); a.Sessions.Add(Item());
        var b = new RecentHistory(); b.Sessions.Add(Item());
        await Task.WhenAll(first.Enqueue(new(), a), second.Enqueue(new(), b));
        var stale = await second.ReadAsync(); Assert.Equal(2, stale.Sessions.Count);
        Assert.Equal(2, stale.Sessions.Select(s => s.Seq).Distinct().Count());
        await first.Enqueue(stale, new(), clearAll: true);
        var next = stale.Copy(); var added = Item(); next.Sessions.Add(added);
        await second.Enqueue(stale, next);
        var result = await first.ReadAsync(); Assert.Equal(added.EntryId, Assert.Single(result.Sessions).EntryId);
    }
    [Fact] public async Task CorruptHistoryWarningSurvivesSupersedingRefreshAndSave()
    {
        File.WriteAllText(FilePath, "{");
        var writer = new RecentHistoryWriter(FilePath);
        var initial = await writer.ReadAsync();
        var refreshed = await writer.ReadAsync();
        Assert.Contains("unavailable", initial.Warning);
        Assert.Equal(initial.Warning, refreshed.Warning);
        Assert.True(File.Exists(FilePath + ".bad"));
        var next = refreshed.Copy(); next.Sessions.Add(Item());
        await writer.Enqueue(refreshed, next);
        var saved = await writer.ReadAsync();
        Assert.Single(saved.Sessions); Assert.Equal(initial.Warning, saved.Warning);
    }
    [Fact] public async Task QueuedWritesReturnWhileFileLockIsHeldAndKeepMutationOrder()
    {
        var writer = new RecentHistoryWriter(FilePath);
        var a = new RecentHistory(); a.Sessions.Add(Item());
        Task<RecentHistory> add, clear;
        using (var held = new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            add = writer.Enqueue(new(), a);
            clear = writer.Enqueue(a, new(), clearAll: true);
            Assert.False(add.IsCompleted); Assert.False(clear.IsCompleted);
        }
        await Task.WhenAll(add, clear).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Empty((await writer.ReadAsync()).Sessions);
    }
    [Fact] public async Task FailedBackgroundMutationIsRetriedWithoutLosingItsEntry()
    {
        Directory.CreateDirectory(FilePath); // Force atomic replacement to fail.
        var writer = new RecentHistoryWriter(FilePath);
        var a = new RecentHistory(); a.Sessions.Add(Item());
        await Assert.ThrowsAsync<IOException>(() => writer.Enqueue(new(), a));
        Assert.Contains("not saved", writer.Warning);
        Directory.Delete(FilePath);
        var b = a.Copy(); b.Sessions.Add(Item());
        var result = await writer.Enqueue(a, b);
        Assert.Equal(2, result.Sessions.Count); Assert.Null(writer.Warning);
        Assert.Equal(2, RecentHistory.Load(FilePath).Sessions.Count);
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
