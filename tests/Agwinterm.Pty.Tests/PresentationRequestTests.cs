using System.Text.Json;
using Agwinterm.Core;

namespace Agwinterm.Pty.Tests;

public class PresentationRequestTests
{
    [Theory]
    [InlineData("session.appearance","{\"op\":false}")]
    [InlineData("session.appearance","{\"op\":\"set\",\"opacity\":\"x\"}")]
    [InlineData("session.appearance","{\"op\":\"set\",\"opacity\":101}")]
    [InlineData("session.appearance","{\"op\":\"set\",\"color\":\"#ffffff\",\"color\":\"#000000\"}")]
    [InlineData("session.appearance","{\"op\":\"clear\",\"text\":\"x\"}")]
    [InlineData("keymap.list","{\"extra\":true}")]
    [InlineData("config.reload","[]")]
    public void InvalidInputRefuses(string command,string json)
    { using var doc=JsonDocument.Parse(json);Assert.Throws<ArgumentException>(()=>PresentationRequest.Validate(command,doc.RootElement,null)); }
    [Theory]
    [InlineData("session","appearance","set","--color","red")]
    [InlineData("session","appearance","set","--opacity","101")]
    [InlineData("session","appearance","clear","--text","x")]
    [InlineData("keymap","list","extra")]
    public void InvalidCliDoesNotConnect(params string[] args) => Assert.Equal(2,CtlRequestTests.RunCtl([..args,"--pipe","missing-c8-"+Guid.NewGuid()]).ExitCode);
    [Theory]
    [InlineData("1.2.3","1.2.3+commit","match")]
    [InlineData("1.2.3","1.2.4","different")]
    [InlineData("1.2.3",null,"unknown")]
    [InlineData("dev","dev","unknown")]
    public void VersionComparisonNeverInfersMissingMatch(string running,string? disk,string expected) => Assert.Equal(expected,ExecutableVersion.Compare(running,disk));
    [Fact] public void AppearanceRoundTripsAsAnOptionalPaneField()
    {
        var pane=new PaneState { Id="pane",Appearance=new(Color:"#123456",Text:"hello",StatusShape:"diamond") };
        var copy=JsonSerializer.Deserialize<PaneState>(JsonSerializer.Serialize(pane));Assert.Equal(pane.Appearance,copy!.Appearance);
        Assert.DoesNotContain("Appearance",JsonSerializer.Serialize(new PaneState()));
        var missing=JsonSerializer.Serialize(ExecutableVersion.Read("1.0","missing-c8-"+Guid.NewGuid(),"Portable"));Assert.Contains("missing-file",missing);Assert.Contains("unknown",missing);
    }
}
