using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Agwinterm.Core;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

internal partial class Program
{
    private const string MimeClipboardPrefix="Agwinterm MIME ";
    private static readonly UTF8Encoding ClipboardUtf8=new(false,true);
    private static readonly SemaphoreSlim TerminalClipboardGate=new(1,1);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint handle);
    [DllImport("user32.dll")] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClipboardFormatName(uint format,StringBuilder name,int size);
    private void ClipboardSet(string text)
    {
        if(!TerminalClipboardGate.Wait(0)) return;
        try { ClipboardSetCore(text); } finally { TerminalClipboardGate.Release(); }
    }
    private void ClipboardSetHtml(string plain,string fragment)
    {
        if(!TerminalClipboardGate.Wait(0)) return;
        try { ClipboardSetHtmlCore(plain,fragment); } finally { TerminalClipboardGate.Release(); }
    }
    private string ClipboardGet()
    {
        if(!TerminalClipboardGate.Wait(0)) return "";
        try { return ClipboardGetCore(); } finally { TerminalClipboardGate.Release(); }
    }

    // Called directly on a feed/effects thread: no UI dispatch or session lock.
    private ClipboardResult ExchangeTerminalClipboard(ClipboardRequest request)
    {
        if(!request.IsValid()) return ClipboardResult.Invalid;
        if(request.Read ? !_config.ClipboardRead : !_config.ClipboardWrite) return ClipboardResult.Denied;
        if(request.Location != 0) return ClipboardResult.Unsupported;
        if(!TerminalClipboardGate.Wait(0)) return ClipboardResult.Busy;
        long deadline=Environment.TickCount64+2000;
        ClipboardResult Run()
        {
            try { return ExecuteTerminalClipboard(request,deadline); }
            finally { TerminalClipboardGate.Release(); }
        }
        // Clipboard owners can delay rendering, or synchronously receive Windows
        // messages while our UI waits for the terminal lock. Keep at most one OS
        // operation in flight and always release the native caller within 2 s.
        // Writes already on the UI thread must service their own owner messages.
        if(!request.Read && Environment.CurrentManagedThreadId == _uiaThreadId) return Run();
        var pending=Task.Run(Run);
        return pending.Wait(TimeSpan.FromSeconds(2)) ? pending.Result : ClipboardResult.IoError;
    }

    private ClipboardResult ExecuteTerminalClipboard(ClipboardRequest request,long deadline)
    {
        try
        {
            if(Environment.TickCount64 >= deadline) return ClipboardResult.Busy;
            if(request.Read ? !_config.ClipboardRead : !_config.ClipboardWrite) return ClipboardResult.Denied;
            return request.Read ? ReadTerminalClipboard(request) : WriteTerminalClipboard(request,deadline);
        }
        catch(DecoderFallbackException) { return ClipboardResult.Invalid; }
        catch(InvalidDataException) { return ClipboardResult.Invalid; }
        catch(Exception ex) { System.Diagnostics.Trace.WriteLine("Windows clipboard: "+ex.Message); return ClipboardResult.IoError; }
    }

    private ClipboardResult WriteTerminalClipboard(ClipboardRequest request,long deadline)
    {
        var payloads=new Dictionary<uint,byte[]>();
        foreach(var item in request.Contents)
        {
            // A private representation keeps binary length, including empty data,
            // independent of HGLOBAL allocation padding or Windows conversions.
            byte[] stored=new byte[4+item.Data.Length];
            BinaryPrimitives.WriteInt32LittleEndian(stored,item.Data.Length); item.Data.CopyTo(stored,4);
            payloads.Add(RegisterClipboardFormat(MimeClipboardPrefix+item.Mime),stored);
            if(item.Mime is "text/plain" or "text/plain;charset=utf-8" or "text/html")
            {
                string text=ClipboardUtf8.GetString(item.Data);
                if(text.Contains('\0')) return ClipboardResult.Invalid;
                if(item.Mime == "text/html") payloads[RegisterClipboardFormat("HTML Format")]=ClipboardHtmlBytes(text);
                else payloads[CF_UNICODETEXT]=Encoding.Unicode.GetBytes(text+"\0");
            }
            else if(item.Mime == "image/png") payloads[RegisterClipboardFormat("PNG")]=item.Data.Length == 0 ? [0] : item.Data;
        }
        if(payloads.ContainsKey(0)) return ClipboardResult.IoError;
        var handles=new Dictionary<uint,nint>();
        try
        {
            foreach(var (format,data) in payloads)
            {
                nint h=GlobalAlloc(GMEM_MOVEABLE,(nuint)Math.Max(1,data.Length));
                if(h == 0) return ClipboardResult.IoError;
                handles.Add(format,h);
                nint p=GlobalLock(h); if(p == 0) return ClipboardResult.IoError;
                try { Marshal.Copy(data,0,p,data.Length); } finally { GlobalUnlock(h); }
            }
            if(!OpenClipboardRetry()) return ClipboardResult.Busy;
            try
            {
                if(Environment.TickCount64 >= deadline) return ClipboardResult.Busy;
                if(!EmptyClipboard()) return ClipboardResult.IoError;
                if(Environment.TickCount64 >= deadline) return ClipboardResult.IoError;
                foreach(var format in handles.Keys.ToArray())
                {
                    if(SetClipboardData(format,handles[format]) == 0)
                    {
                        // Other processes cannot observe partial representations
                        // while we hold OpenClipboard; discard on an OS failure.
                        EmptyClipboard(); return ClipboardResult.IoError;
                    }
                    handles[format]=0;
                }
                return new(0);
            }
            finally { CloseClipboard(); }
        }
        finally { foreach(nint h in handles.Values) if(h != 0) GlobalFree(h); }
    }

    private static byte[] ClipboardHtmlBytes(string fragment)
    {
        const string template="Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string before="<html><body><!--StartFragment-->",after="<!--EndFragment--></body></html>";
        int start=Encoding.UTF8.GetByteCount(string.Format(System.Globalization.CultureInfo.InvariantCulture,template,0,0,0,0));
        int first=start+Encoding.UTF8.GetByteCount(before),last=first+Encoding.UTF8.GetByteCount(fragment);
        return Encoding.UTF8.GetBytes(string.Format(System.Globalization.CultureInfo.InvariantCulture,template,start,last+Encoding.UTF8.GetByteCount(after),first,last)+before+fragment+after+"\0");
    }

    private ClipboardResult ReadTerminalClipboard(ClipboardRequest request)
    {
        if(!OpenClipboardRetry()) return ClipboardResult.Busy;
        try
        {
            var formats=new Dictionary<string,uint>(StringComparer.Ordinal);
            if(IsClipboardFormatAvailable(CF_UNICODETEXT)) formats["text/plain"]=CF_UNICODETEXT;
            uint html=RegisterClipboardFormat("HTML Format"),png=RegisterClipboardFormat("PNG");
            if(IsClipboardFormatAvailable(html)) formats["text/html"]=html;
            if(IsClipboardFormatAvailable(png)) formats["image/png"]=png;
            uint previous=0;
            for(int i=0;i<1024 && (previous=EnumClipboardFormats(previous)) != 0;i++)
            {
                var name=new StringBuilder(256); GetClipboardFormatName(previous,name,256);
                string value=name.ToString();
                if(value.StartsWith(MimeClipboardPrefix,StringComparison.Ordinal) && ClipboardRequest.ValidMime(value[MimeClipboardPrefix.Length..]))
                    formats[value[MimeClipboardPrefix.Length..]]=previous;
            }
            var contents=new List<ClipboardContent>(); int total=0;
            foreach(string mime in request.Mimes.Distinct(StringComparer.Ordinal))
            {
                if(!formats.TryGetValue(mime,out uint format)) continue;
                nint handle=GetClipboardData(format); if(handle == 0) return ClipboardResult.IoError;
                nuint size=GlobalSize(handle);
                if(size > ClipboardRequest.MaximumBytes*2+4096) return ClipboardResult.IoError;
                nint pointer=GlobalLock(handle); if(pointer == 0) return ClipboardResult.IoError;
                byte[] data;
                try { data=new byte[(int)size]; Marshal.Copy(pointer,data,0,data.Length); }
                finally { GlobalUnlock(handle); }
                if(format == CF_UNICODETEXT)
                {
                    if(data.Length % 2 != 0) return ClipboardResult.IoError;
                    string text=Encoding.Unicode.GetString(data); int end=text.IndexOf('\0');
                    data=ClipboardUtf8.GetBytes(end < 0 ? text : text[..end]);
                }
                else if(format == html)
                {
                    string header=Encoding.ASCII.GetString(data.AsSpan(0,Math.Min(data.Length,1024)));
                    int Offset(string key)
                    {
                        int at=header.IndexOf(key,StringComparison.Ordinal);
                        if(at < 0) throw new InvalidDataException();
                        int end=header.IndexOfAny(['\r','\n'],at);
                        return int.Parse(header.AsSpan(at+key.Length,end-at-key.Length),System.Globalization.CultureInfo.InvariantCulture);
                    }
                    int first=Offset("StartFragment:"),last=Offset("EndFragment:");
                    if(first < 0 || last < first || last > data.Length) return ClipboardResult.IoError;
                    data=data[first..last];
                }
                else if(format != png)
                {
                    if(data.Length < 4) return ClipboardResult.IoError;
                    int length=BinaryPrimitives.ReadInt32LittleEndian(data);
                    if(length < 0 || length > data.Length-4) return ClipboardResult.IoError;
                    data=data[4..(4+length)];
                }
                total+=data.Length; if(total > ClipboardRequest.MaximumBytes) return ClipboardResult.IoError;
                contents.Add(new(mime,data));
            }
            if(request.List && formats.Count > ClipboardRequest.MaximumItems) return ClipboardResult.IoError;
            return new(0,contents.ToArray(),request.List ? formats.Keys.ToArray() : []);
        }
        finally { CloseClipboard(); }
    }
}
