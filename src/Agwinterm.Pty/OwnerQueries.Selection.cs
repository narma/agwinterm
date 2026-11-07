using System.Text.Json;
using Agwinterm.Core;

namespace Agwinterm.Pty;

public sealed record TerminalSelectionRequest(string Id,int AnchorRow,int AnchorColumn,int FocusRow,int FocusColumn,bool Block,
    bool All=false,bool AlternateScreen=false,string? Unit=null,string Delimiters="",
    string? GestureId=null,int GestureBehavior=0,int GestureRow=0,int GestureColumn=0);
public sealed record TerminalSelectionResult(string Id,bool Valid,int AnchorRow,int AnchorColumn,int FocusRow,int FocusColumn,string? Text);

internal sealed partial class OwnerQueries
{
    private GhosttySelection? _selection;
    private string? _selectionId;
    private bool _disposed;
    private GhosttySelectionGesture? _gesture;
    private string? _gestureId;

    private TerminalQueryResult ReadSelection(TerminalQuery query,CancellationToken cancellation)
    {
        TerminalQueryResult Result(string status,long generation=0,TerminalSelectionResult? value=null)
            => new(incarnation,epoch,generation,status,value is null ? [] : [JsonSerializer.Serialize(value)]);
        TerminalSelectionRequest? request;
        try { request=JsonSerializer.Deserialize<TerminalSelectionRequest>(query.Text); }
        catch(JsonException) { return Result("invalid"); }
        if(request is null || request.Delimiters is null || request.Delimiters.Length > 4094 ||
            !Guid.TryParseExact(request.Id,"N",out _)) return Result("invalid");
        lock(session.SyncRoot)
        {
            cancellation.ThrowIfCancellationRequested();
            if(_disposed) return Result("closed");
            var core=(GhosttyTerminalCore)session.Emulator;
            if(query.Operation == "selection-release")
            {
                if(_selectionId == request.Id) ReleaseSelection();
                return Result("ok",core.Generation);
            }
            if(query.Operation == "selection-create")
            {
                int first=core.IsAltScreen ? core.HistoryCount : 0,last=core.HistoryCount+core.Rows-1;
                // Select-all is an operation on the current buffer, not stale
                // coordinates from the last painted frame. Never cross screens.
                if(request.All)
                {
                    if(core.IsAltScreen != request.AlternateScreen) return Result("stale",core.Generation);
                    request=request with { AnchorRow=first,AnchorColumn=0,FocusRow=last,FocusColumn=core.Cols-1,Block=false };
                }
                else if(core.Generation != query.Generation) return Result("stale",core.Generation);
                if(request.Unit is not null)
                {
                    int kind=request.Unit switch { "word" => 0, "line" => 1, "output" => 2, _ => -1 };
                    if(kind < 0 || request.AnchorRow < first || request.AnchorRow > last ||
                        request.AnchorColumn < 0 || request.AnchorColumn >= core.Cols) return Result("invalid",core.Generation);
                    if(!core.SelectBounds(kind,request.AnchorRow,request.AnchorColumn,request.Delimiters,
                        out int startRow,out int startCol,out int endRow,out int endCol)) return Result("ok",core.Generation,
                            new(request.Id,false,0,0,0,0,null));
                    request=request with { AnchorRow=startRow,AnchorColumn=startCol,FocusRow=endRow,FocusColumn=endCol,Block=false };
                }
                if(request.AnchorRow < first || request.FocusRow < first || request.AnchorRow > last || request.FocusRow > last ||
                    request.AnchorColumn < 0 || request.FocusColumn < 0 || request.AnchorColumn >= core.Cols || request.FocusColumn >= core.Cols)
                    return Result("invalid",core.Generation);
                if(request.GestureId is { } gestureId)
                {
                    if(!Guid.TryParseExact(gestureId,"N",out _) || request.GestureBehavior is < 0 or > 2)
                        return Result("invalid",core.Generation);
                    if(_gestureId != gestureId)
                    {
                        if(request.GestureRow < first || request.GestureRow > last || request.GestureColumn < 0 || request.GestureColumn >= core.Cols)
                            return Result("invalid",core.Generation);
                        _gesture?.Dispose();
                        _gesture=new(core,request.GestureRow,request.GestureColumn,request.GestureBehavior,request.Delimiters);
                        _gestureId=gestureId;
                    }
                    if(!_gesture!.Update(request.FocusRow,request.FocusColumn,request.Block,out int gar,out int gac,out int gfr,out int gfc))
                        return Result("stale",core.Generation);
                    request=request with { AnchorRow=gar,AnchorColumn=gac,FocusRow=gfr,FocusColumn=gfc };
                }
                GhosttySelection next;
                try { next=core.CreateSelection(request.AnchorRow,request.AnchorColumn,request.FocusRow,request.FocusColumn,request.Block,query.ResultLimit); }
                catch(InvalidDataException) { return Result("limit",core.Generation); }
                ReleaseSelection(); _selection=next; _selectionId=request.Id;
                return Result("ok",core.Generation,new(request.Id,true,request.AnchorRow,request.AnchorColumn,
                    request.FocusRow,request.FocusColumn,next.Text));
            }
            if(query.Operation is not ("selection-locate" or "selection-format")) return Result("unsupported",core.Generation);
            if(_selectionId != request.Id || _selection is null) return Result("stale",core.Generation);
            bool valid=_selection.TryLocate(out int ar,out int ac,out int fr,out int fc);
            if(!valid) ReleaseSelection();
            if(query.Operation == "selection-format")
            {
                if(!valid) return Result("stale",core.Generation);
                if(query.RowCount is < 0 or > 2) return Result("invalid",core.Generation);
                try { return Result("ok",core.Generation,new(request.Id,true,ar,ac,fr,fc,
                    core.FormatSelection(ar,ac,fr,fc,request.Block,query.RowCount,query.ResultLimit))); }
                catch(InvalidDataException) { return Result("limit",core.Generation); }
            }
            return Result("ok",core.Generation,new(request.Id,valid,ar,ac,fr,fc,null));
        }
    }

    private void ReleaseSelection() { _selection?.Dispose(); _selection=null; _selectionId=null; }
    public void Dispose() { lock(session.SyncRoot) { _disposed=true; ReleaseSelection(); _gesture?.Dispose(); _gesture=null; } }
}
