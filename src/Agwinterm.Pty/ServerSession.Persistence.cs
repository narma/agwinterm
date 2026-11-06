using Agwinterm.Core;

namespace Agwinterm.Pty;

public sealed partial class ServerSession
{
    private readonly SemaphoreSlim _bufferTransferGate=new(1);
    private readonly TaskCompletionSource _presentationReady=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[]? _lastBufferCheckpoint;
    public bool CanExportBuffer => UsesPresentation && _presentationReady.Task.IsCompletedSuccessfully;
    public async Task<byte[]> ExportBufferAsync(CancellationToken cancellation=default)
    {
        await _bufferTransferGate.WaitAsync(cancellation).ConfigureAwait(false);
        string token="";
        try
        {
            var begin=await BufferRequest("buffer-export",0,"",cancellation).ConfigureAwait(false);
            if(begin.Rows.Count != 2 || !int.TryParse(begin.Rows[1],out int length) || length is < 1 or > OwnerBufferTransfer.MaximumBytes)
                throw new InvalidDataException("Invalid owner checkpoint size.");
            token=begin.Rows[0]; var bytes=new byte[length];
            for(int offset=0;offset<length;)
            {
                var page=await BufferRequest("buffer-read",offset,token,cancellation).ConfigureAwait(false);
                if(page.Rows.Count != 1) throw new InvalidDataException("Missing checkpoint chunk.");
                byte[] chunk=Convert.FromBase64String(page.Rows[0]);
                if(chunk.Length != Math.Min(OwnerBufferTransfer.ChunkBytes,length-offset)) throw new InvalidDataException("Invalid checkpoint chunk.");
                chunk.CopyTo(bytes,offset); offset+=chunk.Length;
            }
            _lastBufferCheckpoint=bytes;
            return (byte[])bytes.Clone();
        }
        catch(Exception ex) when(HasExited && _lastBufferCheckpoint is not null && ex is IOException or OperationCanceledException)
        {
            // Retain the confirmed final checkpoint if the host subsequently
            // disappears. While attached, export again after reflow/notices.
            return (byte[])_lastBufferCheckpoint.Clone();
        }
        finally
        {
            try { if(token.Length > 0) await BufferRequest("buffer-release",0,token,CancellationToken.None).ConfigureAwait(false); }
            catch(IOException) { } catch(OperationCanceledException) { }
            _bufferTransferGate.Release();
        }
    }
    public async Task ImportBufferAsync(byte[] bytes,CancellationToken cancellation=default)
    {
        if(bytes.Length is < 1 or > OwnerBufferTransfer.MaximumBytes || !BufferPersist.TryParse(bytes,out _))
            throw new InvalidDataException("Invalid saved terminal buffer.");
        await _bufferTransferGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var begin=await BufferRequest("buffer-import",bytes.Length,"",cancellation).ConfigureAwait(false);
            if(begin.Rows.Count != 1) throw new InvalidDataException("Missing checkpoint transfer identity.");
            string token=begin.Rows[0];
            for(int offset=0;offset<bytes.Length;offset+=OwnerBufferTransfer.ChunkBytes)
                await BufferRequest("buffer-write",offset,token+":"+Convert.ToBase64String(bytes,offset,
                    Math.Min(OwnerBufferTransfer.ChunkBytes,bytes.Length-offset)),cancellation).ConfigureAwait(false);
            await BufferRequest("buffer-commit",0,token,cancellation).ConfigureAwait(false);
        }
        finally { _bufferTransferGate.Release(); }
    }
    private async Task<TerminalQueryResult> BufferRequest(string operation,int offset,string text,CancellationToken cancellation)
    {
        await _presentationReady.Task.WaitAsync(TimeSpan.FromSeconds(15),cancellation).ConfigureAwait(false);
        TerminalQuery query;
        lock(_sync) query=new(_creationTicket,_attachmentEpoch,0,operation,StartRow:offset,Text:text);
        var reply=await QueryPresentationAsync(query,cancellation).ConfigureAwait(false);
        if(reply.Status != "ok") throw new IOException("Owner checkpoint failed: "+reply.Status);
        return reply;
    }
}
