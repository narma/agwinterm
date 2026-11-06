using System.IO.Pipes;
using Agwinterm.Core;
using Agwinterm.Pty.Proto;

namespace Agwinterm.Pty;

public sealed partial class ServerSession
{
    // Receiver-owned cache: only generations referenced by the current complete
    // viewport survive. Renderers retain immutable frame leases independently.
    private Dictionary<ulong,GhosttyImage> _presentationImages = new();
    private async Task<TerminalFrame> ResolveFrameImagesAsync(TerminalFrame source,CancellationToken cancellation)
    {
        using var frame=source.Acquire();
        var cache=new Dictionary<ulong,GhosttyImage>();
        var placements=new List<GhosttyImagePlacement>(frame.Images.Count);
        long bytes=0;
        foreach (var placement in frame.Images)
        {
            var image=placement.Image;
            if (!cache.TryGetValue(image.Generation,out var ready))
            {
                int channels=image.Format switch { GhosttyPixelFormat.Rgb=>3,GhosttyPixelFormat.Rgba or GhosttyPixelFormat.Bgra=>4,
                    GhosttyPixelFormat.GrayAlpha=>2,GhosttyPixelFormat.Gray=>1,GhosttyPixelFormat.Png=>0,_=>throw new InvalidDataException("Invalid image format.") };
                int length=channels == 0 ? image.PayloadLength : checked(image.Width*image.Height*channels);
                bytes+=length;
                if (bytes > 256L*1024*1024) throw new InvalidDataException("Visible image budget exceeded.");
                if (_presentationImages.TryGetValue(image.Generation,out ready))
                {
                    if (ready.Width != image.Width || ready.Height != image.Height || ready.Format != image.Format || ready.Pixels.Length != length)
                        throw new InvalidDataException("Image generation changed content metadata.");
                }
                else
                {
                    byte[] pixels=new byte[length];
                    await _queryGate.WaitAsync(cancellation).ConfigureAwait(false);
                    try
                    {
                        for (int offset=0;offset<length;)
                        {
                            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                            deadline.CancelAfter(TimeSpan.FromSeconds(10));
                            using var pipe=new NamedPipeClientStream(".",_queryPipe,PipeDirection.InOut,PipeOptions.Asynchronous);
                            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
                            await PresentationQueryWire.WriteAsync(pipe,new PresentationQuery {
                                Incarnation=frame.Identity.Incarnation,AttachmentEpoch=frame.Identity.AttachmentEpoch,
                                FrameSequence=frame.Identity.Sequence,Operation="image",ImageGeneration=image.Generation,ImageOffset=offset
                            },deadline.Token).ConfigureAwait(false);
                            var reply=await PresentationQueryWire.ReadAsync(pipe,PresentationQueryReply.Parser,deadline.Token).ConfigureAwait(false);
                            if (reply.Status != "ok" || reply.Incarnation != frame.Identity.Incarnation ||
                                reply.AttachmentEpoch != frame.Identity.AttachmentEpoch || reply.Generation != frame.Identity.TerminalGeneration ||
                                reply.ImageBytes.Length != Math.Min(1024*1024,length-offset))
                                throw new InvalidDataException("Stale or incomplete image payload.");
                            reply.ImageBytes.Span.CopyTo(pixels.AsSpan(offset)); offset+=reply.ImageBytes.Length;
                        }
                    }
                    finally { _queryGate.Release(); }
                    ready=image with { Pixels=pixels };
                }
                cache.Add(image.Generation,ready);
            }
            placements.Add(placement with { Image=ready });
        }
        _presentationImages=cache;
        var cells=new Cell[checked(frame.State.Columns*frame.State.Rows)]; frame.CopyCells(cells);
        return new(frame.Identity,frame.State,cells,frame.CapturedTicks,images:placements,
            animationDeadline:frame.AnimationDeadline,
            wrapLengths:Enumerable.Range(0,frame.State.Rows).Select(frame.WrapLength).ToArray(),marks:frame.Marks);
    }
}
