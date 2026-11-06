using System.Text;
using System.Threading.Channels;
using Agwinterm.Core;

namespace Agwinterm.Pty;

/// <summary>One ordered owner-side encoder/writer. Enqueue never waits for PTY IO and
/// can be called by native response callbacks under the feed lock. Overflow closes
/// acceptance explicitly: silently dropping a key release or replaying input is unsafe.</summary>
internal sealed class OwnerInputQueue : IDisposable
{
    internal const int MaximumBytes = 1024 * 1024;
    private readonly record struct Item(TerminalInput Input, int Bytes, long Enqueued);
    private readonly Channel<Item> _queue = Channel.CreateBounded<Item>(new BoundedChannelOptions(4096)
    { SingleReader = false, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly object _gate = new();
    private readonly object _writerGate = new();
    private readonly TerminalSession _session;
    private int _bytes;
    private bool _closed;
    public Task Completion { get; }
    public OwnerInputQueue(TerminalSession session)
    {
        _session = session;
        Completion = Task.Factory.StartNew(Run,CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
    }

    public TerminalInputAcceptance Submit(TerminalInput input)
    {
        // Direct local callers receive the same bounded native-encoder contract
        // as remote records, without serializing a protobuf just to validate it.
        if(input is TerminalInput.Key keyInput && (keyInput.Action > 2 || keyInput.VirtualKey > 255 ||
            keyInput.WindowsUnicode is < 0 or > 65535 || keyInput.ScanCode > 65535 ||
            Encoding.UTF8.GetByteCount(keyInput.TextValue) > 4096 || keyInput.WindowsText.Length > 128))
            throw new ArgumentException("Invalid owner key input.",nameof(input));
        if(input is TerminalInput.Mouse mouse && (mouse.Action > 2 || !float.IsFinite(mouse.X) || !float.IsFinite(mouse.Y) ||
            mouse.CellWidth is < 1 or > 65535 || mouse.CellHeight is < 1 or > 65535 ||
            mouse.Column is < -1 or >= 10000 || mouse.Row is < -1 or >= 10000 || (mouse.Column < 0) != (mouse.Row < 0)))
            throw new ArgumentException("Invalid owner mouse input.",nameof(input));
        if (input is TerminalInput.Command command && command.Value is not (TerminalCommand.InjectOutput or TerminalCommand.Clear or TerminalCommand.Reset or TerminalCommand.ScrollbackLimit { Rows:>=0 }))
            throw new ArgumentException("Unsupported ordered terminal command.",nameof(input));
        if(input is not (TerminalInput.Key or TerminalInput.Mouse or TerminalInput.Text or TerminalInput.Paste or TerminalInput.Focus or TerminalInput.Raw or TerminalInput.Command))
            throw new ArgumentException("Unsupported owner input.",nameof(input));
        int size = input switch
        {
            TerminalInput.Raw raw => raw.Bytes.Length,
            TerminalInput.Command { Value:TerminalCommand.InjectOutput inject } => checked(inject.Bytes.Length+128),
            TerminalInput.Text text => checked(text.Value.Length*2+64),
            TerminalInput.Paste paste => checked(paste.Value.Length*2+64),
            TerminalInput.Key key => checked((key.TextValue.Length+key.WindowsText.Length)*2+128),
            _ => 64
        };
        lock (_gate)
        {
            if (_closed || _session.InputClosed && !(input is TerminalInput.Command && _session.HasExited)) return TerminalInputAcceptance.Closed;
            if (size > MaximumBytes-_bytes) return Overflow();
            // The caller may reuse raw storage immediately after acceptance.
            if (input is TerminalInput.Raw raw) input = new TerminalInput.Raw(raw.Bytes.ToArray());
            if (input is TerminalInput.Command { Value:TerminalCommand.InjectOutput injectCommand })
                input = new TerminalInput.Command(new TerminalCommand.InjectOutput(injectCommand.Bytes.ToArray()));
            if (!_queue.Writer.TryWrite(new(input,size,TerminalLatencyTrace.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0))) return Overflow();
            _bytes += size;
            return TerminalInputAcceptance.Accepted;
        }
    }
    private TerminalInputAcceptance Overflow()
    {
        _closed = true; _queue.Writer.TryComplete(); return TerminalInputAcceptance.Overflow;
    }
    // Only the dedicated transport reader calls this, never UI or native callbacks.
    // Claim the same ordered writer when idle instead of scheduling another thread.
    // Replies still enqueue without IO while the terminal feed lock is held.
    internal TerminalInputAcceptance SubmitAndDrain(IReadOnlyList<TerminalInput> inputs,bool waitForWriter=false)
    {
        lock(_gate)
        {
            foreach(var input in inputs)
            {
                var accepted=Submit(input);
                if(accepted != TerminalInputAcceptance.Accepted) return accepted;
            }
        }
        if(waitForWriter) Monitor.Enter(_writerGate);
        else if(!Monitor.TryEnter(_writerGate)) return TerminalInputAcceptance.Accepted;
        try { Drain(); return TerminalInputAcceptance.Accepted; }
        catch(Exception ex) when(ex is IOException or ObjectDisposedException or InvalidOperationException)
        { System.Diagnostics.Trace.WriteLine("Owner writer failed: "+ex); _session.MarkInputClosed(); Dispose(); return TerminalInputAcceptance.DeliveryUncertain; }
        finally { Monitor.Exit(_writerGate); }
    }
    private void Run()
    {
        try
        {
            while (_queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                lock(_writerGate) Drain();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { System.Diagnostics.Trace.WriteLine("Owner input queue failed: "+ex); _session.MarkInputClosed(); }
        finally
        {
            lock (_gate) { _closed = true; _queue.Writer.TryComplete(); }
            lock (_writerGate) while (_queue.Reader.TryRead(out _)) { }
        }
    }
    private System.Buffers.ArrayBufferWriter<byte> _batch = new(256);
    private void Drain()
    {
            while (_queue.Reader.TryRead(out var item))
            {
                if(item.Input is TerminalInput.Command command)
                {
                    lock(_gate) _bytes-=item.Bytes;
                    if(!_session.InputClosed || _session.HasExited) ExecuteCommand(command.Value);
                    continue;
                }
                _batch.Clear();
                long waiting=TerminalLatencyTrace.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                lock (_session.SyncRoot)
                {
                    using var timing=new TerminalLatencyTrace.LockTiming(waiting,42);
                    int count=0;
                    do
                    {
                        lock (_gate) _bytes -= item.Bytes;
                        if (_session.InputClosed) break;
                        if (item.Enqueued != 0) TerminalLatencyTrace.Record(40,Math.Max(1,System.Diagnostics.Stopwatch.GetTimestamp()-item.Enqueued));
                        byte[] bytes=item.Input is TerminalInput.Raw raw ? raw.Bytes.ToArray()
                            : Encode((GhosttyTerminalCore)_session.Emulator,item.Input);
                        bytes.CopyTo(_batch.GetSpan(bytes.Length)); _batch.Advance(bytes.Length);
                        count++;
                        // Drain only input already available. Never wait or add a coalescing
                        // timer, and keep a bounded encode turn before releasing the feed lock.
                    } while(count < 32 && _batch.WrittenCount < 4096 && _queue.Reader.TryPeek(out var next) &&
                        next.Input is not TerminalInput.Command && _queue.Reader.TryRead(out item));
                }
                if (_batch.WrittenCount != 0) _session.WriteOwner(_batch.WrittenSpan);
                if (_batch.Capacity > 65536) _batch=new(256);
            }
    }
    private void ExecuteCommand(TerminalCommand command)
    {
        switch(command)
        {
            case TerminalCommand.InjectOutput inject: _session.Inject(inject.Bytes.Span); break;
            case TerminalCommand.Clear clear: _session.Inject(clear.History ? "\u001b[2J\u001b[3J\u001b[H"u8 : "\u001b[2J\u001b[H"u8); break;
            case TerminalCommand.Reset: _session.Inject("\u001bc"u8); break;
            case TerminalCommand.ScrollbackLimit limit: _session.MutateLocked(core=>core.ScrollbackMax=limit.Rows); break;
        }
    }
    internal static byte[] Encode(GhosttyTerminalCore core, TerminalInput input) => input switch
    {
        // VK=0 is committed WM_CHAR/IME text, not a physical Windows key
        // record. Preserve the local Ghostty character path in Win32 mode too.
        TerminalInput.Key key when key.VirtualKey != 0 && core.Win32InputMode => EncodeWindowsKey(key),
        TerminalInput.Key key => core.EncodeKey(key.VirtualKey,key.Action,key.Modifiers,key.TextValue,key.Unshifted,key.ConsumedModifiers),
        TerminalInput.Text text => Encoding.UTF8.GetBytes(text.Value),
        TerminalInput.Paste paste => core.EncodePaste(paste.Value),
        TerminalInput.Mouse mouse => core.EncodeMouse(mouse.Action,mouse.Button,mouse.Modifiers,
            core.MouseSgrPixels || mouse.Column < 0 ? mouse.X : mouse.Column*mouse.CellWidth,
            core.MouseSgrPixels || mouse.Row < 0 ? mouse.Y : mouse.Row*mouse.CellHeight,mouse.CellWidth,mouse.CellHeight),
        TerminalInput.Focus focus => core.FocusReporting ? Encoding.ASCII.GetBytes(focus.Focused ? "\x1b[I" : "\x1b[O") : [],
        TerminalInput.Raw raw => raw.Bytes.ToArray(),
        _ => throw new ArgumentException("Unknown terminal input.",nameof(input))
    };
    private static byte[] EncodeWindowsKey(TerminalInput.Key key)
    {
        var result=new StringBuilder();
        if (key.WindowsText.Length == 0) Append(key.WindowsUnicode);
        else foreach(char unit in key.WindowsText) Append(unit);
        return Encoding.UTF8.GetBytes(result.ToString());
        void Append(int unit) => result.Append($"\x1b[{key.VirtualKey};{key.ScanCode};{unit};{(key.Action == 0 ? 0 : 1)};{key.WindowsControlState};1_");
    }
    public void Dispose() { lock (_gate) { _closed = true; _queue.Writer.TryComplete(); } }
}
