using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Win32;

internal partial class Program
{
    private readonly object _ownerSaveGate=new();
    private (string Json,long Stamp,Dictionary<string,ServerSession> Sessions)? _pendingOwnerSave;
    private bool _ownerSaveRunning;

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
