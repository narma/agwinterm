using System.Runtime.InteropServices;
using System.Text;
using Agwinterm.Core;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

internal partial class Program
{
    private async Task CopyFormattedSelection(Pane pane,int format)
    {
        try
        {
            var owner=pane.SelOwner;
            var local=pane.SelFormats;
            if(owner is not null) await owner.Ready.ConfigureAwait(false);
            var snapshot=owner?.Formats ?? local;
            string? formatted=snapshot?.Get(format);
            if(formatted is null) { Post(()=>ShowToast("Formatting was not captured or exceeds the export limit")); return; }
            string plain=snapshot!.Text;
            Post(()=>
            {
                if(!ReferenceEquals(pane.SelOwner,owner) || !ReferenceEquals(pane.SelFormats,local) || !pane.HasSel) return;
                if(format == 1) ClipboardSet(formatted);
                else ClipboardSetHtml(plain,formatted);
            });
        }
        catch(Exception ex) when(ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        { Post(()=>ShowToast("Could not export selection: "+ex.Message)); }
    }

    [DllImport("user32.dll",CharSet=CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string name);

    private bool ClipboardSetHtmlCore(string plain,string fragment)
    {
        const string template="Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string before="<html><body><!--StartFragment-->", after="<!--EndFragment--></body></html>";
        int start=Encoding.UTF8.GetByteCount(string.Format(System.Globalization.CultureInfo.InvariantCulture,template,0,0,0,0));
        int first=start+Encoding.UTF8.GetByteCount(before), last=first+Encoding.UTF8.GetByteCount(fragment);
        string header=string.Format(System.Globalization.CultureInfo.InvariantCulture,template,start,last+Encoding.UTF8.GetByteCount(after),first,last);
        uint htmlFormat=RegisterClipboardFormat("HTML Format");
        if(htmlFormat == 0) return false;
        var payloads=new[] { (CF_UNICODETEXT,Encoding.Unicode.GetBytes(plain+"\0")),
            (htmlFormat,Encoding.UTF8.GetBytes(header+before+fragment+after+"\0")) };
        var handles=new nint[payloads.Length];
        try
        {
            for(int i=0;i<payloads.Length;i++)
            {
                byte[] data=payloads[i].Item2;
                handles[i]=GlobalAlloc(GMEM_MOVEABLE,(nuint)data.Length);
                if(handles[i] == 0) return false;
                nint pointer=GlobalLock(handles[i]);
                if(pointer == 0) return false;
                try { Marshal.Copy(data,0,pointer,data.Length); } finally { GlobalUnlock(handles[i]); }
            }
            if(!OpenClipboardRetry()) return false;
            try
            {
                if(!EmptyClipboard()) return false;
                for(int i=0;i<handles.Length;i++)
                {
                    if(SetClipboardData(payloads[i].Item1,handles[i]) == 0) { EmptyClipboard(); return false; }
                    handles[i]=0;
                }
                return true;
            }
            finally { CloseClipboard(); }
        }
        finally { foreach(nint handle in handles) if(handle != 0) GlobalFree(handle); }
    }
}
