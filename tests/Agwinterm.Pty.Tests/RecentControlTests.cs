namespace Agwinterm.Pty.Tests;

public class RecentControlTests
{
    private sealed class Host : IWindowHost, IRecentHost
    {
        public int Calls;
        public object RecentList() { Calls++; return new { entries = Array.Empty<object>() }; }
        public object RecentOpen(string id, string? window) { Calls++; return new { id, window }; }
        public object RecentClear(string? id) { Calls++; return new { id }; }
        public ISessionHost? ResolveWindow(string? s) => null;
        public IReadOnlyList<WindowSnapshot> Windows() => [];
        public string WindowNew(string? n) => throw new NotSupportedException();
        public bool WindowSelect(string? s) => throw new NotSupportedException();
        public bool WindowClose(string? s) => throw new NotSupportedException();
        public bool WindowDelete(string? s) => throw new NotSupportedException();
        public bool WindowRename(string? s, string n) => throw new NotSupportedException();
        public bool WindowResize(string? s, int w, int h) => throw new NotSupportedException();
        public bool WindowMove(string? s, int x, int y) => throw new NotSupportedException();
        public bool WindowZoom(string? s) => throw new NotSupportedException();
    }
    [Theory]
    [InlineData("{\"cmd\":\"recent.clear\",\"args\":{\"id\":\"bad\"}}")]
    [InlineData("{\"cmd\":\"recent.list\",\"target\":\"active\"}")]
    [InlineData("{\"cmd\":\"recent.list\",\"window\":\"active\"}")]
    [InlineData("{\"cmd\":\"recent.open\",\"window\":5}")]
    [InlineData("{\"cmd\":\"recent.open\",\"window\":\"\"}")]
    [InlineData("{\"cmd\":\"recent.clear\",\"args\":{\"id\":null}}")]
    [InlineData("{\"cmd\":\"recent.clear\",\"extra\":true}")]
    [InlineData("{\"cmd\":\"recent.clear\",\"cmd\":\"recent.clear\"}")]
    [InlineData("{\"cmd\":\"recent.clear\",\"args\":{\"id\":\"00000000-0000-0000-0000-000000000001\",\"id\":\"00000000-0000-0000-0000-000000000002\"}}")]
    public void InvalidRawRequestNeverReachesHistory(string json)
    {
        var host = new Host(); using var server = new ControlServer(new FakeSessionHost(), host, "unused");
        Assert.Contains("\"ok\":false", server.Dispatch(json)); Assert.Equal(0, host.Calls);
    }
    [Fact] public void GlobalReadDoesNotRequireAWindowOrSession()
    {
        var host = new Host(); using var server = new ControlServer(new FakeSessionHost(), host, "unused");
        Assert.Contains("\"entries\":[]", server.Dispatch("{\"cmd\":\"recent.list\"}")); Assert.Equal(1, host.Calls);
    }
}
