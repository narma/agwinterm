using Agwinterm.Pty;
using System.Text.RegularExpressions;

namespace Agwinterm.Pty.Tests;

public class AgentSkillTests
{
    [Fact]
    public void InstallingOverAnOldSkillMakesEveryLinkedGuideAvailable()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            string entry = Path.Combine(dir.FullName, "SKILL.md");
            File.WriteAllText(entry, "previous installation");
            AgentSkill.InstallTo(dir.FullName);
            string markdown = File.ReadAllText(entry);
            Assert.Equal(AgentSkill.SkillMarkdown, markdown);
            var links = Regex.Matches(markdown, @"\]\((references/[^)]+)\)").Cast<Match>().ToArray();
            Assert.NotEmpty(links);
            foreach (var link in links)
            {
                string path = link.Groups[1].Value;
                Assert.Equal(AgentSkill.References[Path.GetFileName(path)], File.ReadAllText(Path.Combine(dir.FullName, path)));
            }
            Assert.Equal(AgentSkill.References.Count, links.Select(link => link.Value).Distinct().Count());
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void ImageSectionAdvertisesTheSharedMemoryProducerContract()
    {
        Assert.Contains(
            @"agwintermctl image frameshm <Local\agwinterm-frame-NAME>",
            AgentSkill.References["images.md"]);
        Assert.Contains(
            "https://github.com/narma/agwinterm/blob/narma/docs/specs/image-frameshm.md",
            AgentSkill.References["images.md"]);
        Assert.Contains("agwintermctl session metrics [<pane-id>] --json", AgentSkill.References["images.md"]);
        Assert.Contains("Ordinary", AgentSkill.References["images.md"]);
        Assert.Contains("image files should use `image show`.", AgentSkill.References["images.md"]);
    }

    /// <summary>P3: the two persistence verbs are advertised beside their neighbours (context beside
    /// rename, capture beside clear) and the skill says the three things a caller cannot learn from
    /// the reply alone — one line / what is refused, that a capture can take seconds, and that
    /// replayOnRestore is the toggle gating the replay, not the capture.</summary>
    [Fact]
    public void PersistenceVerbsAreAdvertisedWithTheirRules()
    {
        var md = AgentSkill.References["sessions.md"] + AgentSkill.References["settings.md"];
        Assert.Contains("agwintermctl session context <text> [--target ID]", md);
        Assert.Contains("agwintermctl restore capture [--target ID]", md);
        Assert.Contains("`tree --json` reads it back as `context`", md);
        Assert.Contains("`tree --json` as `capturedCommands`, keyed by pane id like `restoreCommands`", md);
        Assert.Contains("SECONDS", md);
        Assert.Contains("`replayOnRestore` is the", md);
        // Neighbours: context is listed right after rename, capture right after clear.
        Assert.True(md.IndexOf("session rename <new-name>", StringComparison.Ordinal)
            < md.IndexOf("session context <text>", StringComparison.Ordinal));
        Assert.True(md.IndexOf("agwintermctl restore clear`", StringComparison.Ordinal)
            < md.IndexOf("agwintermctl restore capture", StringComparison.Ordinal));
    }

    /// <summary>P4: the split verbs are advertised with the four things a caller cannot learn from a
    /// reply alone — that <c>session split</c> answers a pane id (and which one when already split),
    /// the axis vocabulary in the one sentence the CLI header and <c>ISessionHost.Split</c> also
    /// carry, that <c>split close</c> takes either side and refuses a one-pane session, and that a
    /// swap moves panes and never ids — in the order split, split close, swap, focus, resize.</summary>
    [Fact]
    public void SplitVerbsAreAdvertisedWithTheirRules()
    {
        var md = AgentSkill.References["layout.md"];
        Assert.Contains("agwintermctl session split [on|off|toggle] [--axis vertical|horizontal] [--target <id>]", md);
        Assert.Contains("REPLIES WITH A PANE ID", md);
        Assert.Contains("ALSO when the session was already split", md);
        Assert.Contains(
            "The axis names the ARRANGEMENT, agterm's words: vertical = left/right panes (the default of a session never split), horizontal = top/bottom panes.",
            md);
        Assert.Contains("agwintermctl session split close [--target <id>]", md);
        Assert.Contains("EITHER side", md);
        Assert.Contains("`session close` is the verb that closes a session", md);
        Assert.Contains("agwintermctl session swap [--target <id>]", md);
        Assert.Contains("A swap moves panes, never ids", md);
        Assert.Contains("agwintermctl session focus [primary|split|left|right|top|bottom|other]", md);
        Assert.Contains("--grow-top N|--grow-bottom N", md);
        int split = md.IndexOf("agwintermctl session split [on|off|toggle]", StringComparison.Ordinal);
        int close = md.IndexOf("agwintermctl session split close", StringComparison.Ordinal);
        int swap = md.IndexOf("agwintermctl session swap", StringComparison.Ordinal);
        int focus = md.IndexOf("agwintermctl session focus [", StringComparison.Ordinal);
        int resize = md.IndexOf("agwintermctl session resize [", StringComparison.Ordinal);
        Assert.True(split < close && close < swap && swap < focus && focus < resize);
    }

    /// <summary>P5: the overlay section quotes the rule (its first and last sentence, in the plain-text
    /// spelling <c>ISessionHost.SessionOverlay</c> and the CLI header carry, so the three copies diff
    /// clean), advertises <c>--pane</c> on the verbs and the two read verbs with <c>--all</c> on both
    /// readers, names every refusal phrase from <see cref="OverlayPanes"/> (one definition, quoted
    /// here — a skill that spells a refusal its own way is the drift #248 closed), and the swap line
    /// says a PANE overlay moves while the session-wide one stays — the old "overlays / scratch /
    /// quick" clause is gone.</summary>
    [Fact]
    public void OverlayVerbsAreAdvertisedWithTheRuleAndTheirRefusals()
    {
        var md = AgentSkill.References["overlays.md"] + AgentSkill.References["io.md"] + AgentSkill.References["layout.md"];
        Assert.Contains("A session has three overlay slots: one session-wide and one per pane. A session-wide overlay", md);
        Assert.Contains("session close, the window closing).", md);
        Assert.Contains("left is pane 0 and right is pane 1", md);
        Assert.Contains("agwintermctl session overlay open \"<command>\" [--pane left|right] [--size-percent N] [--wait] [--block] [--target <id>]", md);
        Assert.Contains("agwintermctl session overlay close [--pane left|right] [--target <id>]", md);
        Assert.Contains("agwintermctl session overlay result [--pane left|right]", md);
        Assert.Contains("agwintermctl session overlay copy [--pane left|right] [--target <id>]", md);
        Assert.Contains("agwintermctl session overlay text [--all|--lines N] [--pane left|right] [--target <id>]", md);
        Assert.Contains("agwintermctl session text [--all|--lines N] [--target <id>]", md);
        Assert.Contains("\"paneOverlays\":[\"left\"]", md);
        foreach (var phrase in new[]
                 {
                     OverlayPanes.NotVisible, OverlayPanes.AlreadyOpen, OverlayPanes.NoOverlay, OverlayPanes.NotRealized,
                     OverlayPanes.NoSelection, OverlayPanes.ReadFailed, OverlayPanes.StillRunning, OverlayPanes.NoResult,
                 })
            Assert.Contains($"`{phrase}`", md);
        Assert.Contains("The clipboard is NOT touched", md);
        Assert.Contains("PANE OVERLAY (the slot is on", md);
        Assert.DoesNotContain("overlays / scratch / quick", md);
        Assert.DoesNotContain("an overlay covers the whole session", md);
    }
}
