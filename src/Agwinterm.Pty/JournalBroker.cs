using System.Buffers.Binary;
using System.IO.Pipes;

namespace Agwinterm.Pty;

/// <summary>PTY transport and journal only. No terminal parser or snapshot decoder.</summary>
internal sealed class JournalBroker : IDisposable
{
    private readonly TerminalSession _session;
    private readonly OwnerInputQueue _writer;
    private readonly object _gate=new();
    private readonly JournalInputFence _fence=new();
    private readonly CancellationTokenSource _lifetime=new();
    private Connection? _connection;
    private bool _disposed;
    public TerminalJournal Journal { get; }
    public int InitialColumns { get; }
    public int InitialRows { get; }
    public bool Attached { get { lock(_gate) return _connection is not null; } }
    public JournalBroker(TerminalSession session,string incarnation)
    {
        _session=session; InitialColumns=session.Cols; InitialRows=session.Rows;
        Journal=new(incarnation); _writer=new(session,ApplyJournalOperation);
        session.RawOutput+=OnOutput; session.Exited+=OnExit;
    }
    private void OnOutput(byte[] bytes) => Journal.AppendOutput(bytes,_lifetime.Token);
    private void OnExit(int code) { lock(_gate) { if(_disposed) return; Journal.Complete(); _writer.Dispose(); } }
    public (string Output,string Input,ulong Epoch) Attach(string prefix)
    {
        lock(_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed,this);
            _connection?.Cancel.Cancel();
            ulong epoch=_fence.Attach();
            var connection=new Connection(prefix,epoch);
            _connection=connection;
            connection.Completion=Task.Run(()=>RunAsync(connection));
            return (connection.OutputName,connection.InputName,epoch);
        }
    }
    public void Detach()
    { lock(_gate) { _fence.Revoke(); _connection?.Cancel.Cancel(); _connection=null; } }
    private async Task RunAsync(Connection connection)
    {
        try
        {
            using var connect=CancellationTokenSource.CreateLinkedTokenSource(connection.Cancel.Token);
            connect.CancelAfter(TimeSpan.FromSeconds(10));
            await Task.WhenAll(connection.Output.WaitForConnectionAsync(connect.Token),connection.Input.WaitForConnectionAsync(connect.Token)).ConfigureAwait(false);
            Task output=PublishAsync(connection), input=ReadInputAsync(connection),checkpoint=ReadCheckpointsAsync(connection);
            await Task.WhenAny(output,input,checkpoint).ConfigureAwait(false);
            connection.Cancel.Cancel();
            await Task.WhenAll(output,input,checkpoint).ConfigureAwait(false);
        }
        catch(Exception ex) when(ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
        finally
        {
            lock(_gate) if(ReferenceEquals(_connection,connection)) _connection=null;
            connection.Output.Dispose(); connection.Input.Dispose(); connection.Checkpoint.Dispose();
            // Cancellation is also invoked by replacement under _gate.
            lock(_gate) connection.Cancel.Dispose();
        }
    }
    private async Task PublishAsync(Connection connection)
    {
        var checkpoint=Journal.Checkpoint;
        long barrier=Journal.LastSequence, sequence=checkpoint.Sequence;
        await Send(JournalMessageKind.Checkpoint,sequence,checkpoint.Snapshot).ConfigureAwait(false);
        await Send(JournalMessageKind.Live,barrier,ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
        var batch=new System.Buffers.ArrayBufferWriter<byte>(64*1024);
        while(true)
        {
            var item=await Journal.ReadNextAsync(sequence,connection.Cancel.Token).ConfigureAwait(false);
            if(item is null)
            {
                byte[] exit=new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(exit,_session.ExitCode ?? 0);
                await Send(JournalMessageKind.Exit,sequence,exit).ConfigureAwait(false); return;
            }
            batch.Clear(); int count=0;
            do
            {
                JournalWire.Append(batch,new(item!.Kind == JournalEventKind.Output ? JournalMessageKind.Output : JournalMessageKind.Resize,
                    connection.Epoch,item.Sequence,item.Payload));
                sequence=item.Sequence; count++;
            } while(count < 128 && batch.WrittenCount < 64*1024 && Journal.TryReadNext(sequence,out item));
            // Batch only events already retained. Quiet interactive output is sent
            // immediately; no timer, credit or acknowledgement delays its first byte.
            await connection.Output.WriteAsync(batch.WrittenMemory,connection.Cancel.Token).ConfigureAwait(false);
            Agwinterm.Core.TerminalLatencyTrace.Record(23,batch.WrittenCount);
        }
        async ValueTask Send(JournalMessageKind kind,long position,ReadOnlyMemory<byte> bytes)
        {
            var record=JournalWire.Encode(new(kind,connection.Epoch,position,bytes));
            await connection.Output.WriteAsync(record,connection.Cancel.Token).ConfigureAwait(false);
            Agwinterm.Core.TerminalLatencyTrace.Record(23,record.Length);
        }
    }
    private async Task ReadInputAsync(Connection connection)
    {
        using var buffered=new BufferedStream(connection.Input,64*1024);
        while(true)
        {
            var message=await JournalWire.ReadAsync(buffered,connection.Epoch,connection.Cancel.Token).ConfigureAwait(false);
            TerminalInput? operation=message.Kind == JournalMessageKind.InjectRequest ? new TerminalInput.JournalInject(message.Payload) : null;
            if(message.Kind == JournalMessageKind.ResizeRequest)
            {
                int cols=BinaryPrimitives.ReadInt32LittleEndian(message.Payload.Span),rows=BinaryPrimitives.ReadInt32LittleEndian(message.Payload.Span[4..]);
                if(cols is < 1 or > 10000 || rows is < 1 or > 10000 || (long)cols*rows > Agwinterm.Core.TerminalFrame.MaximumCells)
                    throw new InvalidDataException("Invalid journal dimensions.");
                operation=new TerminalInput.JournalResize(cols,rows);
            }
            lock(_gate)
            {
                if(!ReferenceEquals(connection,_connection)) throw new IOException("Journal owner replaced.");
                _fence.AcceptInput(message.Epoch,message.Sequence);
                connection.SignalInput();
                ReadOnlyMemory<byte> bytes=message.Payload;
                if(message.Kind == JournalMessageKind.Response)
                {
                    long source=BinaryPrimitives.ReadInt64LittleEndian(bytes.Span);
                    int index=BinaryPrimitives.ReadInt32LittleEndian(bytes.Span[8..]);
                    if(!_fence.AcceptResponse(message.Epoch,source,index,Journal.LastSequence)) continue;
                    bytes=bytes[12..];
                }
                else if(message.Kind != JournalMessageKind.Input && operation is null) throw new InvalidDataException("Unsupported journal input operation.");
                if(_writer.Submit(operation ?? new TerminalInput.Raw(bytes)) != TerminalInputAcceptance.Accepted)
                    throw new IOException("Journal PTY writer rejected input.");
            }
        }
    }
    private void ApplyJournalOperation(TerminalInput operation)
    {
        // The ordered worker may wait for retention/PTY locks. The input reader
        // remains free to accept the cut that allows a checkpoint to release them.
        if(operation is TerminalInput.JournalResize resize) Resize(resize.Columns,resize.Rows);
        else if(operation is TerminalInput.JournalInject inject)
            lock(_session.SyncRoot) Journal.AppendOutput(inject.Bytes.Span,_lifetime.Token);
        else throw new InvalidOperationException("Unknown journal operation.");
    }
    private async Task ReadCheckpointsAsync(Connection connection)
    {
        while(true)
        {
            await connection.Checkpoint.WaitForConnectionAsync(connection.Cancel.Token).ConfigureAwait(false);
            try
            {
                byte[] cutBytes=new byte[8];
                await connection.Checkpoint.ReadExactlyAsync(cutBytes,connection.Cancel.Token).ConfigureAwait(false);
                long cut=BinaryPrimitives.ReadInt64LittleEndian(cutBytes);
                var message=await JournalWire.ReadAsync(connection.Checkpoint,connection.Epoch,connection.Cancel.Token).ConfigureAwait(false);
                if(cut < 0 || message.Kind != JournalMessageKind.Checkpoint || message.Payload.IsEmpty)
                    throw new InvalidDataException("Invalid checkpoint upload.");
                while(true)
                {
                    Task inputChanged;
                    lock(_gate)
                    {
                        if(_disposed || !ReferenceEquals(connection,_connection)) throw new IOException("Journal owner replaced.");
                        if(_fence.InputSequence >= cut)
                        {
                            // Complete upload owns its bytes independently of the UI. Host-crash
                            // durability is outside this prototype; the surviving broker is the store.
                            long commit=System.Diagnostics.Stopwatch.GetTimestamp();
                            Journal.CommitStoredCheckpoint(Journal.Incarnation,new(message.Sequence,message.Payload));
                            _fence.Checkpoint(message.Sequence);
                            Agwinterm.Core.TerminalLatencyTrace.Record(54,Math.Max(1,System.Diagnostics.Stopwatch.GetTimestamp()-commit));
                            Agwinterm.Core.TerminalLatencyTrace.Record(55,Journal.PeakRetainedBytes);
                            break;
                        }
                        inputChanged=connection.InputChanged.Task;
                    }
                    await inputChanged.WaitAsync(connection.Cancel.Token).ConfigureAwait(false);
                }
                await connection.Checkpoint.WriteAsync(new byte[]{1},connection.Cancel.Token).ConfigureAwait(false);
                byte[] receipt=new byte[1];
                await connection.Checkpoint.ReadExactlyAsync(receipt,connection.Cancel.Token).ConfigureAwait(false);
                if(receipt[0] != 1) throw new InvalidDataException("Invalid checkpoint receipt.");
            }
            finally { connection.Checkpoint.Disconnect(); }
        }
    }
    public void Resize(int columns,int rows)
    {
        lock(_session.SyncRoot)
        {
            Journal.AppendResize(columns,rows,_lifetime.Token);
            _session.Resize(columns,rows);
        }
    }
    public void Dispose()
    {
        lock(_gate)
        {
            if(_disposed) return;
            _disposed=true; _lifetime.Cancel(); _connection?.Cancel.Cancel(); _connection=null;
            Journal.Dispose(); _writer.Dispose();
        }
        _session.RawOutput-=OnOutput; _session.Exited-=OnExit;
    }
    private sealed class Connection
    {
        public readonly ulong Epoch;
        public readonly string OutputName,InputName;
        public readonly NamedPipeServerStream Output,Input,Checkpoint;
        public TaskCompletionSource InputChanged=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void SignalInput() { var prior=InputChanged; InputChanged=new(TaskCreationOptions.RunContinuationsAsynchronously); prior.TrySetResult(); }
        public readonly CancellationTokenSource Cancel=new();
        public Task Completion=Task.CompletedTask;
        public Connection(string prefix,ulong epoch)
        {
            Epoch=epoch; OutputName=prefix+"-jo-"+Guid.NewGuid().ToString("N"); InputName=prefix+"-ji-"+Guid.NewGuid().ToString("N");
            Output=new(OutputName,PipeDirection.Out,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
            try
            {
                Input=new(InputName,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
                try { Checkpoint=new(InputName+"-checkpoint",PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous); }
                catch { Input.Dispose(); throw; }
            }
            catch { Output.Dispose(); Cancel.Dispose(); throw; }
        }
    }
}
