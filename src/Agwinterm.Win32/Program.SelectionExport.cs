using System.Runtime.InteropServices;
using System.Text;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

internal partial class Program
{
    private bool _selectionSaveDialog;
    [DllImport("comdlg32.dll",CharSet=CharSet.Unicode)]
    private static extern bool GetSaveFileNameW(ref OPENFILENAME ofn);
    [DllImport("comdlg32.dll")] private static extern uint CommDlgExtendedError();

    private async Task ExportSelection(Pane pane,int format)
    {
        try
        {
            var owner=pane.SelOwner;
            var local=pane.SelFormats;
            if(owner is not null) await owner.Ready.ConfigureAwait(false);
            var snapshot=owner?.Formats ?? local;
            string? data=snapshot?.FileContent(format) ?? (format == 0 ? owner?.Text : null);
            if(data is null) { Post(()=>ShowToast("Selection format was not captured or exceeds the export limit")); return; }
            Post(()=>
            {
                if(!ReferenceEquals(pane.SelOwner,owner) || !ReferenceEquals(pane.SelFormats,local) || !pane.HasSel || NativeDialogOpen) return;
                try
                {
                    string extension=format switch { 1=>"vt",2=>"html",_=>"txt" };
                    string? path=ChooseSelectionFile(extension);
                    if(path is not null) _=WriteSelectionFile(path,data);
                }
                catch(Exception ex) when(ex is IOException or InvalidOperationException)
                { ShowToast("Could not export selection: "+ex.Message); }
            });
        }
        catch(Exception ex) when(ex is IOException or OperationCanceledException or ObjectDisposedException)
        { Post(()=>ShowToast("Could not export selection: "+ex.Message)); }
    }

    private string? ChooseSelectionFile(string extension)
    {
        nint buffer=Marshal.AllocHGlobal(32768*2);
        _selectionSaveDialog=true;
        try
        {
            char[] name=("selection."+extension+'\0').ToCharArray();
            Marshal.Copy(name,0,buffer,name.Length);
            var dialog=new OPENFILENAME {
                lStructSize=Marshal.SizeOf<OPENFILENAME>(),hwndOwner=_hwnd,
                lpstrFilter=$"{extension.ToUpperInvariant()} files\0*.{extension}\0All files\0*.*\0\0",
                nFilterIndex=1,lpstrFile=buffer,nMaxFile=32768,lpstrDefExt=extension,
                lpstrTitle="Export terminal selection",Flags=OFN_PATHMUSTEXIST|OFN_EXPLORER|0x2|0x8 // overwrite prompt, no directory change
            };
            if(GetSaveFileNameW(ref dialog)) return Marshal.PtrToStringUni(buffer);
            uint error=CommDlgExtendedError();
            if(error != 0) throw new IOException($"Save dialog failed ({error:X})");
            return null;
        }
        finally { _selectionSaveDialog=false; Marshal.FreeHGlobal(buffer); }
    }

    private async Task WriteSelectionFile(string path,string data)
    {
        string? temporary=null;
        try
        {
            // Publish only the complete UTF-8 file. Failed writes preserve an
            // existing destination, and no shell interprets the chosen path.
            temporary=Path.Combine(Path.GetDirectoryName(path)!,".agwinterm-"+Guid.NewGuid().ToString("N")+".tmp");
            await File.WriteAllTextAsync(temporary,data,new UTF8Encoding(false)).ConfigureAwait(false);
            File.Move(temporary,path,true);
            temporary=null;
            Post(()=>ShowToast("Selection exported",1200));
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { Post(()=>ShowToast("Could not export selection: "+ex.Message)); }
        finally
        {
            if(temporary is not null)
                try { File.Delete(temporary); } catch(IOException) { } catch(UnauthorizedAccessException) { }
        }
    }
}
