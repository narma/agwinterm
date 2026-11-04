namespace Agwinterm.Pty.Tests;

public class AgtermPageStorageTests
{
    [Fact]
    public void UnrelatedOriginsCannotSharePersistentBrowserState()
    {
        var original = AgtermRemoteBindingTests.Binding();
        var variants = new[]
        {
            original,
            original with { Target = original.Target with { Distribution = "OtherLinux" } },
            original with { Target = original.Target with { User = "other" } },
            original with { Target = original.Target with { Destination = "other@peer" } },
            original with { Target = original.Target with { OriginSocket = "/tmp/other.sock" } },
            original with { Endpoint = original.Endpoint with { SocketDirectory = "/tmp/other-endpoint" } },
            original with { OriginWindow = Guid.NewGuid().ToString() },
            original with { OriginSession = Guid.NewGuid().ToString() }
        };
        Assert.Equal(variants.Length, variants.Select(AgtermPageStorage.Partition).Distinct().Count());
        Assert.Equal(AgtermPageStorage.Partition(original), AgtermPageStorage.Partition(original with { TransportTicket = "reconnected", Role = "right" }));
    }
}
