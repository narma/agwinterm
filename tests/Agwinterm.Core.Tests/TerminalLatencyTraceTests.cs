using System.Text;
using Agwinterm.Core;
using Xunit;

namespace Agwinterm.Core.Tests;

public sealed class TerminalLatencyTraceTests
{
    [Fact]
    public void SingleKeyEchoCountsOnlyEnterDownWithoutPrimingText()
    {
        var input=new TerminalLatencyTrace.MarkerReader(countEnters:true);
        Assert.Equal(0,input.Input("text"u8));
        Assert.Equal(1,input.Input("\r"u8));
        Assert.Equal(0,input.Input("\u001b[13;28;13;0;0;1_"u8));
        Assert.Equal(2,input.Input("\u001b[13;28;13;1;0;1_"u8));
        Assert.Equal(3,input.Input("\u001b[13u"u8));
        var child=new TerminalLatencyTrace.MarkerReader(countEnters:true);
        Assert.Equal(1,child.Push(13,true));
        Assert.Equal(2,child.Push(13,true));
    }
    [Fact]
    public void PresentationRequiresCompleteMarkerInCapturedCells()
    {
        var row = "~AGL0000002A~".Select(c => Cell.Empty with { Rune = c }).ToArray();
        Assert.Equal(42, TerminalLatencyTrace.FrameMarker(row));
        Assert.Equal(0, TerminalLatencyTrace.FrameMarker(row.AsSpan(0,12)));
        row[5] = row[5] with { Grapheme = "0\u0301" };
        Assert.Equal(0, TerminalLatencyTrace.FrameMarker(row));
    }
    [Fact]
    public void MarkerSurvivesEveryReadBoundary()
    {
        byte[] marker = "noise~AGL0000002A~tail"u8.ToArray();
        for (int split = 0; split <= marker.Length; split++)
        {
            var reader = new TerminalLatencyTrace.MarkerReader();
            var found = new List<long>();
            foreach (byte b in marker.AsSpan(0, split)) { long id = reader.Push(b); if (id > 0) found.Add(id); }
            foreach (byte b in marker.AsSpan(split)) { long id = reader.Push(b); if (id > 0) found.Add(id); }
            Assert.Equal(new long[] { 42 }, found);
        }
    }

    [Fact]
    public void InputReceiptRequiresEnterAndDoesNotReplay()
    {
        var reader = new TerminalLatencyTrace.MarkerReader();
        Assert.Equal(0, reader.Input("~AGL00000001~"u8));
        Assert.Equal(1, reader.Input("\r"u8));
        Assert.Equal(0, reader.Input("\r"u8));
        Assert.Equal(2, reader.Input("~AGL00000002~\r"u8));
    }

    [Theory]
    [InlineData("~AGL00000001~\u001b[13;28;13;1;0;1_")]
    [InlineData("~AGL00000001~\u001b[13u")]
    public void RecognizesEncodedEnter(string input)
    {
        var reader = new TerminalLatencyTrace.MarkerReader();
        Assert.Equal(1, reader.Input(Encoding.ASCII.GetBytes(input)));
    }

    [Theory]
    [InlineData("~AGL00000000~")]
    [InlineData("~AGLFFFFFFFF~")]
    [InlineData("~AGL00000x01~")]
    [InlineData("~AGL00000001!")]
    public void RejectsMalformedAndUnboundedIds(string marker)
    {
        var reader = new TerminalLatencyTrace.MarkerReader();
        Assert.All(Encoding.ASCII.GetBytes(marker), b => Assert.Equal(0, reader.Push(b)));
    }
}
