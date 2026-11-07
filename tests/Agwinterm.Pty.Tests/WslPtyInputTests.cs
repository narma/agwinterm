using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class WslPtyInputTests
{
    [Fact]
    public async Task ResizeReturnsWhileInputIsBlockedAndDeliversTheLatestSize()
    {
        using var pipe = new StalledPipe();
        using var frames = new WslPtyConnection.InputFrames(pipe);
        var input = Task.Run(() => frames.Write(new byte[] { 0xff, 0 }));
        try
        {
            await pipe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(() =>
            {
                frames.Resize(80, 24);
                frames.Resize(100, 40);
            }).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { pipe.Resume.Set(); }

        await input;
        await pipe.Flushed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 0, 0, 0, 2, 0xff, 0, 0, 0, 0, 0, 0, 100, 0, 40 }, pipe.ToArray());
    }

    private sealed class StalledPipe : MemoryStream
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Flushed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Resume = new();

        public override void Write(ReadOnlySpan<byte> data)
        {
            Started.TrySetResult();
            Resume.Wait();
            base.Write(data);
        }
        public override void Flush() => Flushed.TrySetResult();
        protected override void Dispose(bool disposing)
        {
            if (disposing) Resume.Dispose();
            base.Dispose(disposing);
        }
    }
}
