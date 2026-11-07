using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class UnsupportedSixelTests
{
    [Fact]
    public void SixelRefusesWithoutMovingTheCursorOrReadingTheFile()
    {
        using var session = new TerminalSession(80, 24);
        using var server = new ControlServer(session);
        var reply = JsonDocument.Parse(server.Dispatch(
            "{\"cmd\":\"image.sixel\",\"args\":{\"path\":\"missing.six\",\"row\":4,\"col\":7}}"));
        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("not supported", reply.RootElement.GetProperty("error").GetString());
        Assert.Equal(0, session.Emulator.CursorRow);
        Assert.Equal(0, session.Emulator.CursorCol);
    }
}
