namespace Agwinterm.Pty.Tests;

public class InputLifecycleTests
{
    [Fact]
    public void HistoryReadBeforeFirstFrameCanReturnLaunchError()
    {
        using var backend=new ServerSessionBackend("unstarted-test",null,singleOwner:true);
        var session=new ServerSession(backend,"pane",30,4);
        try
        {
            Assert.Equal("",SurfaceText.Dump(session,new(true,0)));
            ((Agwinterm.Core.PresentedTerminalView)session.Emulator).ShowError("Cannot start shell");
            Assert.Equal("Cannot start shell",SurfaceText.Dump(session,new(true,0)));
            Assert.Contains("Cannot start shell",SurfaceText.DumpStyled(session,new(true,0)));
        }
        finally { session.Detach(); }
    }
    [Fact]
    public void OwnerStartupAcceptsEarlyFocusButBoundsPendingInput()
    {
        using var backend=new ServerSessionBackend("not-connected",null,singleOwner:true);
        var session=new ServerSession(backend,"pane",80,24);
        try
        {
            Assert.Equal(TerminalInputAcceptance.Accepted,session.SubmitInput(new TerminalInput.Focus(true)));
            Assert.Equal(TerminalInputAcceptance.Rejected, session.SubmitInput(new TerminalInput.Paste(new string('a', 600_000))));
            Assert.False(session.InputClosed);
            TerminalInputAcceptance result=TerminalInputAcceptance.Accepted;
            for(int i=0;i<5000 && result == TerminalInputAcceptance.Accepted;i++)
                result=session.SubmitInput(new TerminalInput.Text("early"));
            Assert.Equal(TerminalInputAcceptance.Overflow,result);
            Assert.True(session.InputClosed);
            Assert.Equal(TerminalInputAcceptance.Closed,session.SubmitInput(new TerminalInput.Focus(false)));
        }
        finally { session.Detach(); }
    }
    [Fact]
    public void InProcessInputCanCloseBeforeOutputCompletion()
    {
        using var session = new TerminalSession(80, 24);
        session.MarkInputClosed();
        Assert.True(session.InputClosed); Assert.False(session.HasExited);
        Assert.Throws<IOException>(() => session.Write("not consumed"u8));
        session.Inject("final-output"u8);
        Assert.Contains("final-output", session.SnapshotRow(0));
    }

    [Fact]
    public void HostedInputClosedStateRefusesWithoutClaimingChildExit()
    {
        // Constructor is cheap and never connects; no server or shared resource is started.
        using var backend = new ServerSessionBackend("not-connected", null, singleOwner: false);
        var session = new ServerSession(backend, "pane", 80, 24);
        session.MarkInputClosed();
        Assert.True(session.InputClosed); Assert.False(session.HasExited);
        Assert.Throws<IOException>(() => session.Write("not consumed"u8));
        session.Inject("final-output"u8);
        Assert.Contains("final-output", session.SnapshotRow(0));
        session.Detach(); // local cancellation only, no kill/control connection
    }

    [Fact]
    public void StoppedTransportKeepsItsVisibleReplicaUntilPaneDisposal()
    {
        using var backend = new ServerSessionBackend("not-connected", null, singleOwner: false);
        using var session = new ServerSession(backend, "pane", 80, 24);
        int exits = 0;
        session.Exited += _ => exits++;
        session.Stop();
        Assert.True(session.Stopped);
        Assert.True(session.HasExited);
        Assert.Throws<IOException>(() => session.Write("not consumed"u8));
        session.Inject("terminated"u8);
        session.Resize(90, 25);
        Assert.Contains("terminated", session.SnapshotRow(0));
        Assert.Equal(0, exits);
    }

    private class ScriptedChannel : Stream
    {
        internal int Reads; internal bool SawFinalOutput; internal bool Disposed;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Reads++;
            if (Reads == 2) SawFinalOutput = true; // output side finishes AFTER input write refusal
            if (Reads == 3) return ValueTask.FromResult(0);
            buffer.Span[0] = 1; return ValueTask.FromResult(1);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] b,int o,int n) => throw new NotSupportedException();
        public override void Write(byte[] b,int o,int n) => throw new NotSupportedException();
        public override void Flush() {} public override long Seek(long o,SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long n) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ChildInputFailureDoesNotCloseDuplexOutputEarly()
    {
        using var channel = new ScriptedChannel(); int attempted = 0;
        await HostInputPump.CopyAsync(channel, (_, _) => { attempted++; throw new IOException("child input closed"); }, CancellationToken.None);
        Assert.Equal(1, attempted); Assert.Equal(3, channel.Reads);
        Assert.True(channel.SawFinalOutput); Assert.False(channel.Disposed);
    }

    [Fact]
    public async Task HostCancellationStillEndsInputPumpWithoutDisposingAnOutstandingRead()
    {
        using var channel = new PendingChannel(); using var cancel = new CancellationTokenSource();
        var pump = HostInputPump.CopyAsync(channel, (_, _) => {}, cancel.Token);
        await channel.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(channel.Disposed); Assert.True(channel.ReadCompleted);
    }

    private sealed class PendingChannel : ScriptedChannel
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool ReadCompleted;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            Entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); return 0; }
            finally { ReadCompleted = true; }
        }
    }
}
