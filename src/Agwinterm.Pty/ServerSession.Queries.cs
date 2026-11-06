using System.IO.Pipes;
using Agwinterm.Pty.Proto;

namespace Agwinterm.Pty;

public sealed partial class ServerSession
{
    internal string ForwardImageCommand(string command,System.Text.Json.JsonElement arguments)
    {
        string request=System.Text.Json.JsonSerializer.Serialize(new { cmd=command,args=arguments });
        if(request.Length > 1024*1024) throw new InvalidDataException("Image request metadata exceeds the owner limit.");
        TerminalQuery query;
        lock(_sync) query=new(_creationTicket,_attachmentEpoch,0,"control-image",Text:request);
        var reply=QueryPresentationAsync(query,default).AsTask().GetAwaiter().GetResult();
        if(reply.Status != "ok" || reply.Rows.Count != 1) throw new IOException("Owner image command failed: "+reply.Status);
        return reply.Rows[0];
    }
    private readonly SemaphoreSlim _queryGate = new(1);
    internal async ValueTask<TerminalQueryResult> QueryPresentationAsync(TerminalQuery query,CancellationToken cancellation)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation,_readCancel.Token);
        lifetime.CancelAfter(TimeSpan.FromSeconds(10));
        await _queryGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            string name;
            lock (_sync)
            {
                if (_disposed) return new(query.Incarnation,query.AttachmentEpoch,query.Generation,"closed",[]);
                if (query.Incarnation != _creationTicket || query.AttachmentEpoch != _attachmentEpoch)
                    return new(query.Incarnation,query.AttachmentEpoch,query.Generation,"stale",[]);
                name=_queryPipe;
            }
            if (name.Length == 0) return new(query.Incarnation,query.AttachmentEpoch,query.Generation,"unsupported",[]);
            using var pipe = new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
            await pipe.ConnectAsync(lifetime.Token).ConfigureAwait(false);
            await PresentationQueryWire.WriteAsync(pipe,new PresentationQuery { Incarnation=query.Incarnation,
                AttachmentEpoch=query.AttachmentEpoch,Generation=query.Generation,Operation=query.Operation,
                StartRow=query.StartRow,RowCount=query.RowCount,Text=query.Text,ResultLimit=query.ResultLimit },lifetime.Token).ConfigureAwait(false);
            var reply = await PresentationQueryWire.ReadAsync(pipe,PresentationQueryReply.Parser,lifetime.Token).ConfigureAwait(false);
            lifetime.Token.ThrowIfCancellationRequested();
            if (reply.Incarnation != query.Incarnation || reply.AttachmentEpoch != query.AttachmentEpoch ||
                reply.Generation < 0 || reply.Rows.Count > 32 || reply.Matches.Count > 100000)
                throw new InvalidDataException("Owner query reply identity or bounds mismatch.");
            return new(reply.Incarnation,reply.AttachmentEpoch,reply.Generation,reply.Status,reply.Rows.ToArray(),reply.Truncated,
                reply.Matches.Select(m => new TerminalSearchMatch(m.Line,m.Column,m.EndLine,m.EndColumn)).ToArray());
        }
        finally { _queryGate.Release(); }
    }
}
