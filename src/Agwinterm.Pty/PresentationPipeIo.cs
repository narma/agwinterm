using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Agwinterm.Pty;

/// <summary>One dedicated reader or writer, with completion signalled directly to its thread.
/// The event low bit suppresses IOCP notification for this individual operation:
/// the managed pipe's completion callback must never see our OVERLAPPED pointer.
/// Cancellation drains the operation before returning or releasing pinned memory.</summary>
internal sealed unsafe class PresentationPipeIo : IDisposable
{
    private readonly PipeStream _pipe;
    private readonly EventWaitHandle _completed = new(false,EventResetMode.ManualReset);
    private readonly NativeOverlapped* _operation;
    private readonly CancellationToken _cancellation;
    private readonly CancellationTokenRegistration _registration;
    private System.Buffers.MemoryHandle _writePin;
    private int _writeLength;
    private bool _writePending;

    public PresentationPipeIo(PipeStream pipe,CancellationToken cancellation)
    {
        _pipe=pipe; _cancellation=cancellation;
        _operation=(NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
        _operation->EventHandle=_completed.SafeWaitHandle.DangerousGetHandle() | 1;
        _registration=cancellation.UnsafeRegister(static state=>((PresentationPipeIo)state!).Cancel(),this);
    }
    private void Cancel() => CancelIoEx(_pipe.SafePipeHandle.DangerousGetHandle(),_operation);

    public int Read(Memory<byte> buffer)
    {
        using var pin=buffer.Pin();
        return ReadPinned(pin.Pointer,buffer.Length);
    }
    public void Write(ReadOnlyMemory<byte> buffer)
    {
        if(!TryWrite(buffer)) CompleteWrite();
    }
    // Never waits. A pending operation owns its pin until CompleteWrite is called
    // by the dedicated writer, even if cancellation races the initial submission.
    public bool TryWrite(ReadOnlyMemory<byte> buffer)
    {
        if(_writePending) throw new InvalidOperationException("Input write already pending.");
        _cancellation.ThrowIfCancellationRequested(); _completed.Reset();
        nint handle=_pipe.SafePipeHandle.DangerousGetHandle();
        _writePin=buffer.Pin(); _writeLength=buffer.Length;
        bool immediate=WriteFile(handle,_writePin.Pointer,
            checked((uint)buffer.Length),out uint bytes,_operation);
        int error=immediate ? 0 : Marshal.GetLastWin32Error();
        if(error == 997)
        {
            _writePending=true;
            if(_cancellation.IsCancellationRequested) Cancel();
            return false;
        }
        _writePin.Dispose();
        CheckWrite(error,bytes); return true;
    }
    public void CompleteWrite()
    {
        if(!_writePending) return;
        int error;
        uint bytes;
        try { error=GetOverlappedResult(_pipe.SafePipeHandle.DangerousGetHandle(),_operation,out bytes,true) ? 0 : Marshal.GetLastWin32Error(); }
        finally { _writePending=false; _writePin.Dispose(); }
        CheckWrite(error,bytes);
    }
    private void CheckWrite(int error,uint bytes)
    {
        if(error == 995 && _cancellation.IsCancellationRequested) throw new OperationCanceledException(_cancellation);
        if(error != 0) throw new IOException("Presentation input write failed.",new Win32Exception(error));
        if(bytes != _writeLength) throw new IOException("Incomplete presentation input write.");
    }
    private int ReadPinned(void* buffer,int length)
    {
        _cancellation.ThrowIfCancellationRequested();
        _completed.Reset();
        nint pipe=_pipe.SafePipeHandle.DangerousGetHandle();
        uint bytes;
        bool immediate=ReadFile(pipe,buffer,checked((uint)length),out bytes,_operation);
        int error=immediate ? 0 : Marshal.GetLastWin32Error();
        if(error == 997) // ERROR_IO_PENDING
        {
            // Close the check/submission race if cancellation ran before ReadFile.
            if(_cancellation.IsCancellationRequested) Cancel();
            if(!GetOverlappedResult(pipe,_operation,out bytes,true)) error=Marshal.GetLastWin32Error();
            else error=0;
        }
        if(error == 109) return 0; // ERROR_BROKEN_PIPE
        if(error == 995 && _cancellation.IsCancellationRequested) throw new OperationCanceledException(_cancellation);
        if(error != 0) throw new IOException("Presentation input IO failed.",new Win32Exception(error));
        return checked((int)bytes);
    }
    public void Dispose()
    {
        // Called only after the last operation returns. Registration disposal waits
        // for a racing cancellation callback before freeing its OVERLAPPED storage.
        _registration.Dispose(); NativeMemory.Free(_operation); _completed.Dispose();
    }
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(nint file,void* buffer,uint length,out uint read,NativeOverlapped* operation);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(nint file,void* buffer,uint length,out uint written,NativeOverlapped* operation);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResult(nint file,NativeOverlapped* operation,out uint transferred,[MarshalAs(UnmanagedType.Bool)] bool wait);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(nint file,NativeOverlapped* operation);
}
