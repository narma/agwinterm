using System.IO.Pipes;
using System.Threading.Channels;
using Agwinterm.Pty.Proto;
using Agwinterm.Core;
using System.Text.Json;

namespace Agwinterm.Pty;

internal sealed class HostEffectChannel
{
    private const int MaximumBytes=4*1024*1024;
    private readonly object _gate=new();
    private const int MaximumReliableRecords=256;
    // One reserved slot holds the latest title and directory. A slow effect
    // reader must not turn normal title churn into an attachment failure.
    private readonly Channel<PresentationEffect> _queue=Channel.CreateBounded<PresentationEffect>(MaximumReliableRecords+1);
    private PresentationEffect? _pendingMetadata;
    private int _reliableRecords;
    private readonly string _incarnation;
    private readonly ulong _epoch;
    private readonly Action _overflow;
    private int _bytes;
    private ulong _sequence;
    private bool _closed;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,TaskCompletionSource<ClipboardResult>> _clipboard=new();
    public ClipboardResult ExchangeClipboard(ClipboardRequest request)
    {
        if(!request.IsValid()) return ClipboardResult.Invalid;
        lock(_gate) if(_closed) return ClipboardResult.Denied;
        string id=Guid.NewGuid().ToString("N");
        var completion=new TaskCompletionSource<ClipboardResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _clipboard[id]=completion;
        try
        {
            Publish(new() {Kind=10,Title=id,Text=JsonSerializer.Serialize(request),CompletionSequence=DateTime.UtcNow.AddSeconds(3).Ticks});
            return completion.Task.Wait(TimeSpan.FromSeconds(5)) ? completion.Task.Result : ClipboardResult.IoError;
        }
        finally { _clipboard.TryRemove(id,out _); }
    }
    public string PipeName { get; }
    public HostEffectChannel(string name,string incarnation,ulong epoch,Action overflow)
    { PipeName=name; _incarnation=incarnation; _epoch=epoch; _overflow=overflow; }
    public void Publish(PresentationEffect effect)
    {
        bool overflow=false;
        lock(_gate)
        {
            if(_closed) return;
            int bytes=checked((effect.Title.Length+effect.Text.Length)*2+128);
            bool metadata=effect.Kind == 7;
            if(metadata && bytes <= MaximumBytes && _pendingMetadata is { } pending)
            {
                pending.Title=effect.Title;
                pending.Text=effect.Text;
                return;
            }
            if(bytes > (effect.Kind == 10 ? MaximumBytes : 1024*1024) || !metadata && (bytes > MaximumBytes-_bytes || _reliableRecords >= MaximumReliableRecords)) overflow=true;
            else
            {
                effect=effect.Clone(); effect.Incarnation=_incarnation; effect.AttachmentEpoch=_epoch; effect.Sequence=++_sequence;
                if(!_queue.Writer.TryWrite(effect)) overflow=true;
                else if(metadata) _pendingMetadata=effect;
                else { _bytes+=bytes; _reliableRecords++; }
            }
            if(overflow) { _closed=true; _queue.Writer.TryComplete(new IOException("Owner effect queue overflow.")); }
        }
        // Native callbacks may hold the feed lock. Never wait on attachment locks here.
        if(overflow) ThreadPool.QueueUserWorkItem(_=>_overflow());
    }
    public void Complete()
    {
        lock(_gate) { _closed=true; _queue.Writer.TryComplete(); }
        foreach(var pending in _clipboard.Values) pending.TrySetResult(ClipboardResult.IoError);
    }
    public async Task RunAsync(CancellationToken cancellation)
    {
        using var pipe=new NamedPipeServerStream(PipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var connect=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        connect.CancelAfter(TimeSpan.FromSeconds(10));
        await pipe.WaitForConnectionAsync(connect.Token).ConfigureAwait(false);
        await foreach(var effect in _queue.Reader.ReadAllAsync(cancellation).ConfigureAwait(false))
        {
            if(effect.Kind == 10)
            {
                lock(_gate)
                    if(_closed || !_clipboard.ContainsKey(effect.Title)) effect.CompletionSequence=0;
            }
            if(effect.Kind == 7)
            {
                // Freeze the record before serialization. Further changes get
                // a new slot, even if this pipe write is still in flight.
                lock(_gate) _pendingMetadata=null;
            }
            await PresentationQueryWire.WriteAsync(pipe,effect,cancellation).ConfigureAwait(false);
            if(effect.Kind == 10)
            {
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                var reply=await PresentationQueryWire.ReadAsync(pipe,PresentationEffect.Parser,timeout.Token).ConfigureAwait(false);
                if(reply.Kind != 10 || reply.Incarnation != _incarnation || reply.AttachmentEpoch != _epoch ||
                    reply.Sequence != effect.Sequence || reply.Title != effect.Title || reply.Text.Length > 2*1024*1024)
                    throw new InvalidDataException("Invalid clipboard receipt.");
                var result=JsonSerializer.Deserialize<ClipboardResult>(reply.Text) ?? ClipboardResult.IoError;
                if(_clipboard.TryGetValue(effect.Title,out var completion)) completion.TrySetResult(result);
            }
            if(effect.Kind != 7)
                lock(_gate) { _bytes-=(effect.Title.Length+effect.Text.Length)*2+128; _reliableRecords--; }
        }
        await PresentationQueryWire.WriteAsync(pipe,new PresentationEffect { Incarnation=_incarnation,
            AttachmentEpoch=_epoch,Sequence=++_sequence,Kind=0 },cancellation).ConfigureAwait(false);
    }
}
