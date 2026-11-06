using System.Threading.Channels;

namespace Agwinterm.Pty;

internal sealed class PresentationInputSender : IDisposable
{
    private readonly record struct Item(byte[] Bytes, bool Started = false);
    private readonly Channel<Item> _queue=Channel.CreateBounded<Item>(new BoundedChannelOptions(4096)
    { SingleReader=true, SingleWriter=false });
    private readonly object _gate=new();
    private readonly Stream _pipe;
    private readonly PresentationPipeIo? _direct;
    private readonly string _incarnation;
    private readonly ulong _epoch;
    private readonly CancellationTokenSource _cancel=new();
    private int _bytes;
    private ulong _sequence;
    private long _latencyEnter;
    private bool _closed, _completed, _writing;
    public Task Completion { get; }
    internal Exception? Failure { get; private set; }
    public PresentationInputSender(Stream pipe,string incarnation,ulong epoch)
    {
        _pipe=pipe; _incarnation=incarnation; _epoch=epoch;
        if(pipe is System.IO.Pipes.PipeStream native) _direct=new(native,_cancel.Token);
        Completion=Task.Factory.StartNew(Run,CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
    }
    public TerminalInputAcceptance Submit(TerminalInput input,bool persistentUiThread=false)
    {
        lock (_gate)
        {
            if (_closed) return TerminalInputAcceptance.Closed;
            if (Agwinterm.Core.TerminalLatencyTrace.Enabled && input is TerminalInput.Key { VirtualKey:13, Action: > 0 })
                Agwinterm.Core.TerminalLatencyTrace.Record(9,++_latencyEnter);
            if (input is TerminalInput.Paste paste && !OwnerInputQueue.PasteFits(paste.Value)) return TerminalInputAcceptance.Rejected;
            byte[] bytes;
            try { bytes=PresentationInputCodec.Encode(input,_incarnation,_epoch,checked(_sequence+1)); }
            catch(InvalidDataException) { CloseLocked(); return TerminalInputAcceptance.Overflow; }
            if(bytes.Length > OwnerInputQueue.MaximumBytes-_bytes)
            { if (input is TerminalInput.Paste) return TerminalInputAcceptance.Rejected;
              CloseLocked(); return TerminalInputAcceptance.Overflow; }
            bool started=false;
            // Only the persistent UI thread may start IO here. A transient caller
            // could exit while IO is pending; Windows would abort that operation.
            if(persistentUiThread && _direct is not null && !_writing)
            {
                // Submit overlapped IO only: never wait on UI. A local empty pipe
                // commonly completes immediately, avoiding a per-key thread handoff.
                try
                {
                    if(_direct.TryWrite(bytes)) { _sequence++; return TerminalInputAcceptance.Accepted; }
                    started=true; _writing=true;
                }
                catch(Exception ex) when(ex is IOException or ObjectDisposedException or OperationCanceledException)
                { Failure=ex; CloseLocked(); return TerminalInputAcceptance.DeliveryUncertain; }
            }
            if(!_queue.Writer.TryWrite(new(bytes,started)))
            { if (!started && input is TerminalInput.Paste) return TerminalInputAcceptance.Rejected;
              CloseLocked(); return started ? TerminalInputAcceptance.DeliveryUncertain : TerminalInputAcceptance.Overflow; }
            // Reserve the writer for queued records too, before releasing the
            // gate. A fast UI key must not overtake an older queued frame credit.
            _writing=true;
            _sequence++; _bytes+=bytes.Length;
            return TerminalInputAcceptance.Accepted;
        }
    }
    private void Run()
    {
        var batch=new System.Buffers.ArrayBufferWriter<byte>(256);
        try
        {
            while(_queue.Reader.WaitToReadAsync(_cancel.Token).AsTask().GetAwaiter().GetResult())
            {
                Item item;
                lock(_gate)
                {
                    if(!_queue.Reader.TryRead(out item)) { _writing=false; continue; }
                    _writing=true; _bytes-=item.Bytes.Length;
                    if(!item.Started)
                    {
                        batch.Clear();
                        do
                        {
                            item.Bytes.CopyTo(batch.GetSpan(item.Bytes.Length)); batch.Advance(item.Bytes.Length);
                            if(batch.WrittenCount >= 65536 || !_queue.Reader.TryRead(out item)) break;
                            _bytes-=item.Bytes.Length;
                        } while(true);
                    }
                }
                if(item.Started) _direct!.CompleteWrite();
                else if(_direct is not null) _direct.Write(batch.WrittenMemory);
                else _pipe.WriteAsync(batch.WrittenMemory,_cancel.Token).AsTask().GetAwaiter().GetResult();
                if(batch.Capacity > 65536) batch=new(256);
                lock(_gate) if(!_queue.Reader.TryPeek(out _)) _writing=false;
            }
        }
        catch(Exception ex) when(ex is IOException or ObjectDisposedException or OperationCanceledException) { Failure=ex; }
        finally
        {
            // A pending UI submission may still be in the queue when cancellation
            // stops WaitToRead. Drain its kernel operation before freeing the pin.
            lock(_gate) { _closed=true; _queue.Writer.TryComplete(); }
            try { _direct?.CompleteWrite(); }
            catch(Exception ex) when(ex is IOException or OperationCanceledException) { }
            _direct?.Dispose();
            lock(_gate) { _completed=true; _cancel.Dispose(); }
            while(_queue.Reader.TryRead(out _)) { }
            _pipe.Dispose();
        }
    }
    private void CloseLocked() { _closed=true; _queue.Writer.TryComplete(); if(!_completed) _cancel.Cancel(); }
    public void Dispose() { lock(_gate) CloseLocked(); }
}
