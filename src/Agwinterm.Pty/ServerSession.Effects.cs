using System.IO.Pipes;
using Agwinterm.Core;
using Agwinterm.Pty.Proto;

namespace Agwinterm.Pty;

public sealed partial class ServerSession
{
    private async Task ReadEffectsAsync()
    {
        try
        {
            using var pipe=new NamedPipeClientStream(".",_effectPipe,PipeDirection.In,PipeOptions.Asynchronous);
            using var connect=CancellationTokenSource.CreateLinkedTokenSource(_readCancel.Token);
            connect.CancelAfter(TimeSpan.FromSeconds(10));
            await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
            ulong sequence=0;
            while(true)
            {
                var effect=await PresentationQueryWire.ReadAsync(pipe,PresentationEffect.Parser,_readCancel.Token).ConfigureAwait(false);
                if(effect.Incarnation != _creationTicket || effect.AttachmentEpoch != _attachmentEpoch ||
                    effect.Sequence != ++sequence || effect.Kind > 9 || effect.Title.Length+effect.Text.Length > 512*1024)
                    throw new InvalidDataException("Invalid owner effect identity or bounds.");
                if(effect.Kind == 0)
                {
                    lock(_sync)
                    {
                        // Keep the view readable while refusing subsequent
                        // writes. Consume the outstanding frame even without a
                        // paint, so the final frame and exit code can follow.
                        _childInputClosed=true;
                        using var frame=((PresentedTerminalView)Emulator).AcquireFrame();
                        if(frame is not null) AcknowledgeFrame(frame.Identity.Sequence);
                    }
                    return;
                }
                lock(_sync)
                {
                    if(_disposed || _stopped) return;
                    var view=(PresentedTerminalView)Emulator;
                    int retainedBytes=(effect.Title.Length+effect.Text.Length)*2+128;
                    switch(effect.Kind)
                    {
                        case 1: view.DispatchHostEffect(a=>a.Notify(effect.Title,effect.Text),retainedBytes); break;
                        case 2: view.SetProgress(effect.State,effect.Value); break;
                        case 3: view.DispatchHostEffect(a=>a.ClipboardWrite(effect.Text),retainedBytes); break;
                        case 4: view.DispatchHostEffect(a=>a.Bell(),retainedBytes); break;
                        case 5: view.DispatchHostEffect(a=>a.CommandFinished(effect.Text,effect.CompletionSequence),retainedBytes); break;
                        case 6: view.PublishRoleNotice(effect.Text); break;
                        case 7: view.SetMetadata(effect.Title,effect.Text); break;
                        case 8:
                            var report = System.Text.Json.JsonSerializer.Deserialize<ProgramStatus>(effect.Text)
                                ?? throw new InvalidDataException("Missing program status report.");
                            view.DispatchHostEffect(a=>a.ProgramStatusChanged(report),retainedBytes); break;
                        case 9: view.DispatchHostEffect(a=>a.ProgramStatusCommandFinished(),retainedBytes); break;
                    }
                }
                if(effect.Kind == 7) OutputReceived?.Invoke();
            }
        }
        catch(Exception ex) when(ex is IOException or OperationCanceledException or Google.Protobuf.InvalidProtocolBufferException)
        {
            // A missing reliable effect is an explicit attachment failure, never
            // a reason to replay an uncertain clipboard or notification action.
            MarkInputClosed(); CancelRead();
            throw;
        }
    }
}
