using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Agwinterm.Core;

internal static unsafe partial class GhosttyNative
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ClipboardString { public byte* Data; public nuint Length; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeClipboardContent { public ClipboardString Mime,Data; }
    [LibraryImport(Library,EntryPoint="agwvt_clipboard_callback")]
    [UnmanagedCallConv(CallConvs=[typeof(CallConvCdecl)])]
    private static partial void SetClipboardCallback(nint handle,
        delegate* unmanaged[Cdecl]<nint,uint,uint,nint,NativeClipboardContent*,nuint,ClipboardString*,nuint,uint,void> callback);
    [LibraryImport(Library,EntryPoint="agwvt_clipboard_reply")]
    [UnmanagedCallConv(CallConvs=[typeof(CallConvCdecl)])]
    private static partial void ClipboardReply(nint request,uint read,uint status,NativeClipboardContent* contents,nuint count,ClipboardString* available,nuint availableCount);

    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]
    private static void OnClipboard(nint context,uint read,uint location,nint source,NativeClipboardContent* contents,nuint count,
        ClipboardString* mimes,nuint mimeCount,uint list)
    {
        ClipboardResult result=ClipboardResult.Denied;
        try
        {
            if(count > ClipboardRequest.MaximumItems || mimeCount > ClipboardRequest.MaximumItems)
                result=ClipboardResult.Invalid;
            else
            {
                var items=new ClipboardContent[(int)count]; var wanted=new string[(int)mimeCount];
                int bytes=0;
                string Mime(ClipboardString s)
                {
                    if(s.Length > 128) throw new InvalidDataException();
                    return Encoding.UTF8.GetString(new ReadOnlySpan<byte>(s.Data,(int)s.Length));
                }
                for(int i=0;i<items.Length;i++)
                {
                    if(contents[i].Data.Length > (nuint)(ClipboardRequest.MaximumBytes-bytes)) throw new InvalidDataException();
                    bytes+=(int)contents[i].Data.Length;
                    items[i]=new(Mime(contents[i].Mime),new ReadOnlySpan<byte>(contents[i].Data.Data,(int)contents[i].Data.Length).ToArray());
                }
                for(int i=0;i<wanted.Length;i++) wanted[i]=Mime(mimes[i]);
                var request=new ClipboardRequest(read != 0,(int)location,items,wanted,list != 0);
                if(!request.IsValid()) result=ClipboardResult.Invalid;
                else if(((Events)GCHandle.FromIntPtr(context).Target!).Host?.TryGetTarget(out var host) == true)
                    result=host.ClipboardExchange(request);
            }
        }
        catch(InvalidDataException) { result=ClipboardResult.Invalid; }
        catch(Exception ex) { System.Diagnostics.Trace.WriteLine("Clipboard exchange failed: "+ex.Message); result=ClipboardResult.IoError; }
        var allocations=new List<nint>();
        try
        {
            var items=result.Contents ?? []; var available=result.Available ?? [];
            if(!new ClipboardRequest(true,0,items,available).IsValid()) { result=ClipboardResult.Invalid; items=[]; available=[]; }
            ClipboardString Allocate(byte[] data)
            {
                nint p=Marshal.AllocHGlobal(Math.Max(1,data.Length)); allocations.Add(p);
                Marshal.Copy(data,0,p,data.Length); return new() {Data=(byte*)p,Length=(nuint)data.Length};
            }
            var native=new NativeClipboardContent[items.Length]; var names=new ClipboardString[available.Length];
            for(int i=0;i<items.Length;i++) native[i]=new() {Mime=Allocate(Encoding.UTF8.GetBytes(items[i].Mime)),Data=Allocate(items[i].Data)};
            for(int i=0;i<names.Length;i++) names[i]=Allocate(Encoding.UTF8.GetBytes(available[i]));
            fixed(NativeClipboardContent* ptr=native) fixed(ClipboardString* namesPtr=names)
                ClipboardReply(source,read,(uint)result.Status,ptr,(nuint)native.Length,namesPtr,(nuint)names.Length);
        }
        catch { ClipboardReply(source,read,5,null,0,null,0); }
        finally { foreach(nint p in allocations) Marshal.FreeHGlobal(p); }
    }
}
