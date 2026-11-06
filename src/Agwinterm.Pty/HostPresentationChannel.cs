using System.IO.Pipes;
using Agwinterm.Core;

namespace Agwinterm.Pty;

/// <summary>Independent frame and input pipes. Output only signals a bounded wakeup;
/// the publisher captures after any in-flight write, so stalled readers retain at most
/// one encoded record, a base viewport and the latest coalesced wakeup.</summary>
internal sealed class HostPresentationChannel
{
    private readonly TerminalSession _session;
    private readonly OwnerInputQueue _input;
    private readonly GhosttyFrameSource _frames;
    private readonly string _incarnation;
    private readonly ulong _epoch;
    private readonly object _captureGate;
    private ulong _viewportRevision;
    private int _scrollOffset, _cellWidth=8, _cellHeight=18;
    private bool _visible=true;
    private readonly CancellationTokenSource _cancel = new();
    private readonly SemaphoreSlim _changed = new(0,1);
    private readonly SemaphoreSlim _credit = new(1,1);
    private ulong _sentSequence, _acknowledgedSequence;
    private readonly NamedPipeServerStream _outputPipe, _inputPipe;
    private readonly object _signalGate = new();
    private readonly object _acceptanceGate = new();
    private readonly Timer _animation;
    private readonly HostEffectChannel _effects;
    private Task _effectWriter=Task.CompletedTask;
    private bool _finished;
    public string FramePipeName { get; }
    public string InputPipeName { get; }
    public string QueryPipeName { get; }
    public string EffectPipeName => _effects.PipeName;
    public void PublishEffect(Proto.PresentationEffect effect) => _effects.Publish(effect);
    public Task Completion { get; private set; } = Task.CompletedTask;

