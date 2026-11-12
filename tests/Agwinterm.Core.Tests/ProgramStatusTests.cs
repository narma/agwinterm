using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public sealed class ProgramStatusTests
{
    private sealed class Actions : IHostActions
    {
        public List<ProgramStatus> Reports = [];
        public string Replies = "";
        public int Prompts;
        public (Color Foreground, Color Background)? DefaultColors => null;
        public void ProgramStatusChanged(ProgramStatus report) => Reports.Add(report);
        public void ProgramStatusCommandFinished() => Prompts++;
        public void Respond(string reply) => Replies += reply;
        public void Notify(string title, string body) { }
        public void Progress(int state, int value) { }
        public void ClipboardWrite(string text) { }
        public void Bell() { }
        public void CommandFinished(string token, long sequence) { }
        public void Unhandled(string kind, string detail) { }
    }

    [Fact]
    public void NativeReportsSurviveEveryByteBoundaryAndQueryRepliesExactlyOnce()
    {
        using var core = new GhosttyTerminalCore(80, 24);
        var host = new Actions(); core.Host = host;
        var data = Encoding.UTF8.GetBytes("\x1b]7501;state=working:app=codex:progress=42\a" +
            "\x1b]7501;state=blocked:id=workers/test:kind=permission:msg=QXBwbHk/\x1b\\" +
            "\x1b]7501;?\a");
        foreach (byte b in data) core.Feed(new[] { b });
        Assert.Equal(2, host.Reports.Count);
        Assert.Equal(42, host.Reports[0].Progress);
        Assert.Equal("codex", host.Reports[0].App);
        Assert.Equal("workers/test", host.Reports[1].Id);
        Assert.Equal("Apply?", host.Reports[1].Message);
        Assert.Equal("permission", host.Reports[1].Kind);
        Assert.StartsWith("\x1b]7501;?", host.Replies);
        Assert.Single(host.Replies.Split("\x1b]7501;?").Skip(1));
    }

    [Fact]
    public void NativeParserRejectsMalformedReportsAndResetClearsRecords()
    {
        using var core = new GhosttyTerminalCore(80, 24);
        var host = new Actions(); core.Host = host;
        core.Feed(Encoding.UTF8.GetBytes("\x1b]7501;state=bogus\a\x1b]7501;state=working:id=a//b\a" +
            "\x1b]7501;state=blocked:msg=/w==\a\x1b]7501;state=working:msg=AA==\a"));
        Assert.Empty(host.Reports);
        core.Feed(Encoding.UTF8.GetBytes("\x1b]7501;state=done\a\x1b]133;A\a\x1b" + "c"));
        Assert.Equal("done", host.Reports[0].State);
        Assert.Equal("clear", host.Reports[^1].State);
        Assert.Equal("", host.Reports[^1].Id);
        Assert.Equal(1, host.Prompts);
    }

    private static ProgramStatus Report(string state, string id = "", string app = "") => new(state, id, app, "", "", null, null);

    [Fact]
    public void RecordsReplaceClearSubtreesAndBoundMemory()
    {
        var records = new ProgramStatusRecords();
        records.Apply(Report("working", "build", "codex"));
        records.Apply(Report("done", "build"));
        Assert.Equal("", Assert.Single(records.Records).App);
        records.Apply(Report("blocked", "build/tests"));
        records.Apply(Report("working", "builder"));
        records.Apply(Report("clear", "build"));
        Assert.Equal("builder", Assert.Single(records.Records).Id);
        for (int i = 0; i < 1000; i++) records.Apply(Report("idle", i.ToString()));
        Assert.Equal(64, records.Records.Count);
        Assert.DoesNotContain(records.Records, r => r.Id == "0");
        records.Apply(Report("clear"));
        Assert.Empty(records.Records);
    }

    [Fact]
    public void PromptEndsAllProducerRecordsAndDetachedCompletions()
    {
        var records = new ProgramStatusRecords();
        records.Apply(Report("working")); records.Apply(Report("done", "child"), retainCompletion: true);
        records.Apply(Report("error", "error")); records.Apply(Report("blocked", "wait"));
        records.Apply(Report("idle", "idle"));
        records.FinishCommand();
        Assert.Empty(records.Records);
        Assert.Empty(records.Replay());
        Assert.Equal(0, records.CompletionToken("child"));
        records.Apply(Report("idle", "next"));
        Assert.Equal("next", Assert.Single(records.Replay()).Report.Id);
    }

    [Fact]
    public void ExitedAgentLosesAttentionAndNextAgentStartsFresh()
    {
        var activity = new AgentActivity();
        activity.Observe(AgentStatus.Completed, false);
        activity.Reset();
        Assert.False(activity.Reported);
        Assert.Equal(AgentStatus.Idle, activity.Display(false));
        activity.Observe(AgentStatus.Idle, false);
        Assert.True(activity.Reported);
        Assert.Equal(AgentStatus.Idle, activity.Display(false));
    }

    [Fact]
    public void DoneIsStickyUntilViewedAndBlockedSurvivesViewing()
    {
        var activity = new AgentActivity();
        activity.Observe(AgentStatus.Active, false);
        activity.Observe(AgentStatus.Completed, false);
        activity.Observe(AgentStatus.Idle, false);
        Assert.Equal(AgentStatus.Completed, activity.Display(false));
        Assert.Equal(AgentStatus.Idle, activity.Display(true));
        Assert.Equal(AgentStatus.Idle, activity.Display(false));
        activity.Observe(AgentStatus.Blocked, false);
        Assert.Equal(AgentStatus.Blocked, activity.Display(true));
        activity.Observe(AgentStatus.Active, true);
        activity.Observe(AgentStatus.Completed, true);
        Assert.Equal(AgentStatus.Idle, activity.Display(false));
    }

    [Fact]
    public void DetachedCompletionReplaysUntilReceiptBeforeLatestIdleFields()
    {
        var records = new ProgramStatusRecords();
        records.Apply(Report("done", "child"), retainCompletion: true);
        records.Apply(Report("idle", "child", "codex"), retainCompletion: true);
        var replay = records.Replay();
        Assert.Equal(new[] { "done", "idle" }, replay.Select(r => r.Report.State));
        Assert.All(replay, r => Assert.Equal("codex", r.Report.App));
        Assert.Equal(replay, records.Replay()); // a failed attach did not consume anything
        records.AcknowledgeCompletion("child", replay[0].Completion);
        Assert.Equal("idle", Assert.Single(records.Replay()).Report.State);
        records.Apply(Report("working", "child"), retainCompletion: true);
        records.Apply(Report("idle", "child"), retainCompletion: true);
        records.AcknowledgeCompletion("child", replay[0].Completion); // stale attachment receipt
        Assert.Equal(new[] { "done", "idle" }, records.Replay().Select(r => r.Report.State));
        records.Apply(Report("done", "child"), retainCompletion: true);
        records.Apply(Report("working", "child"), retainCompletion: true);
        records.FinishCommand();
        Assert.Empty(records.Replay());
        records.Apply(Report("done", "child"), retainCompletion: true);
        records.Apply(Report("clear"), retainCompletion: true);
        Assert.Empty(records.Replay());
    }

    [Fact]
    public void WorkingToIdleProducesUnseenCompletion()
    {
        var activity = new AgentActivity();
        activity.Observe(AgentStatus.Active, false);
        activity.Observe(AgentStatus.Idle, false);
        Assert.Equal(AgentStatus.Completed, activity.Display(false));
        activity.Observe(AgentStatus.Active, false);
        Assert.Equal(AgentStatus.Active, activity.Display(false));
    }
}
