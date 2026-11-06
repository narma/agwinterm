using Agwinterm.Core;
using Agwinterm.Pty;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

internal partial class Program
{
    private readonly object _ownerSaveGate=new();
    private (string Json,long Stamp,Dictionary<string,ServerSession> Sessions)? _pendingOwnerSave;
    private bool _ownerSaveRunning;
    private bool _closeSavePending;
    private bool _closeSaveComplete;
    private bool? _detachOnClose;

    // A non-final window can remain alive for its export while the other
    // windows keep processing input and paint. Destroy it only after the
    // exact captured sessions and window checkpoint are durable.
    private bool DeferCloseForOwnerHistory()
    {
        if (_closeSavePending) return true;
        if (_detachOnClose == true) return false;
        var sessions = CaptureOwnerBufferSessions();
        if (sessions.Count == 0) return false;
        foreach (var session in AllSessions()) ObserveLayout(session);
        string? json = BuildStateSnapshot(out _, captureCommands:true);
        if (json is null) return false;
        string path = StatePath;
        long stamp = _stateWriter.Reserve();
        _closeSavePending = true;
        ShowWindow(_hwnd, SW_HIDE);
        _ = Task.Run(async () =>
        {
            try
            {
                string complete = await CompleteOwnerBuffers(json,sessions).ConfigureAwait(false);
                if (!_stateWriter.Publish(path,complete,stamp,out var error))
                    throw new IOException(error ?? "Window checkpoint failed");
                Post(() =>
                {
                    _closeSaveComplete = true;
                    DestroyWindow(_hwnd);
                });
            }
            catch (Exception ex)
            {
                Post(() =>
                {
                    _closeSavePending = false;
                    _detachOnClose = null;
                    ShowWindow(_hwnd, SW_SHOW);
                    ShowToast("Window could not save its history: " + ex.Message, 10000);
                });
            }
        });
        return true;
    }

    // Capture references on UI, then query these exact sessions off UI. A pane
    // replacement cannot accidentally contribute its history to an older tree.
    private Dictionary<string,ServerSession> CaptureOwnerBufferSessions() => !_config.RestoreBuffer ? new() :
        AllSessions().SelectMany(s=>s.AllPanes).Where(p=>p.S is ServerSession { CanExportBuffer:true })
            .ToDictionary(p=>p.Id,p=>(ServerSession)p.S);

    private static async Task<string> CompleteOwnerBuffers(string json,Dictionary<string,ServerSession> sessions)
    {
        if(sessions.Count == 0) return json;
        if(!RestoreState.TryDeserialize(json,out var state) || state is null) throw new InvalidDataException("Invalid state snapshot.");
        foreach(var pane in state.Workspaces.SelectMany(w=>w.Sessions).SelectMany(s=>s.Panes))
            if(sessions.TryGetValue(pane.Id,out var session))
                pane.BufferBlob=Convert.ToBase64String(await session.ExportBufferAsync().ConfigureAwait(false));
        return RestoreState.Serialize(state);
    }

    private void QueueOwnerSave(string json,long stamp,Dictionary<string,ServerSession> sessions)
    {
        lock(_ownerSaveGate)
        {
            _pendingOwnerSave=(json,stamp,sessions);
            if(_ownerSaveRunning) return;
            _ownerSaveRunning=true;
        }
        _=Task.Run(async ()=>
        {
            while(true)
            {
                (string Json,long Stamp,Dictionary<string,ServerSession> Sessions) snapshot;
                lock(_ownerSaveGate)
                {
                    if(_pendingOwnerSave is not { } next) { _ownerSaveRunning=false; return; }
                    snapshot=next; _pendingOwnerSave=null;
                }
                try
                {
                    string complete=await CompleteOwnerBuffers(snapshot.Json,snapshot.Sessions).ConfigureAwait(false);
                    _stateWriter.Publish(StatePath,complete,snapshot.Stamp,out _);
                }
                catch(Exception ex) { System.Diagnostics.Trace.WriteLine("Owner state save failed; previous file retained: "+ex.Message); }
            }
        });
    }
}
