using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Agwinterm.Pty;

internal static partial class PipeReadAvailability
{
    // Called only by the pipe's single reader, after its previous read completed.
    // Never wait for another chunk while holding the terminal's ingestion lock.
    internal static bool HasData(Stream stream)
    {
        SafeHandle? handle = stream switch
        {
            FileStream file => file.SafeFileHandle,
            PipeStream pipe => pipe.SafePipeHandle,
            _ => null
        };
        return handle is not null && PeekNamedPipe(handle, 0, 0, 0, out uint available, 0) != 0 && available != 0;
    }

    [LibraryImport("kernel32.dll")]
    private static partial int PeekNamedPipe(SafeHandle pipe, nint buffer, uint size, nint read, out uint available, nint remaining);
}
