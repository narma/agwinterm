using Agwinterm.Core;

namespace Agwinterm.Win32;

internal partial class Program
{
    private void SelectCommandOutput(Pane pane,bool copy)
    {
        void Unavailable() => ShowToast("No command output here; shell integration marks are required");
        void Finish()
        {
            if(string.IsNullOrWhiteSpace(SelectionText(pane))) { pane.ClearSel(); Unavailable(); }
            else if(copy && CopySelection(pane,clear:false)) ShowToast("Command output copied",500);
            RequestRedraw();
        }
        try
        {
            bool finishNow=false;
            lock(pane.S.SyncRoot)
            {
                var core=pane.S.Emulator;
                if(core.IsAltScreen || core.SynchronizedOutput) { Unavailable(); return; }
                bool latest=!HasLiveSel(pane);
                if(!latest && !pane.SelContentVisible) { ShowToast("Selected command is no longer visible; clear the selection to use the latest command"); return; }
                int row=latest ? core.HistoryCount+core.CursorRow : pane.SelFocLine;
                int col=latest ? core.CursorCol : pane.SelFocCol;
                if(core is PresentedTerminalView)
                {
                    pane.ClearSel();
                    pane.SelAncLine=pane.SelFocLine=row; pane.SelAncCol=pane.SelFocCol=col;
                    pane.BlockSel=false; pane.HasSel=true;
                    StampSelection(pane,false,latest ? "last-output" : "output");
                    if(pane.SelOwner is not { } owner) { pane.ClearSel(); Unavailable(); return; }
                    _=owner.Ready.ContinueWith(_=>Post(()=>
                    {
                        if(!ReferenceEquals(pane.SelOwner,owner)) return;
                        Finish();
                    }),TaskScheduler.Default);
                }
                else if(core is GhosttyTerminalCore native && native.SelectCommandOutput(row,col,latest,out int ar,out int ac,out int fr,out int fc))
                {
                    pane.ClearSel();
                    pane.SelAncLine=ar; pane.SelAncCol=ac; pane.SelFocLine=fr; pane.SelFocCol=fc;
                    pane.BlockSel=false; pane.HasSel=true; StampSel(pane);
                    finishNow=true;
                }
                else Unavailable();
            }
            if(finishNow) Finish();
        }
        catch(Exception ex) when(ex is InvalidDataException or InvalidOperationException or ObjectDisposedException)
        { ShowToast("Could not select command output: "+ex.Message); }
        RequestRedraw();
    }
}
