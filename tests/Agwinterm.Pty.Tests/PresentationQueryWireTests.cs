using Agwinterm.Pty.Proto;
using Xunit;

namespace Agwinterm.Pty.Tests;

public class PresentationQueryWireTests
{
    [Fact]
    public async Task RoundtripKeepsIdentityUnicodeAndCoordinates()
    {
        using var stream = new MemoryStream();
        var reply = new PresentationQueryReply { Incarnation="owner",AttachmentEpoch=3,Generation=17,Status="ok",Truncated=true };
        reply.Rows.Add("é界😀");
        reply.Matches.Add(new PresentationSearchMatch { Line=7,Column=4,EndLine=8,EndColumn=2 });
        await PresentationQueryWire.WriteAsync(stream,reply,default);
        stream.Position=0;
        var actual = await PresentationQueryWire.ReadAsync(stream,PresentationQueryReply.Parser,default);
        Assert.Equal(reply,actual);
    }

    [Fact]
    public async Task InvalidPrefixAndTruncatedBodyFailBeforeParsing()
    {
        using var oversized = new MemoryStream(BitConverter.GetBytes(PresentationQueryWire.MaximumBytes+1));
        await Assert.ThrowsAsync<InvalidDataException>(() => PresentationQueryWire.ReadAsync(oversized,PresentationQuery.Parser,default));
        Assert.Equal(4,oversized.Position);
        using var truncated = new MemoryStream([8,0,0,0,1]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => PresentationQueryWire.ReadAsync(truncated,PresentationQuery.Parser,default));
    }
}
