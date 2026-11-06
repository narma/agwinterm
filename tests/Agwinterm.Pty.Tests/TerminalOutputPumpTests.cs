using System.IO.Pipes;
using System.Reflection;
using System.Text;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class TerminalOutputPumpTests
{
    [Fact]
    public async Task DrainingPipeBurstsPreservesEveryByteAndFinalOutputAcrossBudgets()
    {
        using var reader = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None, 65536);
        using var writer = new AnonymousPipeClientStream(PipeDirection.Out, reader.ClientSafePipeHandle);
        using var session = new TerminalSession(80, 24);
        // Cross the drain byte budget several times; finish inside an escape sequence
        // split across writes, then close the pipe without another redraw request.
        byte[] payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("line\r\n", 100000)) + "\x1b[31mFINAL_OUTPUT");
        using var tapped = new MemoryStream();
        int notifications = 0;
        session.RawOutput += bytes => tapped.Write(bytes);
        session.OutputReceived += () => Interlocked.Increment(ref notifications);
        var method = typeof(TerminalSession).GetMethod("PumpLoop", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task pumping = Task.Run(() => method.Invoke(session, [reader]));
        Task writing = Task.Run(() =>
        {
            for (int start = 0; start < payload.Length; start += 3001)
                writer.Write(payload, start, Math.Min(3001, payload.Length - start));
            writer.Dispose();
        });
        await Task.WhenAll(pumping, writing).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(payload, tapped.ToArray());
        Assert.True(notifications > 0);
        lock (session.SyncRoot) Assert.Contains("FINAL_OUTPUT", string.Join("\n", session.Emulator.DumpBuffer()));
    }
}
