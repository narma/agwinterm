using System.Reflection;
using Agwinterm.Core;
using Agwinterm.Pty;
using Porta.Pty;

namespace Agwinterm.Pty.Tests;

public class TerminalSessionLifetimeTests
{
    public interface IDisposableCore : ITerminalCore, IDisposable { }
    public class CoreProxy : DispatchProxy
    {
        public int Disposals;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IDisposable.Dispose)) { Disposals++; return null; }
            throw new InvalidOperationException("Unexpected core call: " + method.Name);
        }
    }
    public class ConnectionProxy : DispatchProxy
    {
        public int Kills, Disposals;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IPtyConnection.Kill)) Kills++;
            else if (method.Name == nameof(IDisposable.Dispose)) Disposals++;
            else throw new InvalidOperationException("Unexpected connection call: " + method.Name);
            return null;
        }
    }

    [Fact]
    public void SpawnCompletingAfterCloseIsKilledAndDisposed()
    {
        using var session = new TerminalSession(20, 5);
        session.Dispose();
        var connection = DispatchProxy.Create<IPtyConnection, ConnectionProxy>();
        var publish = typeof(TerminalSession).GetMethod("PublishConnection", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var error = Assert.Throws<TargetInvocationException>(() => publish.Invoke(session, [connection]));
        Assert.IsType<ObjectDisposedException>(error.InnerException);
        Assert.Equal(1, ((ConnectionProxy)(object)connection).Kills);
        Assert.Equal(1, ((ConnectionProxy)(object)connection).Disposals);
        Assert.Null(session.ChildProcessId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleasesCoreExactlyOnceWithoutStartingAProcess(bool hosted)
    {
        using var session = new TerminalSession(20, 5);
        var core = DispatchProxy.Create<IDisposableCore, CoreProxy>();
        // Do not mutate the process-wide factory while unrelated tests create sessions.
        typeof(TerminalSession).GetField("<Emulator>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(session, core);
        if (hosted) Assert.True(session.DisposeHosted()); else session.Dispose();
        session.Detach(); session.Dispose();
        Assert.Equal(1, ((CoreProxy)(object)core).Disposals);
        Assert.Throws<ObjectDisposedException>(() => session.Inject(new byte[] { 65 }));
        Assert.Throws<ObjectDisposedException>(() => session.Resize(30, 10));
        Assert.Throws<ObjectDisposedException>(() => session.MutateLocked(_ => throw new Exception("must not run")));
    }
}
