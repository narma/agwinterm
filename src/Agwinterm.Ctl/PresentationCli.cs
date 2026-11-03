using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Agwinterm.Pty;

namespace Agwinterm.Ctl;

/// <summary>Scoped presentation relay only. Stdout contains protocol frames, never command replies.</summary>
public static class PresentationCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (!CtlArguments.TryParse(args, out var positionals, out var options, out _, out _, out _, out var error)
                || positionals.Count != 3) throw new ArgumentException(error ?? "Invalid presentation relay arguments");
            string Required(string key) => options.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value)
                ? value : throw new ArgumentException("Missing presentation option: " + key);
            string window = Required("window"), session = Required("target");
            bool jobChannel = positionals[2] == "job";
            bool pageChannel = positionals[2] == "page";
            if (!Guid.TryParseExact(window, "D", out _) || !Guid.TryParseExact(session, "D", out _))
                throw new ArgumentException("Exact window and session IDs required");
            var endpoint = JsonSerializer.SerializeToElement(new { distribution = Required("distribution"), user = Required("user"),
                origin = Required("origin"), executable = Required("executable"), socketDirectory = Required("socket-directory"), adapter = Required("adapter") });
            _ = WslOriginExportRequest.Parse(endpoint, null);
            string pipeName = options.GetValueOrDefault("socket") ?? options.GetValueOrDefault("pipe")
                ?? Environment.GetEnvironmentVariable("AGWINTERM_PIPE") ?? "agwinterm";
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification);
            using var stop = new CancellationTokenSource();
            await pipe.ConnectAsync(5000, stop.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            // Anonymous stdout is synchronous on Windows. Its sole owner is a background thread,
            // just like stdin; a blocked WriteFile must not hold process shutdown or be disposed
            // concurrently. At most one bounded frame is outstanding and the caller owns its timeout.
            var outputLines = Channel.CreateBounded<(string Line, TaskCompletionSource Done)>(1);
            new Thread(() =>
            {
                try
                {
                    using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), 4096) { AutoFlush = true };
                    while (outputLines.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                        while (outputLines.Reader.TryRead(out var item))
                        {
                            try { output.WriteLine(item.Line); item.Done.TrySetResult(); }
                            catch (Exception ex) { item.Done.TrySetException(ex); return; }
                        }
                }
                catch (Exception ex) { outputLines.Writer.TryComplete(ex); }
            }) { IsBackground = true, Name = "presentation-stdout" }.Start();
            Task WriteOutput(string line)
            {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!outputLines.Writer.TryWrite((line, done))) throw new IOException("Presentation output unavailable");
                return done.Task;
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { cmd = pageChannel ? "zmx.export.page" : jobChannel ? "zmx.export.job" : "zmx.export.present", window, target = session, args = endpoint,
                job = jobChannel ? Required("job") : null, token = pageChannel ? Required("token") : null })).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            async Task Copy(TextReader source, Func<string, Task> write)
            {
                while (true)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    if (!pageChannel) deadline.CancelAfter(TimeSpan.FromSeconds(35));
                    var line = await AgtermPresentationStream.ReadLineAsync(source, deadline.Token).ConfigureAwait(false);
                    if (line is null) return;
                    if (pageChannel) _ = AgtermPageProtocol.Decode(Encoding.UTF8.GetBytes(line));
                    else if (!jobChannel) _ = AgtermPresentationProtocol.Decode(Encoding.UTF8.GetBytes(line));
                    else { using var frame = JsonDocument.Parse(line); }
                    Task writing = write(line);
                    try { await writing.WaitAsync(TimeSpan.FromSeconds(5), stop.Token).ConfigureAwait(false); }
                    catch { pipe.Dispose(); throw; }
                }
            }
            // Windows anonymous stdin is synchronous: cancellation cannot release ReadFile while SSH
            // keeps its write end open. A bounded background reader belongs to this short-lived CLI
            // process; it never prevents process exit, and no pending read is disposed underneath it.
            var lines = Channel.CreateBounded<string>(pageChannel ? 8 : 256);
            var inputThread = new Thread(() =>
            {
                try
                {
                    using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true));
                    var line = new StringBuilder();
                    int ch;
                    while (!stop.IsCancellationRequested && (ch = input.Read()) >= 0)
                    {
                        if (ch == '\n')
                        {
                            string value = line.ToString();
                            if (pageChannel) _ = AgtermPageProtocol.Decode(Encoding.UTF8.GetBytes(value));
                            else if (!jobChannel) _ = AgtermPresentationProtocol.Decode(Encoding.UTF8.GetBytes(value));
                            else { using var frame = JsonDocument.Parse(value); }
                            if (pageChannel) lines.Writer.WriteAsync(value, stop.Token).AsTask().GetAwaiter().GetResult();
                            else if (!lines.Writer.TryWrite(value)) throw new IOException("Presentation input queue overflow");
                            line.Clear();
                        }
                        else
                        {
                            if (line.Length >= AgtermPresentationProtocol.MaxFrameBytes) throw new IOException("Presentation input exceeds limit");
                            line.Append((char)ch);
                        }
                    }
                    if (line.Length != 0) throw new IOException("Truncated presentation input");
                    lines.Writer.TryComplete();
                }
                catch (Exception ex) { lines.Writer.TryComplete(ex); }
            }) { IsBackground = true, Name = "presentation-stdin" };
            inputThread.Start();
            async Task CopyInput()
            {
                await foreach (string line in lines.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
                {
                    Task writing = writer.WriteLineAsync(line);
                    try { await writing.WaitAsync(TimeSpan.FromSeconds(5), stop.Token).ConfigureAwait(false); }
                    catch { pipe.Dispose(); try { await writing.ConfigureAwait(false); } catch { } throw; }
                }
            }
            var tasks = new[] { CopyInput(), Copy(reader, WriteOutput) };
            try { await await Task.WhenAny(tasks).ConfigureAwait(false); }
            finally
            {
                stop.Cancel();
                outputLines.Writer.TryComplete();
                pipe.Dispose();
                try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
                try { writer.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or OperationCanceledException or TimeoutException)
        { Console.Error.WriteLine("Presentation relay unavailable: " + ex.Message); return 1; }
    }
}