    public HostPresentationChannel(string prefix, TerminalSession session, OwnerInputQueue input,
        string incarnation, ulong epoch, object captureGate)
    {
        _session=session; _input=input; _incarnation=incarnation; _epoch=epoch; _captureGate=captureGate;
        _frames=new((GhosttyTerminalCore)session.Emulator,session.SyncRoot,incarnation,epoch,captureGate);
        FramePipeName=prefix+"-f-"+Guid.NewGuid().ToString("N"); InputPipeName=prefix+"-i-"+Guid.NewGuid().ToString("N");
        QueryPipeName=prefix+"-q-"+Guid.NewGuid().ToString("N");
        _outputPipe=new(FramePipeName,PipeDirection.Out,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
        try { _inputPipe=new(InputPipeName,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous); }
        catch { _outputPipe.Dispose(); _frames.Dispose(); throw; }
        _animation=new Timer(_=>Signal(),null,Timeout.Infinite,Timeout.Infinite);
        _effects=new(prefix+"-e-"+Guid.NewGuid().ToString("N"),incarnation,epoch,Cancel);
    }
    public void Start() => Completion=Task.Run(RunAsync);
    public void Signal()
    {
        lock (_signalGate) if (!_finished && _changed.CurrentCount == 0) _changed.Release();
    }
    public void Cancel()
    {
        // Revocation and acceptance share a gate. Once replacement attach has
        // cancelled this channel, no late decoded input/resize can be accepted.
        lock(_acceptanceGate) lock (_signalGate) if (!_finished) _cancel.Cancel();
    }
    private async Task RunAsync()
    {
        _session.OutputReceived+=Signal; _session.Exited+=OnExit;
        Task queries=new HostQueryChannel(QueryPipeName,_session,_incarnation,_epoch,_frames).RunAsync(_cancel.Token);
        _effectWriter=_effects.RunAsync(_cancel.Token);
        try
        {
            using var connect=CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
            connect.CancelAfter(TimeSpan.FromSeconds(10));
            await Task.WhenAll(_outputPipe.WaitForConnectionAsync(connect.Token),_inputPipe.WaitForConnectionAsync(connect.Token)).ConfigureAwait(false);
            Signal();
            Task output=PublishAsync(), input=Task.Factory.StartNew(ReadInput,CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
            // An effect writer can finish normally only after the final output
            // publisher asks it to drain. Its failure revokes this attachment.
            _ = _effectWriter.ContinueWith(t=> { if(t.IsFaulted) { _ = t.Exception; Cancel(); } },TaskScheduler.Default);
            await Task.WhenAny(output,input,queries).ConfigureAwait(false);
            _cancel.Cancel();
            // Both operations finish before either pipe is disposed (overlapped IO lifetime).
            await Task.WhenAll(output,input).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        { if(ex is not OperationCanceledException) System.Diagnostics.Trace.WriteLine("Owner presentation ended: "+ex); }
        finally
        {
            _cancel.Cancel();
            try { await Task.WhenAll(queries,_effectWriter).ConfigureAwait(false); }
            catch(Exception ex) when(ex is IOException or OperationCanceledException) { }
            finally
            {
                _session.OutputReceived-=Signal; _session.Exited-=OnExit;
                lock (_signalGate) { _finished=true; _animation.Dispose(); _changed.Dispose(); _credit.Dispose(); _cancel.Dispose(); }
                _outputPipe.Dispose(); _inputPipe.Dispose(); _frames.Dispose();
            }
        }
    }
    private void OnExit(int code)
    {
        // A hidden or occluded client may hold the only viewport credit.
        // Notify it that the child has finished independently of that credit.
        _effects.Complete();
        Signal();
    }
    private async Task PublishAsync()
    {
        TerminalFrame.Lease? previous=null;
        try
        {
            while (true)
            {
                await _changed.WaitAsync(_cancel.Token).ConfigureAwait(false);
                await _credit.WaitAsync(_cancel.Token).ConfigureAwait(false);
                TerminalFrame.Lease? captured;
                lock(_captureGate)
                {
                    _cancel.Token.ThrowIfCancellationRequested();
                    captured=_visible || _session.HasExited ? _frames.Capture(_scrollOffset,_cellWidth,_cellHeight,_viewportRevision) : null;
                }
                using var frame=captured;
                long animation=frame?.AnimationDeadline ?? 0;
                _animation.Change(_visible && animation > 0 ? Math.Clamp(animation-Environment.TickCount64,1,int.MaxValue) : Timeout.Infinite,
                    Timeout.Infinite);
                if (frame is null || previous?.Identity == frame.Identity)
                {
                    _credit.Release();
                    long remaining=((GhosttyTerminalCore)_session.Emulator).SynchronizedOutputDeadline-Environment.TickCount64;
                    if (remaining > 0)
                    {
                        // One deadline wakeup, not continuous polling of an idle terminal.
                        await _changed.WaitAsync(TimeSpan.FromMilliseconds(remaining),_cancel.Token).ConfigureAwait(false);
                        Signal();
                    }
                    continue;
                }
                bool exited=_session.HasExited;
                if (exited) { _effects.Complete(); await _effectWriter.ConfigureAwait(false); }
                byte[] record=PresentationFrameCodec.Encode(frame,previous,exited,_session.ExitCode ?? 0);
                lock(_signalGate) _sentSequence=frame.Identity.Sequence;
                await _outputPipe.WriteAsync(record,_cancel.Token).ConfigureAwait(false);
                TerminalLatencyTrace.Record(41,record.Length);
                previous?.Dispose();
                // A second lease of this exact immutable frame is kept as the next delta base.
                previous = frame.Retain();
                // Child exit closes typing, not the terminal view. Keep queries
                // and viewport updates alive until the attachment is released.
            }
        }
        finally { previous?.Dispose(); }
    }
    private void ReadInput()
    {
        using var pipeReader = new PresentationPipeIo(_inputPipe,_cancel.Token);
        var reader = new PresentationInputReader(_inputPipe,pipeReader.Read);
        ulong sequence=0;
        long latencyEnter=0;
        var inputs=new List<TerminalInput>(32);
        while (true)
        {
            inputs.Clear();
            do
            {
            var message=reader.Read(_incarnation,_epoch,sequence,_cancel.Token);
            sequence=message.Sequence;
            if (TerminalLatencyTrace.Enabled && message.Input is TerminalInput.Key { VirtualKey:13, Action: > 0 })
                TerminalLatencyTrace.Record(10,++latencyEnter);
            if(message.Input is TerminalInput.FramePresented presented)
            {
                lock(_signalGate)
                {
                    if(presented.Sequence != _sentSequence || presented.Sequence <= _acknowledgedSequence)
                        throw new InvalidDataException("Invalid presentation credit.");
                    _acknowledgedSequence=presented.Sequence;
                    _credit.Release();
                }
            }
            else if(message.Input is TerminalInput.Viewport viewport)
            {
                // Keep previously received input before this resize. The same
                // writer arbitrates replies; no PTY IO occurs under the feed lock.
                SubmitInputs(waitForWriter:true);
                inputs.Clear();
                lock(_acceptanceGate) lock(_captureGate)
                {
                    _cancel.Token.ThrowIfCancellationRequested();
                    if(viewport.Revision <= _viewportRevision) throw new InvalidDataException("Stale viewport revision.");
                    _viewportRevision=viewport.Revision; _scrollOffset=viewport.ScrollOffset;
                    _cellWidth=viewport.CellWidth; _cellHeight=viewport.CellHeight; _visible=viewport.Visible;
                    if(_session.Cols != viewport.Columns || _session.Rows != viewport.Rows)
                        _session.Resize(viewport.Columns,viewport.Rows);
                }
                Signal();
            }
            else inputs.Add(message.Input);
            // Drain only complete records already returned by the same pipe read.
            // One ordered PTY write can carry a burst; there is no batching timer.
            } while(inputs.Count < 32 && reader.HasBufferedRecord);
            if(inputs.Count == 0) continue;
            SubmitInputs();
        }
        void SubmitInputs(bool waitForWriter=false)
        {
            if(inputs.Count == 0 && _session.InputClosed) return;
            lock(_acceptanceGate)
            {
                _cancel.Token.ThrowIfCancellationRequested();
                foreach(var input in inputs)
                {
                    // A key already in the pipe may arrive just after child
                    // exit. It cannot reach a child and must not tear down the
                    // still-readable terminal view or its history queries.
                    // ConPTY closes input before the final-output settle period.
                    // A last key during that interval must not revoke the query
                    // pipe before the final frame can be delivered.
                    if(_session.InputClosed && input is not TerminalInput.Command) continue;
                    var accepted=_input.Submit(input);
                    if(accepted == TerminalInputAcceptance.Closed && _session.InputClosed && input is not TerminalInput.Command) continue;
                    if(accepted != TerminalInputAcceptance.Accepted) throw new IOException("Owner rejected input: "+accepted);
                }
            }
            var drained=_input.SubmitAndDrain([],waitForWriter);
            if(drained != TerminalInputAcceptance.Accepted) throw new IOException("Owner writer closed: "+drained);
        }
    }
}
