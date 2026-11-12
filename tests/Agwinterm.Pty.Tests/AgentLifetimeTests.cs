using Agwinterm.Core;
using Agwinterm.Pty.Proto;

namespace Agwinterm.Pty.Tests;

public class AgentLifetimeTests
{
    [Fact]
    public void AuthenticatedShellCompletionClearsDetachedHostAndStalePromptsCannotClearNextAgent()
    {
        string directory = Path.Combine(Path.GetTempPath(), "agwinterm-lifetime-" + Guid.NewGuid().ToString("N"));
        string token = Guid.NewGuid().ToString("N");
        var effects = new List<PresentationEffect>();
        var host = new PtyHostServer.QueryReplies
        {
            Completion = new ShellCompletion(token, Path.Combine(directory, "completion")),
            EffectSink = effects.Add
        };
        try
        {
            host.ProgramStatusChanged(new("done", "", "claude", "Plan", "", null, null));
            host.ProgramStatusChanged(new("idle", "", "claude", "Plan", "", null, null));
            Assert.Equal(2, host.ProgramStatuses.Replay().Count);
            effects.Clear();
            host.CommandFinished(Guid.NewGuid().ToString("N"), 1);
            Assert.Empty(effects);
            Assert.NotEmpty(host.ProgramStatuses.Records);
            host.CommandFinished(token, 1);
            Assert.Empty(host.ProgramStatuses.Replay());
            Assert.Equal(new uint[] { 9, 5 }, effects.Select(e => e.Kind));
            host.ProgramStatusChanged(new("working", "", "codex", "Next", "", null, null));
            effects.Clear();
            host.CommandFinished(token, 1);
            Assert.Empty(effects);
            Assert.Equal("Next", Assert.Single(host.ProgramStatuses.Records).Title);
            host.CommandFinished(token, 2);
            Assert.Empty(host.ProgramStatuses.Replay());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
