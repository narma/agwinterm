using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;
using System.Text.Json;

namespace Agwinterm.Core;

internal static unsafe partial class GhosttyNative
{
    internal const string Library = "agwinterm_ghostty";
    internal const string Revision = "ee02d019e8459ed4d09fac55753ad2363c5a8b01";
    private static readonly object LoadLock = new();
    private static nint _library, _upstream;
    private static bool _resolverInstalled;

    public static bool TryLoad(string directory, out string? error)
    {
        lock (LoadLock)
        {
            if (_library != 0) { error = null; return true; }
            nint upstream = 0, library = 0;
            try
            {
                // Check the pair before loading either DLL. A shim's compiled-in
                // revision alone cannot detect an accidentally replaced upstream DLL.
                using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "ghostty-vt.provenance.json")));
                var provenance = manifest.RootElement;
                if (provenance.GetProperty("upstream").GetString() != Revision || provenance.GetProperty("abi").GetInt32() != 1)
                    throw new InvalidOperationException("Ghostty build provenance does not match the expected revision/ABI.");
                foreach (string filename in new[] { "ghostty-vt.dll", "agwinterm_ghostty.dll" })
                {
                    var entries = provenance.GetProperty("binaries").EnumerateArray()
                        .Where(entry => entry.GetProperty("file").GetString() == filename).ToArray();
                    using var file = File.OpenRead(Path.Combine(directory, filename));
                    if (entries.Length != 1 || !string.Equals(entries[0].GetProperty("sha256").GetString(),
                            Convert.ToHexString(SHA256.HashData(file)), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Ghostty DLL hash mismatch; rebuild and copy both DLLs with their provenance file.");
                }
                upstream = NativeLibrary.Load(Path.GetFullPath(Path.Combine(directory, "ghostty-vt.dll")));
                library = NativeLibrary.Load(Path.GetFullPath(Path.Combine(directory, "agwinterm_ghostty.dll")));
                var abi = (delegate* unmanaged[Cdecl]<uint*, uint*, uint>)NativeLibrary.GetExport(library, "agwvt_abi");
                var revision = (delegate* unmanaged[Cdecl]<byte*>)NativeLibrary.GetExport(library, "agwvt_revision");
                uint cellSize, infoSize;
                if (abi(&cellSize, &infoSize) != 1 || cellSize != sizeof(NativeCell) || infoSize != sizeof(Info) ||
                    Marshal.PtrToStringUTF8((nint)revision()) != Revision)
                    throw new InvalidOperationException("Ghostty adapter ABI, layout or upstream revision mismatch; rebuild both DLLs together.");
                foreach (string entry in new[] { "new", "free", "feed", "resize", "info", "scrollback", "begin", "end", "cursor", "cursor_default", "colors", "cells", "clean", "abort", "read_row", "text", "marks", "anchor_new", "anchor_point", "anchor_free", "snapshot", "snapshot_free", "restore", "images", "image_pixels" })
                    _ = NativeLibrary.GetExport(library, "agwvt_" + entry);
                if (!_resolverInstalled)
                {
                    NativeLibrary.SetDllImportResolver(typeof(GhosttyNative).Assembly,
                        (name, _, _) => name == Library ? _library : 0);
                    _resolverInstalled = true;
                }
                // Successful libraries live for the process; SafeHandle finalizers may still use them.
                _upstream = upstream; _library = library;
                error = null;
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or InvalidOperationException
                or IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or FormatException)
            {
                if (library != 0) NativeLibrary.Free(library);
                if (upstream != 0) NativeLibrary.Free(upstream);
                error = ex.Message;
                return false;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeCell
    {
        public uint Rune, Fg, Bg, Attributes, Width, FgKind, FgValue, BgKind, BgValue, TextOffset, TextLength;
        public uint LinkOffset, LinkLength;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Info
    {
        public uint Cols, Rows, CursorCol, CursorRow, CursorVisible, AltScreen, History, KeyboardFlags, Modes, Background, DefaultBackground;
        public ulong ScrollEpoch;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Mark { public int Prompt, Command, Output, End, ExitCode, HasExitCode; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Image
    {
        public ulong Generation;
        public uint Id, Layer;
        public int Z, Col, Row;
        public uint OffsetX, OffsetY, Width, Height, SourceX, SourceY, SourceWidth, SourceHeight;
        public uint ImageWidth, ImageHeight, Format, DataLength;
    }
    internal sealed class Events
    {
        internal readonly Queue<(uint Kind, byte[] Data)> Pending = new();
        internal Exception? Error;
        internal int Bytes;
    }
    internal sealed class AnchorHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly Handle _owner;
        private AnchorHandle(Handle owner, nint pointer) : base(true) { _owner = owner; SetHandle(pointer); }
        internal static AnchorHandle Create(Handle owner, uint col, uint row)
        {
            bool retained = false; nint pointer = 0;
            try
            {
                owner.DangerousAddRef(ref retained);
                Check(AnchorNew(owner, col, row, out pointer));
                return new AnchorHandle(owner, pointer);
            }
            catch
            {
                if (pointer != 0) AnchorFree(pointer);
                if (retained) owner.DangerousRelease();
                throw;
            }
        }
        protected override bool ReleaseHandle()
        {
            // Native release only queues the pin. Actual destruction happens
            // under terminal ownership, without blocking the finalizer on UI locks.
            AnchorFree(handle); _owner.DangerousRelease();
            return true;
        }
    }
    internal sealed class Handle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private GCHandle _events;
        internal Handle(nint pointer, GCHandle events) : base(true) { SetHandle(pointer); _events = events; }
        protected override bool ReleaseHandle()
        {
            Free(handle);
            if (_events.IsAllocated) _events.Free();
            return true;
        }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnEvent(nint context, uint kind, byte* data, nuint length)
    {
        Events? events = null;
        try
        {
            events = (Events)GCHandle.FromIntPtr(context).Target!;
            if (events.Error is not null) return;
            if (length > 1024 * 1024 || events.Bytes + (long)length > 4 * 1024 * 1024 || events.Pending.Count >= 4096)
                throw new InvalidOperationException("Ghostty host-effect queue limit exceeded.");
            var bytes = new ReadOnlySpan<byte>(data, checked((int)length)).ToArray();
            events.Pending.Enqueue((kind, bytes)); events.Bytes += bytes.Length;
        }
        catch (Exception ex) { if (events is not null) events.Error = ex; }
    }
    internal static Handle Create(int cols, int rows, Events events)
    {
        var root = GCHandle.Alloc(events); // Roots only the queue, not its owning terminal.
        nint pointer = 0;
        try
        {
            Check(New(checked((uint)cols), checked((uint)rows), &OnEvent, GCHandle.ToIntPtr(root), &pointer));
            return new Handle(pointer, root);
        }
        catch { if (pointer != 0) Free(pointer); root.Free(); throw; }
    }
    internal static void Check(int result)
    {
        if (result != 0) throw new InvalidOperationException($"libghostty-vt operation failed ({result}).");
    }
    [LibraryImport(Library, EntryPoint = "agwvt_new")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int New(uint cols, uint rows, delegate* unmanaged[Cdecl]<nint, uint, byte*, nuint, void> callback, nint context, nint* pointer);
    [LibraryImport(Library, EntryPoint = "agwvt_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void Free(nint pointer);
    [LibraryImport(Library, EntryPoint = "agwvt_feed")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Feed(Handle handle, byte* data, nuint length);
    [LibraryImport(Library, EntryPoint = "agwvt_resize")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Resize(Handle handle, uint cols, uint rows, uint cw, uint ch);
    [LibraryImport(Library, EntryPoint = "agwvt_info")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int GetInfo(Handle handle, out Info info);
    [LibraryImport(Library, EntryPoint = "agwvt_scrollback")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Scrollback(Handle handle, nuint lines);
    [LibraryImport(Library, EntryPoint = "agwvt_begin")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Begin(Handle handle);
    [LibraryImport(Library, EntryPoint = "agwvt_end")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int End(Handle handle);
    [LibraryImport(Library, EntryPoint = "agwvt_images")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Images(Handle handle, Image* images, nuint capacity, out nuint count, uint stride);
    [LibraryImport(Library, EntryPoint = "agwvt_image_pixels")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ImagePixels(Handle handle, ulong generation, byte* pixels, nuint capacity);
    [LibraryImport(Library, EntryPoint = "agwvt_cursor")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Cursor(Handle handle, out uint col, out uint row, out uint visible, out uint shape);
    [LibraryImport(Library, EntryPoint = "agwvt_cursor_default")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int CursorDefault(Handle handle, uint style, uint blink);
    [LibraryImport(Library, EntryPoint = "agwvt_colors")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Colors(Handle handle, uint foreground, uint background);
    [LibraryImport(Library, EntryPoint = "agwvt_abort")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Abort(Handle handle);
    [LibraryImport(Library, EntryPoint = "agwvt_cells")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Cells(Handle handle, NativeCell* cells, nuint capacity, byte* dirty, int* wraps,
        nuint rows, byte* text, nuint textCapacity, out nuint textUsed);
    [LibraryImport(Library, EntryPoint = "agwvt_clean")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Clean(Handle handle);
    [LibraryImport(Library, EntryPoint = "agwvt_read_row")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ReadRow(Handle handle, uint row, uint history, NativeCell* cells, nuint capacity,
        out int wrap, byte* text, nuint textCapacity, out nuint textUsed);
    [LibraryImport(Library, EntryPoint = "agwvt_text")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Text(Handle handle, uint kind, byte* text, nuint capacity, out nuint length);
    [LibraryImport(Library, EntryPoint = "agwvt_marks")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Marks(Handle handle, Mark* marks, nuint capacity, out nuint count);
    [LibraryImport(Library, EntryPoint = "agwvt_anchor_new")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int AnchorNew(Handle handle, uint col, uint row, out nint anchor);
    [LibraryImport(Library, EntryPoint = "agwvt_anchor_point")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int AnchorPoint(AnchorHandle anchor, out uint col, out uint row);
    [LibraryImport(Library, EntryPoint = "agwvt_anchor_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void AnchorFree(nint anchor);
    [LibraryImport(Library, EntryPoint = "agwvt_snapshot")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Snapshot(Handle handle, out nint bytes, out nuint length);
    [LibraryImport(Library, EntryPoint = "agwvt_snapshot_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SnapshotFree(Handle handle, nint bytes, nuint length);
    [LibraryImport(Library, EntryPoint = "agwvt_restore")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Restore(Handle handle, byte* bytes, nuint length);
}
