using System.Text;
using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class AgtermPageProtocolTests
{
    [Fact]
    public void ActualPinnedPageFramesDecodeAndRoundTrip()
    {
        using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Presentation", "pinned-frames.json")));
        foreach (var item in fixtures.RootElement.EnumerateArray().Where(x => x.GetProperty("channel").GetString() == "page"))
        {
            var frame = AgtermPageProtocol.Decode(JsonSerializer.SerializeToUtf8Bytes(item.GetProperty("frame")));
            var again = AgtermPageProtocol.Decode(AgtermPageProtocol.Encode(frame).AsMemory()[..^1]);
            Assert.Equal(frame.Kind, again.Kind); Assert.Equal(frame.Id, again.Id);
            Assert.Equal(frame.Text, again.Text); Assert.Equal(frame.Data, again.Data);
        }
    }

    [Theory]
    [InlineData("{\"kind\":\"end\",\"id\":1,\"id\":2}")]
    [InlineData("{\"kind\":\"end\",\"id\":0}")]
    [InlineData("{\"kind\":\"ping\",\"id\":1}")]
    [InlineData("{\"kind\":\"resource\",\"id\":1,\"path\":null}")]
    [InlineData("{\"kind\":\"chunk\",\"id\":1,\"data\":\"!\"}")]
    public void InvalidFramesRefuse(string json) => Assert.Throws<IOException>(() => AgtermPageProtocol.Decode(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void ChunkAndEncodedFrameCapsAreIndependent()
    {
        Assert.Equal(65536, AgtermPageProtocol.Decode(AgtermPageProtocol.Encode(new("chunk", 1, Data: new byte[65536])).AsMemory()[..^1]).Data!.Length);
        Assert.Throws<IOException>(() => AgtermPageProtocol.Encode(new("chunk", 1, Data: new byte[65537])));
        Assert.Throws<IOException>(() => AgtermPageProtocol.Encode(new("reply", 1, new string('a', 262144))));
    }

    private static AgtermOriginPresentationScope Scope() => new("account", Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
        new("Ubuntu", "alice", "origin", "/usr/bin/zmx", "/tmp/zmx"));

    [Fact]
    public void WrongScopeDoesNotConsumeClaimAndOnlyOneConcurrentClaimWins()
    {
        var scope = Scope(); var grant = new AgtermPageGrant(scope, 1, 2, true, 100);
        Assert.Throws<IOException>(() => grant.Claim(scope with { Account = "other" }, 1, 2, 101));
        Assert.Throws<IOException>(() => grant.Claim(scope with { Window = Guid.NewGuid().ToString() }, 1, 2, 101));
        Assert.Throws<IOException>(() => grant.Claim(scope with { Session = Guid.NewGuid().ToString() }, 1, 2, 101));
        Assert.Throws<IOException>(() => grant.Claim(scope, 2, 2, 101));
        Assert.Throws<IOException>(() => grant.Claim(scope, 1, 3, 101));
        int claimed = 0;
        Parallel.For(0, 32, _ => { try { grant.Claim(scope, 1, 2, 101); Interlocked.Increment(ref claimed); } catch (IOException) { } });
        Assert.Equal(1, claimed);
        int ended = 0; grant.Ended += () => ended++;
        grant.Close(); grant.Close(); Assert.Equal(1, ended); Assert.False(grant.IsAlive);
    }

    [Fact]
    public void ExpiryRetiresOnlyUnclaimedFilePages()
    {
        var scope = Scope(); var expired = new AgtermPageGrant(scope, 1, 2, true, 100);
        expired.Expire(30100); Assert.False(expired.IsAlive);
        Assert.Throws<IOException>(() => expired.Claim(scope, 1, 2, 30100));
        var claimed = new AgtermPageGrant(scope, 1, 2, true, 100); claimed.Claim(scope, 1, 2, 101);
        claimed.Expire(30100); Assert.True(claimed.IsAlive);
        var url = new AgtermPageGrant(scope, 1, 2, false, 100); url.Expire(30100); Assert.True(url.IsAlive);
        Assert.Throws<IOException>(() => url.Claim(scope, 1, 2, 101));
    }
}
