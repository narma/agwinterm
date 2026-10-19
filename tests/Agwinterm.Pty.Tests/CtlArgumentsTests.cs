using Agwinterm.Ctl;
using System.IO.Pipes;

namespace Agwinterm.Pty.Tests;

public class CtlArgumentsTests
{
    [Theory]
    [InlineData("ping")]
    [InlineData("version")]
    [InlineData("tree")]
    [InlineData("events --since 2 --limit 10")]
    [InlineData("install cli --remove")]
    [InlineData("omp set theme --persist")]
    [InlineData("workspace new --name work")]
    [InlineData("workspace new --collapsed --default-dir C:/work")]
    [InlineData("workspace default-dir set C:/work --target ws")]
    [InlineData("workspace default-dir clear --target ws")]
    [InlineData("workspace go --to next")]
    [InlineData("session new --command echo --no-select")]
    [InlineData("session status active --blink --sound")]
    [InlineData("session status blocked --sound notify.wav")]
    [InlineData("session type --allow-control hello")]
    [InlineData("session text --styles --all")]
    [InlineData("session text --lines 999999999999999999999999999")]
    [InlineData("session overlay text --lines 999999999999999999999999999")]
    [InlineData("session overlay open echo --wait")]
    [InlineData("session hud --spinner hello")]
    [InlineData("session split on --axis horizontal")]
    [InlineData("command run test --mode detached --target pane")]
    [InlineData("claude yolo --target pane")]
    [InlineData("app update")]
    [InlineData("theme set theme")]
    [InlineData("keymap reload")]
    [InlineData("profiles reload")]
    [InlineData("restore capture --target pane")]
    [InlineData("config set theme dark")]
    [InlineData("settings")]
    [InlineData("surface cursor")]
    [InlineData("selection all")]
    [InlineData("sidebar width 220")]
    [InlineData("quick on")]
    [InlineData("broadcast state")]
    [InlineData("notify hello --category ok")]
    [InlineData("font inc")]
    [InlineData("dashboard --auto-size")]
    [InlineData("window resize active 800 600")]
    [InlineData("image show test.png --row 0")]
    [InlineData("image frameshm Local\\agwinterm-frame-test --seq 9223372036854775807")]
    public void ValidVocabulary(string invocation)
    {
        Assert.True(CtlArguments.TryParse(invocation.Split(' '), out _, out _, out _, out _, out _, out var error), error);
    }

    [Theory]
    [InlineData("session type hello --pane right")]
    [InlineData("session close --target A --target B")]
    [InlineData("session close --TARGET A --target A")]
    [InlineData("session close A --target B")]
    [InlineData("session resize --grow-left abc")]
    [InlineData("session resize --split-ratio NaN")]
    [InlineData("session split close --axis horizontal")]
    [InlineData("session scratch on --command ignored")]
    [InlineData("install skill --typo value")]
    [InlineData("command run label --mode typo")]
    [InlineData("tree --window")]
    [InlineData("tree --socket other")]
    [InlineData("tree --window A --window A")]
    [InlineData("window resize active invalid 600")]
    [InlineData("window move active 10 20 --x 30")]
    [InlineData("command run label --command ignored")]
    [InlineData("session type --stdin literal")]
    [InlineData("session text --all --lines 3")]
    [InlineData("session hud close --detail forbidden")]
    public void InvalidInvocationNeverConnects(string invocation)
    {
        string pipe = "agwinterm-test-arguments-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var result = CtlRequestTests.RunCtl([.. invocation.Split(' '), "--pipe", pipe]);
        Assert.Equal(2, result.ExitCode);
        Assert.False(server.IsConnected);
        Assert.NotEmpty(result.StdErr);
    }

    [Fact]
    public void FlagDoesNotConsumeTextAndOptionsIgnoreCase()
    {
        Assert.True(CtlArguments.TryParse(["session", "type", "--ALLOW-CONTROL", "hello", "--JSON"],
            out var words, out var options, out var valued, out _, out bool json, out _));
        Assert.Equal(new[] { "session", "type", "hello" }, words);
        Assert.True(json);
        Assert.Equal("true", options["allow-control"]);
        Assert.DoesNotContain("allow-control", valued);
    }
}
