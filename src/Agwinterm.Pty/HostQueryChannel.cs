using System.IO.Pipes;
using Agwinterm.Core;
using Agwinterm.Pty.Proto;
using Google.Protobuf;

namespace Agwinterm.Pty;

/// <summary>One bounded query at a time per attachment. A short-lived connection
/// makes cancellation local to the query, without disturbing input or frames.</summary>
internal sealed class HostQueryChannel(string name, TerminalSession session, string incarnation, ulong epoch,
    GhosttyFrameSource frames, Action<string,long>? programStatusReceived = null)
{
    public string PipeName => name;
    public async Task RunAsync(CancellationToken cancellation)
    {
        using var owner = new OwnerQueries(session,incarnation,epoch);
        using var images = new ControlServer(session);
        var buffers = new OwnerBufferTransfer(session);
        while (!cancellation.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                var request = await PresentationQueryWire.ReadAsync(pipe,PresentationQuery.Parser,deadline.Token).ConfigureAwait(false);
                // After the request the client sends no more bytes. EOF means it
                // abandoned this query; cancel native search before serving another.
                var disconnected = WatchDisconnect(pipe,deadline);
                try
                {
                    if(request.Operation == "program-status-receipt")
                    {
                        var receipt = new PresentationQueryReply { Incarnation=incarnation,AttachmentEpoch=epoch,
                            Generation=request.Generation,Status="stale" };
                        if(request.Incarnation == incarnation && request.AttachmentEpoch == epoch &&
                            request.Generation > 0 && request.Text.Length <= 256 && programStatusReceived is not null)
                        {
                            deadline.Token.ThrowIfCancellationRequested();
                            programStatusReceived(request.Text,request.Generation);
                            receipt.Status="ok";
                        }
                        await PresentationQueryWire.WriteAsync(pipe,receipt,deadline.Token).ConfigureAwait(false);
                        continue;
                    }
                    if(request.Operation.StartsWith("buffer-",StringComparison.Ordinal))
                    {
                        var bufferReply=new PresentationQueryReply { Incarnation=incarnation,AttachmentEpoch=epoch,Status="stale" };
                        if(request.Incarnation == incarnation && request.AttachmentEpoch == epoch)
                            await Task.Run(()=>buffers.Execute(request,bufferReply),deadline.Token).ConfigureAwait(false);
                        await PresentationQueryWire.WriteAsync(pipe,bufferReply,deadline.Token).ConfigureAwait(false);
                        continue;
                    }
                    if(request.Operation == "control-image")
                    {
                        var imageReply=new PresentationQueryReply { Incarnation=incarnation,AttachmentEpoch=epoch,Status="stale" };
                        if(request.Incarnation == incarnation && request.AttachmentEpoch == epoch && request.Text.Length <= 1024*1024)
                        {
                            deadline.Token.ThrowIfCancellationRequested();
                            string imageResult=await Task.Run(()=>OwnerImageCommands.Dispatch(images,request.Text),deadline.Token).ConfigureAwait(false);
                            imageReply.Status="ok"; imageReply.Rows.Add(imageResult);
                        }
                        await PresentationQueryWire.WriteAsync(pipe,imageReply,deadline.Token).ConfigureAwait(false);
                        continue;
                    }
                    if (request.Operation == "image")
                    {
                        using var frame=frames.AcquireCached();
                        var image=frame?.Images.FirstOrDefault(p=>p.Image.Generation == request.ImageGeneration)?.Image;
                        var imageReply=new PresentationQueryReply { Incarnation=incarnation,AttachmentEpoch=epoch,
                            Generation=frame?.Identity.TerminalGeneration ?? 0,Status="stale" };
                        if (request.Incarnation == incarnation && request.AttachmentEpoch == epoch &&
                            frame?.Identity.Sequence == request.FrameSequence && image is not null &&
                            request.ImageOffset >= 0 && request.ImageOffset < image.Pixels.Length)
                        {
                            imageReply.Status="ok";
                            imageReply.ImageBytes=ByteString.CopyFrom(image.Pixels.Span.Slice(request.ImageOffset,
                                Math.Min(1024*1024,image.Pixels.Length-request.ImageOffset)));
                        }
                        await PresentationQueryWire.WriteAsync(pipe,imageReply,deadline.Token).ConfigureAwait(false);
                        continue;
                    }
                    var result = await owner.ReadAsync(new(request.Incarnation,request.AttachmentEpoch,request.Generation,
                        request.Operation,request.StartRow,request.RowCount,request.Text,request.ResultLimit),deadline.Token).ConfigureAwait(false);
                    var reply = new PresentationQueryReply { Incarnation=result.Incarnation,AttachmentEpoch=result.AttachmentEpoch,
                        Generation=result.Generation,Status=result.Status,Truncated=result.Truncated };
                    reply.Rows.AddRange(result.Rows);
                    if (result.Matches is { } matches) reply.Matches.AddRange(matches.Select(m => new PresentationSearchMatch
                        { Line=m.Line,Column=m.Column,EndLine=m.EndLine,EndColumn=m.EndColumn }));
                    await PresentationQueryWire.WriteAsync(pipe,reply,deadline.Token).ConfigureAwait(false);
                }
                finally
                {
                    // Drain the disconnect read before disposing its overlapped handle.
                    deadline.Cancel();
                    await disconnected.ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidProtocolBufferException or System.Text.Json.JsonException) { }
        }
    }
    private static async Task WatchDisconnect(Stream pipe,CancellationTokenSource cancellation)
    {
        try { await pipe.ReadAsync(new byte[1],cancellation.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        finally { cancellation.Cancel(); }
    }
}
