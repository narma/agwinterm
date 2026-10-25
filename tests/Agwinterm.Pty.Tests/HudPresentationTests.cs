using System.Text;
using System.Text.Json;
using Agwinterm.Ctl;

namespace Agwinterm.Pty.Tests;

public class HudPresentationTests
{
    [Theory][InlineData("--font-size", "NaN")][InlineData("--font-size", "73")][InlineData("--hide-after", "-1")][InlineData("--file", "missing-hud-file.md")]
    public void InvalidCliRefusesBeforeConnecting(string option, string value)
    {
        var argv = new List<string> { "session", "hud", "open" };
        if (option != "--file") argv.Add("hello");
        argv.AddRange([option, value, "--pipe", "absent-c4-" + Guid.NewGuid().ToString("N")]);
        Assert.Equal(2, CtlRequestTests.RunCtl(argv.ToArray()).ExitCode);
    }
    [Theory]
    [InlineData("\"hide-after\":-1")][InlineData("\"hide-after\":86401")][InlineData("\"font-size\":5")]
    [InlineData("\"font-size\":73")][InlineData("\"sticky\":\"true\"")][InlineData("\"frame\":null")]
    [InlineData("\"markdown\":1")][InlineData("\"message\":\"duplicate\"")]
    public void InvalidTypedOptionsRefuse(string field)
    { using var d = JsonDocument.Parse("{\"message\":\"hello\"," + field + "}"); Assert.False(SessionHuds.TryParse("open", d.RootElement, out _, out _)); }
    [Fact] public void StylesAndGeometryHonorStickyCaps()
    {
        using var d = JsonDocument.Parse("""{"message":"# title\nbody","markdown":true,"sticky":true,"frame":false,"position":"top","size-percent":100,"font-size":24,"hide-after":0.2,"pane":"right"}""");
        Assert.True(SessionHuds.TryParse("open", d.RootElement, out var h, out var error), error);
        Assert.NotNull(h!.Document); Assert.Equal(24, h.EffectiveFontSize); Assert.Equal(100, h.SizePercent);
        Assert.Equal(new SessionHuds.Box(10, 20, 200, 40), SessionHuds.Place(10, 20, 200, 100, 80, 40, h));
        Assert.Equal(1200, SessionHuds.Deadline(1000, h.HideAfter)); Assert.Null(SessionHuds.Deadline(1000, 0));
    }
    [Fact] public void AnchorResolutionNeverWidensUnknownIds()
    {
        Assert.Equal("b", SessionHuds.ResolvePane("right", ["a", "b"], 0));
        Assert.Equal("b", SessionHuds.ResolvePane("focused", ["a", "b"], 1));
        Assert.Equal("b", SessionHuds.ResolvePane("b", ["b", "a"], 1));
        Assert.Null(SessionHuds.ResolvePane("unknown", ["a", "b"], 0));
        Assert.Null(SessionHuds.ResolvePane("right", ["a"], 0));
    }
    [Fact] public void FileInputIsStrictBoundedUtf8AndAllowsBom()
    {
        string path = Path.Combine(Path.GetTempPath(), "hud-" + Guid.NewGuid() + ".md");
        try
        {
            File.WriteAllText(path, "# Привет", new UTF8Encoding(true)); Assert.Equal("# Привет", HudCliInput.ReadFile(path));
            File.WriteAllBytes(path, [0xff, 0xfe]); Assert.Throws<DecoderFallbackException>(() => HudCliInput.ReadFile(path));
            File.WriteAllBytes(path, new byte[65537]); Assert.Throws<ArgumentException>(() => HudCliInput.ReadFile(path));
        }
        finally { File.Delete(path); }
    }
}
