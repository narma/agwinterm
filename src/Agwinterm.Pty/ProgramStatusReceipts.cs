namespace Agwinterm.Pty;

/// <summary>Best-effort delivery receipts must never stall effects or close terminal input.</summary>
internal sealed class ProgramStatusReceipts(Func<string,long,Task<bool>> send, CancellationToken cancellation,
    TimeSpan? retryDelay = null)
{
    private readonly object _gate = new();
    private readonly Dictionary<string,long> _pending = new(StringComparer.Ordinal);
    private bool _running;

    public void Enqueue(string id, long completion)
    {
        lock(_gate)
        {
            if(cancellation.IsCancellationRequested) return;
            // Mirrors the owner's bounded record store; an evicted receipt is
            // harmless because its completion remains available for replay.
            if(!_pending.ContainsKey(id) && _pending.Count >= 64) _pending.Remove(_pending.Keys.First());
            _pending[id] = completion;
            if(_running) return;
            _running=true;
            _=Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while(!cancellation.IsCancellationRequested)
            {
                KeyValuePair<string,long> receipt;
                lock(_gate)
                {
                    if(_pending.Count == 0) { _running=false; return; }
                    receipt=_pending.First();
                }
                bool accepted=false;
                try { accepted=await send(receipt.Key,receipt.Value).ConfigureAwait(false); }
                catch(Exception ex) when(ex is IOException or OperationCanceledException or ObjectDisposedException or
                    Google.Protobuf.InvalidProtocolBufferException) { }
                if(accepted)
                {
                    lock(_gate)
                        if(_pending.GetValueOrDefault(receipt.Key) == receipt.Value) _pending.Remove(receipt.Key);
                }
                else await Task.Delay(retryDelay ?? TimeSpan.FromSeconds(1),cancellation).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested) { }
        finally { if(cancellation.IsCancellationRequested) lock(_gate) { _pending.Clear(); _running=false; } }
    }
}
