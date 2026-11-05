using System.Buffers.Binary;
using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Pty;

public sealed partial class ServerSession
{
    private long _journalApplied, _journalEventSequence, _journalReplayThrough;
    private int _journalResponseIndex;
    private bool _journalReplaying=true;
    private string _journalCheckpointPipe="";
    private long _journalBytesSinceCheckpoint;
    private int _journalResponsesSinceCheckpoint;
    private Task<bool>? _journalCheckpointTask;
    private readonly bool _journalAutoCheckpoint=Environment.GetEnvironmentVariable("AGWINTERM_JOURNAL_CHECKPOINTS") != "0";
    public Task<bool> CheckpointJournalAsync()
    {
        if(!UsesJournal) throw new InvalidOperationException("Not a journal owner.");
        lock(_sync)
        {
            if(_journalCheckpointTask is { IsCompleted:false }) return _journalCheckpointTask;
            return _journalCheckpointTask=Task.Run(CaptureAndUploadJournalCheckpointAsync);
        }
    }
    private async Task<bool> CaptureAndUploadJournalCheckpointAsync()
    {
        try
        {
            byte[] snapshot; long sequence,cut;
            long waiting=System.Diagnostics.Stopwatch.GetTimestamp();
            while(true)
            {
            lock(_sync)
            {
                if(_disposed || InputClosed || _journalReplaying || !_started) return false;
                if(((GhosttyTerminalCore)Emulator).SnapshotCaptureAvailable)
                {
                long start=System.Diagnostics.Stopwatch.GetTimestamp();
                TerminalLatencyTrace.Record(50,Math.Max(1,start-waiting));
                snapshot=((GhosttyTerminalCore)Emulator).CaptureSnapshot();
                sequence=_journalApplied;
                cut=checked((long)_presentationInput!.SubmittedSequence);
                TerminalLatencyTrace.Record(51,Math.Max(1,System.Diagnostics.Stopwatch.GetTimestamp()-start));
                TerminalLatencyTrace.Record(52,snapshot.Length);
                _journalBytesSinceCheckpoint=0;
                _journalResponsesSinceCheckpoint=0;
                break;
                }
            }
            await Task.Delay(10,_readCancel.Token).ConfigureAwait(false);
            }
            long upload=System.Diagnostics.Stopwatch.GetTimestamp();
            using var pipe=new System.IO.Pipes.NamedPipeClientStream(".",_journalCheckpointPipe,System.IO.Pipes.PipeDirection.InOut,System.IO.Pipes.PipeOptions.Asynchronous);
            await pipe.ConnectAsync(10000,_readCancel.Token).ConfigureAwait(false);
            byte[] cutBytes=new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(cutBytes,cut);
            await pipe.WriteAsync(cutBytes,_readCancel.Token).ConfigureAwait(false);
            await pipe.WriteAsync(JournalWire.Encode(new(JournalMessageKind.Checkpoint,_attachmentEpoch,sequence,snapshot)),_readCancel.Token).ConfigureAwait(false);
            byte[] ack=new byte[1]; await pipe.ReadExactlyAsync(ack,_readCancel.Token).ConfigureAwait(false);
            if(ack[0] != 1) throw new InvalidDataException("Checkpoint publication refused.");
            await pipe.WriteAsync(new byte[]{1},_readCancel.Token).ConfigureAwait(false);
            TerminalLatencyTrace.Record(53,Math.Max(1,System.Diagnostics.Stopwatch.GetTimestamp()-upload));
            return true;
        }
        catch(Exception ex) when(ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or TimeoutException)
        { System.Diagnostics.Debug.WriteLine("Journal checkpoint: "+ex.Message); return false; }
    }
    public void SetJournalHost(IHostActions host)
    {
        lock(_sync)
        {
            if(Emulator.Host is not JournalOwnerActions actions) throw new InvalidOperationException("Not a journal owner.");
            actions.Target=host;
            ((GhosttyTerminalCore)Emulator).SyncTheme();
        }
    }
    public TerminalInputAcceptance SubmitJournalInput(ReadOnlySpan<byte> bytes,bool persistentUiThread=false)
        => bytes.Length == 0 ? TerminalInputAcceptance.Accepted : SubmitInput(new TerminalInput.Raw(bytes.ToArray()),persistentUiThread);
    private byte[] EncodeJournalInput(TerminalInput input,ulong sequence)
    {
        if(input is TerminalInput.JournalInject inject)
            return JournalWire.Encode(new(JournalMessageKind.InjectRequest,_attachmentEpoch,checked((long)sequence),inject.Bytes));
        if(input is TerminalInput.JournalResize resize)
        {
            byte[] payload=new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(payload,resize.Columns);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4),resize.Rows);
            return JournalWire.Encode(new(JournalMessageKind.ResizeRequest,_attachmentEpoch,checked((long)sequence),payload));
        }
        if(input is TerminalInput.Raw raw)
            return JournalWire.Encode(new(JournalMessageKind.Input,_attachmentEpoch,checked((long)sequence),raw.Bytes));
        if(input is TerminalInput.JournalResponse response)
        {
            if(response.Bytes.Length > 1024*1024-12) throw new InvalidDataException("Oversized terminal response.");
            byte[] payload=new byte[12+response.Bytes.Length];
            BinaryPrimitives.WriteInt64LittleEndian(payload,response.EventSequence);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8),response.Index);
            response.Bytes.Span.CopyTo(payload.AsSpan(12));
            return JournalWire.Encode(new(JournalMessageKind.Response,_attachmentEpoch,checked((long)sequence),payload));
        }
        throw new InvalidDataException("Journal input must already be encoded by its owner.");
    }
    private void ReadJournalLoop()
    {
        var data=_data!;
        var pipeReader=data is System.IO.Pipes.PipeStream pipe ? new PresentationPipeIo(pipe,_readCancel.Token) : null;
        var reader=new JournalReader(data,pipeReader is null ? null : pipeReader.Read);
        try
        {
            var checkpoint=reader.Read(_attachmentEpoch,_readCancel.Token);
            if(checkpoint.Kind != JournalMessageKind.Checkpoint) throw new InvalidDataException("Journal stream must start with a checkpoint.");
            lock(_sync)
            {
                if(_disposed) return;
                if(!checkpoint.Payload.IsEmpty) ((GhosttyTerminalCore)Emulator).RestoreSnapshot(checkpoint.Payload.Span);
                _journalApplied=checkpoint.Sequence;
            }
            var barrier=reader.Read(_attachmentEpoch,_readCancel.Token);
            if(barrier.Kind != JournalMessageKind.Live || barrier.Sequence < _journalApplied) throw new InvalidDataException("Invalid journal replay barrier.");
            _journalReplayThrough=barrier.Sequence;
            _journalReplaying=_journalApplied < _journalReplayThrough;
            if(!_journalReplaying) OutputReceived?.Invoke();
            while(true)
            {
                var message=reader.Read(_attachmentEpoch,_readCancel.Token);
                if(message.Kind == JournalMessageKind.Exit)
                {
                    if(message.Sequence != _journalApplied) throw new InvalidDataException("Exit skipped journal output.");
                    ExitCode=BinaryPrimitives.ReadInt32LittleEndian(message.Payload.Span); HasExited=true;
                    OutputReceived?.Invoke(); Exited?.Invoke(ExitCode.Value); break;
                }
                if(message.Sequence != _journalApplied+1) throw new InvalidDataException("Nonconsecutive journal output.");
                lock(_sync)
                {
                    if(_disposed) break;
                    _journalEventSequence=message.Sequence; _journalResponseIndex=0;
                    if(message.Kind == JournalMessageKind.Output)
                    {
                        Emulator.Feed(message.Payload.Span);
                        _latencyReceive?.Output(message.Payload.Span,5);
                    }
                    else if(message.Kind == JournalMessageKind.Resize)
                    {
                        int cols=BinaryPrimitives.ReadInt32LittleEndian(message.Payload.Span),rows=BinaryPrimitives.ReadInt32LittleEndian(message.Payload.Span[4..]);
                        if(cols is < 1 or > 10000 || rows is < 1 or > 10000 || (long)cols*rows > TerminalFrame.MaximumCells)
                            throw new InvalidDataException("Invalid journal dimensions.");
                        Emulator.Resize(cols,rows);
                    }
                    else throw new InvalidDataException("Unexpected journal event.");
                    _journalApplied=message.Sequence;
                    _journalReplaying=_journalApplied < _journalReplayThrough;
                    _journalBytesSinceCheckpoint+=message.Payload.Length+32;
                    if(_journalAutoCheckpoint && !_journalReplaying && (_journalBytesSinceCheckpoint >= 8*1024*1024 || _journalResponsesSinceCheckpoint >= 8192) &&
                        _journalCheckpointTask is not { IsCompleted:false }) _=CheckpointJournalAsync();
                }
                if(!_journalReplaying) OutputReceived?.Invoke();
            }
        }
        catch(Exception ex) when(ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
        finally { pipeReader?.Dispose(); MarkInputClosed(); _presentationInput?.Dispose(); data.Dispose(); }
        OnStreamEnded();
    }
    private sealed class JournalOwnerActions(ServerSession session) : IHostActions
    {
        public IHostActions? Target;
        public (Color Foreground,Color Background)? DefaultColors => Target?.DefaultColors;
        public IReadOnlyList<Color>? DefaultPalette => Target?.DefaultPalette;
        public Color? DefaultCursorColor => Target?.DefaultCursorColor;
        public void Respond(string reply)
        {
            if(session._journalEventSequence <= 0) return;
            session._journalResponsesSinceCheckpoint++;
            session.SubmitInput(new TerminalInput.JournalResponse(session._journalEventSequence,session._journalResponseIndex++,Encoding.UTF8.GetBytes(reply)));
        }
        public void Notify(string title,string body) { if(!session._journalReplaying) Target?.Notify(title,body); }
        public void Progress(int state,int value) { if(!session._journalReplaying) Target?.Progress(state,value); }
        public void ClipboardWrite(string text) { if(!session._journalReplaying) Target?.ClipboardWrite(text); }
        public void Bell() { if(!session._journalReplaying) Target?.Bell(); }
        public void CommandFinished(string token,long sequence) => Target?.CommandFinished(token,sequence);
        public void Unhandled(string kind,string detail) { if(!session._journalReplaying) Target?.Unhandled(kind,detail); }
    }
}
