using System.Text;
using System.Text.Json;
using Agwinterm.Pty;
using Xunit;

namespace Agwinterm.Pty.Tests;

public class ControlObservationTests
{
    [Theory]
    [InlineData("{\"since\":-1}")]
    [InlineData("{\"since\":1.5}")]
    [InlineData("{\"since\":9223372036854775808}")]
    [InlineData("{\"tail\":true,\"since\":0}")]
    [InlineData("{\"limit\":1001}")]
    [InlineData("{\"limit\":1,\"limit\":2}")]
    [InlineData("{\"kinds\":[]}")]
    [InlineData("{\"kinds\":[\"status\",\"status\"]}")]
    [InlineData("{\"kinds\":[42]}")]
    [InlineData("{\"wait-ms\":25001}")]
    public void InvalidWireArgumentsRefuse(string args)
    {
        using var json = JsonDocument.Parse(args);
        Assert.Throws<ObservationException>(() => EventQuery.Parse(json.RootElement));
        var host = new FakeSessionHost();
        using var server = new ControlServer(host);
        using var reply = JsonDocument.Parse(server.Dispatch("{\"cmd\":\"events\",\"args\":" + args + "}"));
        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("invalid-argument", reply.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void Int64CursorSurvivesCliAndWireParsing()
    {
        const string cursor = "4294967296";
        Assert.True(Agwinterm.Ctl.CtlArguments.TryParse(["events", "--since", cursor],
            out _, out var opts, out _, out _, out _, out _));
        Assert.Equal(cursor, opts["since"]);
        var query = EventQuery.Parse(JsonSerializer.SerializeToElement(new { since = 4294967296L }));
        Assert.Equal(4294967296L, query.Since);
        var journal = new ControlObservation("W", initialSequence: 4294967296L);
        journal.Publish("tree");
        var page = journal.Read(query);
        Assert.Equal(4294967297L, page.Cursor);
        Assert.Equal(4294967297L, Assert.Single(page.Events).GetProperty("seq").GetInt64());
    }

    [Fact]
    public void EmptyJournalAndRunIdentity()
    {
        var first = new ControlObservation("A");
        var second = new ControlObservation("B");
        Assert.Equal(first.Run, second.Run);
        var page = first.Read(new());
        Assert.Equal(1, page.Oldest);
        Assert.Equal(0, page.Head);
        Assert.Equal(0, page.Cursor);
        Assert.False(page.Gap);
        Assert.False(page.More);
        Assert.Empty(page.Events);
        using var json = JsonDocument.Parse(page.ToJson());
        Assert.Equal(1, json.RootElement.GetProperty("schema").GetInt32());
    }

    [Fact]
    public void LimitedPagesDoNotLoseEvents()
    {
        var journal = new ControlObservation("A");
        for (int i = 0; i < 12; i++) journal.Publish("status");
        var first = journal.Read(new(5, 2));
        Assert.Equal(new long[] { 6, 7 }, first.Events.Select(e => e.GetProperty("seq").GetInt64()));
        Assert.Equal(7, first.Cursor);
        Assert.Equal(12, first.Head);
        Assert.True(first.More);
        Assert.Equal(8, journal.Read(new(first.Cursor, 1)).Cursor);
    }

    [Fact]
    public void RetentionGapIncludesZeroAndPreservesSuffix()
    {
        var journal = new ControlObservation("A");
        for (int i = 0; i < 1100; i++) journal.Publish("tree");
        var page = journal.Read(new());
        Assert.True(page.Gap);
        Assert.Equal(101, page.Oldest);
        Assert.Equal(1100, page.Cursor);
        Assert.Equal(1000, page.Events.Count);
        Assert.False(journal.Read(new(100)).Gap);
        Assert.True(journal.Read(new(99)).Gap);
        var tail = journal.Read(new(Tail: true));
        Assert.False(tail.Gap);
        Assert.Empty(tail.Events);
        Assert.Equal(1100, tail.Cursor);
    }

    [Fact]
    public void FiltersScanExcludedRecordsButStopAtPageLimit()
    {
        var journal = new ControlObservation("A");
        journal.Publish("tree"); journal.Publish("status"); journal.Publish("tree"); journal.Publish("status");
        var kinds = new HashSet<string> { "status" };
        var first = journal.Read(new(Limit: 1, Kinds: kinds));
        Assert.Equal(2, first.Cursor);
        Assert.True(first.More);
        Assert.Equal(4, journal.Read(new(first.Cursor, Kinds: kinds)).Cursor);
        var empty = journal.Read(new(Kinds: new HashSet<string> { "overlay" }));
        Assert.Equal(4, empty.Cursor);
        Assert.Empty(empty.Events);
        Assert.False(empty.More);
    }

    [Fact]
    public void OversizedUnicodeTextIsBoundedAndOwnershipSurvives()
    {
        var journal = new ControlObservation("A");
        journal.Publish("notification", new Dictionary<string, object?>
        {
            ["info"] = string.Concat(Enumerable.Repeat("😀текст", 10000)), ["paneId"] = "P", ["sessionId"] = "S"
        });
        var entry = Assert.Single(journal.Read(new()).Events);
        Assert.True(Encoding.UTF8.GetByteCount(entry.GetRawText()) <= ControlObservation.MaxRecordBytes);
        Assert.True(entry.GetProperty("truncated").GetBoolean());
        Assert.Equal("P", entry.GetProperty("paneId").GetString());
        Assert.DoesNotContain("�", entry.GetProperty("info").GetString());
        Assert.Throws<ArgumentException>(() => journal.Publish("status", new Dictionary<string, object?> { ["paneId"] = new string('x', 9000) }));
        Assert.Equal(1, journal.Read(new()).Head);
    }

    [Fact]
    public void RefusalsAreTypedAndDoNotAdvanceTheJournal()
    {
        var journal = new ControlObservation("A", "new");
        Assert.Equal("run-mismatch", Assert.Throws<ObservationException>(() => journal.Read(new(Run: "old"))).Code);
        Assert.Equal("cursor-ahead", Assert.Throws<ObservationException>(() => journal.Read(new(1))).Code);
        Assert.Throws<ObservationException>(() => journal.Read(new(-1)));
        Assert.Throws<ObservationException>(() => journal.Read(new(Limit: 1001)));
        Assert.Throws<ObservationException>(() => journal.Read(new(Kinds: new HashSet<string> { "unknown" })));
        journal.Close();
        Assert.Equal("window-gone", Assert.Throws<ObservationException>(() => journal.Read(new())).Code);
    }

    [Fact]
    public void RecreatedWindowJournalContinuesSequenceAndNewRunRefusesOldIdentity()
    {
        var before = new ControlObservation("W", "R");
        before.Publish("tree"); before.Close();
        var reopened = new ControlObservation("W", "R", before.LastSequence);
        reopened.Publish("tree");
        var page = reopened.Read(new(1, Run: "R"));
        Assert.Equal(2, page.Cursor); Assert.False(page.Gap);
        var restarted = new ControlObservation("W", "NEW");
        Assert.Equal("run-mismatch", Assert.Throws<ObservationException>(() => restarted.Read(new(2, Run: "R"))).Code);
    }
}
